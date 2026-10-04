package main

import (
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/gorilla/websocket"
	"github.com/pion/rtp"
	"github.com/pion/webrtc/v4"
)

// These tests run real Pion clients against the SFU over loopback: they
// publish synthetic RTP and check what every other participant receives.

const waitTimeout = 15 * time.Second

func startServer(t *testing.T, maxParticipants int) string {
	t.Helper()
	api, err := newWebRTCAPI(apiConfig{UDPPort: 0, Loopback: true})
	if err != nil {
		t.Fatal(err)
	}
	s := newServer(api, maxParticipants)
	mux := http.NewServeMux()
	mux.HandleFunc("/ws", s.handleWebSocket)
	ts := httptest.NewServer(mux)
	t.Cleanup(ts.Close)
	return "ws" + strings.TrimPrefix(ts.URL, "http") + "/ws"
}

type testClient struct {
	t    *testing.T
	name string
	api  *webrtc.API
	// newAPI, when set, gives each PeerConnection its own sockets, like a browser.
	newAPI func() *webrtc.API

	wsMu sync.Mutex
	ws   *websocket.Conn

	id      string
	joined  map[string]any
	pub     *webrtc.PeerConnection
	sub     *webrtc.PeerConnection
	events  chan map[string]any
	stop    chan struct{}
	stopped sync.Once

	mu       sync.Mutex
	received map[string]int    // "<participantId>/<kind>" -> packets
	trackIDs map[string]string // track id -> "<participantId>/<kind>"
}

func newClientAPI(t *testing.T) *webrtc.API {
	m := &webrtc.MediaEngine{}
	if err := m.RegisterDefaultCodecs(); err != nil {
		t.Fatal(err)
	}
	se := webrtc.SettingEngine{}
	se.SetIncludeLoopbackCandidate(true)
	se.SetNetworkTypes([]webrtc.NetworkType{webrtc.NetworkTypeUDP4})
	return webrtc.NewAPI(webrtc.WithMediaEngine(m), webrtc.WithSettingEngine(se))
}

// dial connects and sends join; it returns the first server message.
func dial(t *testing.T, url string, join map[string]any) (*testClient, map[string]any) {
	t.Helper()
	ws, _, err := websocket.DefaultDialer.Dial(url, nil)
	if err != nil {
		t.Fatal(err)
	}
	c := &testClient{
		t: t, ws: ws, api: newClientAPI(t),
		events: make(chan map[string]any, 100), stop: make(chan struct{}),
		received: map[string]int{},
		trackIDs: map[string]string{},
	}
	t.Cleanup(c.leave)
	join["type"] = "join"
	c.send(join)
	var first map[string]any
	if err := ws.ReadJSON(&first); err != nil {
		t.Fatal(err)
	}
	return c, first
}

// join dials, expects `joined` and starts publishing audio and video.
func join(t *testing.T, url, roomID, name string) *testClient {
	t.Helper()
	c, first := dial(t, url, map[string]any{"roomId": roomID, "name": name, "e2ee": false})
	if first["type"] != "joined" {
		t.Fatalf("%s: expected joined, got %v", name, first)
	}
	c.name = name
	c.id = first["participantId"].(string)
	c.joined = first
	go c.readLoop()
	c.publish()
	return c
}

func (c *testClient) send(v any) {
	c.wsMu.Lock()
	defer c.wsMu.Unlock()
	_ = c.ws.WriteJSON(v)
}

func (c *testClient) onCandidate(pc string) func(*webrtc.ICECandidate) {
	return func(cand *webrtc.ICECandidate) {
		if cand != nil {
			c.send(map[string]any{"type": "candidate", "pc": pc, "candidate": cand.ToJSON()})
		}
	}
}

func (c *testClient) pcAPI() *webrtc.API {
	if c.newAPI != nil {
		return c.newAPI()
	}
	return c.api
}

