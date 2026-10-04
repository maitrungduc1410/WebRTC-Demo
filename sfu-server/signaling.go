package main

import (
	"encoding/json"
	"log"
	"net/http"
	"strings"
	"sync"
	"time"

	"github.com/gorilla/websocket"
	"github.com/pion/webrtc/v4"
)

// Messages, see "Group call (SFU)" in ARCHITECTURE.md.

type MediaState struct {
	Audio  bool `json:"audio"`
	Video  bool `json:"video"`
	Screen bool `json:"screen"`
}

type ParticipantInfo struct {
	ID    string     `json:"id"`
	Name  string     `json:"name"`
	State MediaState `json:"state"`
}

// flexString accepts a JSON string or number, like the Node server's roomId.toString().
type flexString string

func (s *flexString) UnmarshalJSON(b []byte) error {
	var str string
	if err := json.Unmarshal(b, &str); err == nil {
		*s = flexString(str)
		return nil
	}
	var num json.Number
	if err := json.Unmarshal(b, &num); err != nil {
		return err
	}
	*s = flexString(num.String())
	return nil
}

type inMessage struct {
	Type      string                   `json:"type"`
	RoomID    flexString               `json:"roomId"`
	Name      string                   `json:"name"`
	E2EE      bool                     `json:"e2ee"`
	E2EEKey   string                   `json:"e2eeKey"`
	PC        string                   `json:"pc"`
	SDP       string                   `json:"sdp"`
	Candidate *webrtc.ICECandidateInit `json:"candidate"`
	State     *MediaState              `json:"state"`
	Text      string                   `json:"text"`
}

type joinedMessage struct {
	Type          string            `json:"type"`
	ParticipantID string            `json:"participantId"`
	Participants  []ParticipantInfo `json:"participants"`
	E2EE          bool              `json:"e2ee"`
	E2EEKey       string            `json:"e2eeKey,omitempty"`
}

type sdpMessage struct {
	Type string `json:"type"`
	PC   string `json:"pc"`
	SDP  string `json:"sdp"`
}

type participantJoinedMessage struct {
	Type        string          `json:"type"`
	Participant ParticipantInfo `json:"participant"`
}

type participantLeftMessage struct {
	Type          string `json:"type"`
	ParticipantID string `json:"participantId"`
}

type mediaStateMessage struct {
	Type          string     `json:"type"`
	ParticipantID string     `json:"participantId"`
	State         MediaState `json:"state"`
}

type chatMessage struct {
	Type          string `json:"type"`
	ParticipantID string `json:"participantId"`
	Name          string `json:"name"`
	Text          string `json:"text"`
}

type errorMessage struct {
	Type    string `json:"type"`
	Message string `json:"message"`
	Fatal   bool   `json:"fatal"`
}

const (
	pcPublish   = "publish"
	pcSubscribe = "subscribe"

	maxMessageSize = 1 << 20
	maxChatLength  = 4000
	pingInterval   = 20 * time.Second
	readTimeout    = 60 * time.Second
	writeTimeout   = 10 * time.Second
	sendQueueSize  = 256
)

var upgrader = websocket.Upgrader{
	// Demo: any page may connect, like the Node server's `cors: { origin: "*" }`.
	CheckOrigin: func(*http.Request) bool { return true },
}

// connection serializes writes to one WebSocket. Messages are queued so that
// callers holding a room lock never block on the network.
type connection struct {
	ws        *websocket.Conn
	send      chan []byte
	done      chan struct{}
	closeOnce sync.Once
}

func newConnection(ws *websocket.Conn) *connection {
	c := &connection{ws: ws, send: make(chan []byte, sendQueueSize), done: make(chan struct{})}
	go c.writeLoop()
	return c
}

func (c *connection) sendJSON(v any) {
	b, err := json.Marshal(v)
	if err != nil {
		log.Printf("marshal: %v", err)
		return
	}
	c.enqueue(b)
}

// enqueue drops a client that cannot keep up instead of blocking the room.
func (c *connection) enqueue(b []byte) {
	select {
	case <-c.done:
	case c.send <- b:
	default:
		log.Printf("send queue full, closing connection")
		c.close()
	}
}

func (c *connection) sendError(message string, fatal bool) {
	c.sendJSON(errorMessage{Type: "error", Message: message, Fatal: fatal})
	if fatal {
		// A nil message tells writeLoop to close after the queued ones are written.
		c.enqueue(nil)
	}
}

func (c *connection) close() {
	c.closeOnce.Do(func() {
		close(c.done)
		_ = c.ws.Close()
	})
}

func (c *connection) writeLoop() {
	ticker := time.NewTicker(pingInterval)
	defer ticker.Stop()
	defer c.close()
	for {
		select {
		case <-c.done:
			return
		case b := <-c.send:
			_ = c.ws.SetWriteDeadline(time.Now().Add(writeTimeout))
			if b == nil {
				_ = c.ws.WriteMessage(websocket.CloseMessage, websocket.FormatCloseMessage(websocket.CloseNormalClosure, ""))
				return
			}
			if err := c.ws.WriteMessage(websocket.TextMessage, b); err != nil {
				return
			}
		case <-ticker.C:
			_ = c.ws.SetWriteDeadline(time.Now().Add(writeTimeout))
			if err := c.ws.WriteMessage(websocket.PingMessage, nil); err != nil {
				return
			}
		}
	}
}

func (s *server) handleWebSocket(w http.ResponseWriter, r *http.Request) {
	ws, err := upgrader.Upgrade(w, r, nil)
	if err != nil {
		return
	}
	c := newConnection(ws)
	defer c.close()

	ws.SetReadLimit(maxMessageSize)
	_ = ws.SetReadDeadline(time.Now().Add(readTimeout))
	ws.SetPongHandler(func(string) error {
		return ws.SetReadDeadline(time.Now().Add(readTimeout))
	})

	var p *participant
	defer func() {
		if p != nil {
			s.leave(p)
		}
	}()

	for {
		_, data, err := ws.ReadMessage()
		if err != nil {
			return
		}
		_ = ws.SetReadDeadline(time.Now().Add(readTimeout))

		var msg inMessage
		if err := json.Unmarshal(data, &msg); err != nil {
			c.sendError("Invalid message", false)
			continue
		}

		if p == nil {
			if msg.Type != "join" {
				c.sendError("Join a room first", false)
				continue
			}
			p = s.join(c, msg)
			if p == nil {
				// Let writeLoop deliver the fatal error before the socket closes.
				select {
				case <-c.done:
				case <-time.After(writeTimeout):
				}
				return
			}
			continue
		}

		switch msg.Type {
		case "offer":
			p.handlePublishOffer(msg)
		case "answer":
			p.handleSubscribeAnswer(msg)
		case "candidate":
			p.handleCandidate(msg)
		case "media state":
			if msg.State != nil {
				p.room.setMediaState(p, *msg.State)
			}
		case "chat":
			if text := strings.TrimSpace(msg.Text); text != "" && len(text) <= maxChatLength {
				p.room.broadcast(p, chatMessage{Type: "chat", ParticipantID: p.id, Name: p.name, Text: text})
			}
		case "leave":
			return
		case "join":
			c.sendError("Already in a room", false)
		}
	}
}