func (c *testClient) publish() {
	pc, err := c.pcAPI().NewPeerConnection(webrtc.Configuration{})
	if err != nil {
		c.t.Fatal(err)
	}
	c.pub = pc
	pc.OnICECandidate(c.onCandidate("publish"))

	audio, _ := webrtc.NewTrackLocalStaticRTP(webrtc.RTPCodecCapability{MimeType: webrtc.MimeTypeOpus, ClockRate: 48000, Channels: 2}, "audio", "local")
	video, _ := webrtc.NewTrackLocalStaticRTP(webrtc.RTPCodecCapability{MimeType: webrtc.MimeTypeVP8, ClockRate: 90000}, "video", "local")
	for _, track := range []*webrtc.TrackLocalStaticRTP{audio, video} {
		if _, err := pc.AddTransceiverFromTrack(track, webrtc.RTPTransceiverInit{Direction: webrtc.RTPTransceiverDirectionSendonly}); err != nil {
			c.t.Fatal(err)
		}
	}
	offer, err := pc.CreateOffer(nil)
	if err != nil {
		c.t.Fatal(err)
	}
	if err := pc.SetLocalDescription(offer); err != nil {
		c.t.Fatal(err)
	}
	c.send(map[string]any{"type": "offer", "pc": "publish", "sdp": offer.SDP})

	go func() {
		ticker := time.NewTicker(20 * time.Millisecond)
		defer ticker.Stop()
		var seq uint16
		for {
			select {
			case <-c.stop:
				return
			case <-ticker.C:
				seq++
				_ = audio.WriteRTP(&rtp.Packet{Header: rtp.Header{Version: 2, SequenceNumber: seq, Timestamp: uint32(seq) * 960}, Payload: []byte{0xfc, 0x01, 0x02}})
				_ = video.WriteRTP(&rtp.Packet{Header: rtp.Header{Version: 2, SequenceNumber: seq, Timestamp: uint32(seq) * 3000, Marker: true}, Payload: []byte{0x10, 0x00, 0x9d, 0x01, 0x2a}})
			}
		}
	}()
}

func (c *testClient) readLoop() {
	for {
		var msg map[string]any
		if err := c.ws.ReadJSON(&msg); err != nil {
			close(c.events)
			return
		}
		switch {
		case msg["type"] == "answer" && msg["pc"] == "publish":
			if err := c.pub.SetRemoteDescription(webrtc.SessionDescription{Type: webrtc.SDPTypeAnswer, SDP: msg["sdp"].(string)}); err != nil {
				c.t.Errorf("%s: publish answer: %v", c.name, err)
			}
		case msg["type"] == "offer" && msg["pc"] == "subscribe":
			c.answerSubscribe(msg["sdp"].(string))
		case msg["type"] == "candidate":
			c.t.Errorf("%s: the server must not trickle candidates", c.name)
		default:
			c.events <- msg
		}
	}
}

func (c *testClient) answerSubscribe(sdp string) {
	if c.sub == nil {
		pc, err := c.pcAPI().NewPeerConnection(webrtc.Configuration{})
		if err != nil {
			c.t.Fatal(err)
		}
		c.sub = pc
		pc.OnICECandidate(c.onCandidate("subscribe"))
		pc.OnTrack(func(track *webrtc.TrackRemote, _ *webrtc.RTPReceiver) {
			key := track.StreamID() + "/" + track.Kind().String()
			c.mu.Lock()
			if other, ok := c.trackIDs[track.ID()]; ok && other != key {
				c.t.Errorf("%s: track id %q is used by %s and %s", c.name, track.ID(), other, key)
			}
			c.trackIDs[track.ID()] = key
			c.mu.Unlock()
			for {
				if _, _, err := track.ReadRTP(); err != nil {
					return
				}
				c.mu.Lock()
				c.received[key]++
				c.mu.Unlock()
			}
		})
	}
	if err := c.sub.SetRemoteDescription(webrtc.SessionDescription{Type: webrtc.SDPTypeOffer, SDP: sdp}); err != nil {
		c.t.Errorf("%s: subscribe offer: %v", c.name, err)
		return
	}
	answer, err := c.sub.CreateAnswer(nil)
	if err != nil {
		c.t.Errorf("%s: subscribe answer: %v", c.name, err)
		return
	}
	if err := c.sub.SetLocalDescription(answer); err != nil {
		c.t.Errorf("%s: set subscribe answer: %v", c.name, err)
		return
	}
	c.send(map[string]any{"type": "answer", "pc": "subscribe", "sdp": answer.SDP})
}

func (c *testClient) packets(from *testClient, kind string) int {
	c.mu.Lock()
	defer c.mu.Unlock()
	return c.received[from.id+"/"+kind]
}

// waitForMedia waits until c receives fresh audio and video packets from each of from.
func (c *testClient) waitForMedia(from ...*testClient) {
	c.t.Helper()
	start := map[string]int{}
	for _, f := range from {
		for _, kind := range []string{"audio", "video"} {
			start[f.id+kind] = c.packets(f, kind)
		}
	}
	deadline := time.Now().Add(waitTimeout)
	for {
		missing := ""
		for _, f := range from {
			for _, kind := range []string{"audio", "video"} {
				if c.packets(f, kind)-start[f.id+kind] < 10 {
					missing = f.name + " " + kind
				}
			}
		}
		if missing == "" {
			return
		}
		if time.Now().After(deadline) {
			c.t.Fatalf("%s: no %s received", c.name, missing)
		}
		time.Sleep(50 * time.Millisecond)
	}
}

func (c *testClient) waitForEvent(msgType string, match func(map[string]any) bool) map[string]any {
	c.t.Helper()
	timeout := time.After(waitTimeout)
	for {
		select {
		case msg, ok := <-c.events:
			if !ok {
				c.t.Fatalf("%s: socket closed while waiting for %q", c.name, msgType)
			}
			if msg["type"] == msgType && (match == nil || match(msg)) {
				return msg
			}
		case <-timeout:
			c.t.Fatalf("%s: timed out waiting for %q", c.name, msgType)
		}
	}
}

func (c *testClient) leave() {
	c.stopped.Do(func() {
		close(c.stop)
		c.send(map[string]any{"type": "leave"})
		_ = c.ws.Close()
		if c.pub != nil {
			_ = c.pub.Close()
		}
		if c.sub != nil {
			_ = c.sub.Close()
		}
	})
}

func TestForwardsMediaBetweenParticipants(t *testing.T) {
	url := startServer(t, 8)

	a := join(t, url, "room", "A")
	b := join(t, url, "room", "B")
	c := join(t, url, "room", "C")

	if got := len(c.joined["participants"].([]any)); got != 2 {
		t.Fatalf("C: expected 2 participants in joined, got %d", got)
	}
	a.waitForEvent("participant joined", func(m map[string]any) bool {
		return m["participant"].(map[string]any)["id"] == c.id
	})

	a.waitForMedia(b, c)
	b.waitForMedia(a, c)
	c.waitForMedia(a, b)

	// B leaves: the others are told, and the rest of the call keeps flowing.
	b.leave()
	for _, o := range []*testClient{a, c} {
		o.waitForEvent("participant left", func(m map[string]any) bool { return m["participantId"] == b.id })
	}
	a.waitForMedia(c)
	c.waitForMedia(a)

	// D joins after a renegotiation that removed B's tracks.
	d := join(t, url, "room", "D")
	d.waitForMedia(a, c)
	a.waitForMedia(c, d)
	c.waitForMedia(a, d)
}

func TestRelaysMediaStateAndChat(t *testing.T) {
	url := startServer(t, 8)
	a := join(t, url, "42", "A")
	b := join(t, url, "42", "B")

	a.send(map[string]any{"type": "media state", "state": map[string]any{"audio": false, "video": true, "screen": true}})
	msg := b.waitForEvent("media state", nil)
	if msg["participantId"] != a.id || msg["state"].(map[string]any)["audio"] != false || msg["state"].(map[string]any)["screen"] != true {
		t.Fatalf("unexpected media state: %v", msg)
	}

	a.send(map[string]any{"type": "chat", "text": "hello"})
	msg = b.waitForEvent("chat", nil)
	if msg["participantId"] != a.id || msg["name"] != "A" || msg["text"] != "hello" {
		t.Fatalf("unexpected chat: %v", msg)
	}

	// A late joiner gets the current state in `joined`.
	c := join(t, url, "42", "C")
	for _, p := range c.joined["participants"].([]any) {
		info := p.(map[string]any)
		if info["id"] == a.id && info["state"].(map[string]any)["screen"] != true {
			t.Fatalf("joined has stale state: %v", info)
		}
	}
}

func TestRejectsFullRoomAndE2EEMismatch(t *testing.T) {
	url := startServer(t, 2)
	join(t, url, "r", "A")
	join(t, url, "r", "B")

	_, msg := dial(t, url, map[string]any{"roomId": "r", "name": "C", "e2ee": false})
	if msg["type"] != "error" || msg["fatal"] != true || msg["message"] != "Room is full" {
		t.Fatalf("expected room full, got %v", msg)
	}

	_, msg = dial(t, url, map[string]any{"roomId": "e", "name": "A", "e2ee": true, "e2eeKey": "a2V5LTE="})
	if msg["type"] != "joined" || msg["e2ee"] != true || msg["e2eeKey"] != "a2V5LTE=" {
		t.Fatalf("expected joined with the creator's key, got %v", msg)
	}
	_, msg = dial(t, url, map[string]any{"roomId": "e", "name": "B", "e2ee": true, "e2eeKey": "a2V5LTI="})
	if msg["type"] != "joined" || msg["e2eeKey"] != "a2V5LTE=" {
		t.Fatalf("expected the room key, got %v", msg)
	}
	_, msg = dial(t, url, map[string]any{"roomId": "e", "name": "C", "e2ee": false})
	if msg["type"] != "error" || msg["fatal"] != true {
		t.Fatalf("expected E2EE mismatch, got %v", msg)
	}
}

func TestRoomIDMayBeANumber(t *testing.T) {
	var msg inMessage
	if err := json.Unmarshal([]byte(`{"type":"join","roomId":123}`), &msg); err != nil || msg.RoomID != "123" {
		t.Fatalf("got %q, %v", msg.RoomID, err)
	}
}
