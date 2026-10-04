package main

import (
	"crypto/rand"
	"encoding/base64"
	"encoding/hex"
	"log"
	"strings"
	"sync"

	"github.com/pion/webrtc/v4"
)

type server struct {
	api             *webrtc.API
	maxParticipants int

	// Lock order: server.mu, then room.mu.
	mu    sync.Mutex
	rooms map[string]*room
}

func newServer(api *webrtc.API, maxParticipants int) *server {
	return &server{api: api, maxParticipants: maxParticipants, rooms: map[string]*room{}}
}

type room struct {
	id      string
	e2ee    bool
	e2eeKey string

	mu           sync.Mutex
	participants []*participant // in join order
}

// join validates the request, adds the participant to its room and subscribes
// it to every track already published there. It returns nil after sending a
// fatal error.
func (s *server) join(c *connection, msg inMessage) *participant {
	roomID := strings.TrimSpace(string(msg.RoomID))
	if roomID == "" {
		c.sendError("Missing roomId", true)
		return nil
	}
	if msg.E2EE {
		if key, err := base64.StdEncoding.DecodeString(msg.E2EEKey); err != nil || len(key) == 0 {
			c.sendError("E2EE needs e2eeKey (base64 key material)", true)
			return nil
		}
	}
	name := strings.TrimSpace(msg.Name)
	if name == "" {
		name = "Guest"
	}

	s.mu.Lock()
	r := s.rooms[roomID]
	if r == nil {
		r = &room{id: roomID, e2ee: msg.E2EE}
		if msg.E2EE {
			r.e2eeKey = msg.E2EEKey
		}
		s.rooms[roomID] = r
	}
	r.mu.Lock()
	switch {
	case len(r.participants) >= s.maxParticipants:
		r.mu.Unlock()
		s.mu.Unlock()
		c.sendError("Room is full", true)
		return nil
	case r.e2ee != msg.E2EE:
		r.mu.Unlock()
		s.mu.Unlock()
		c.sendError("E2EE setting does not match the room", true)
		return nil
	}

	p, err := newParticipant(s.api, r, c, r.newParticipantID(), name)
	if err != nil {
		if len(r.participants) == 0 {
			delete(s.rooms, roomID)
		}
		r.mu.Unlock()
		s.mu.Unlock()
		log.Printf("room %s: create peer connections: %v", roomID, err)
		c.sendError("Server error", true)
		return nil
	}

	others := make([]ParticipantInfo, 0, len(r.participants))
	for _, o := range r.participants {
		others = append(others, o.info())
	}
	// Queued while holding the lock, so `joined` always precedes any offer and
	// every peer learns about the newcomer before its tracks.
	c.sendJSON(joinedMessage{Type: "joined", ParticipantID: p.id, Participants: others, E2EE: r.e2ee, E2EEKey: r.e2eeKey})
	for _, o := range r.participants {
		o.conn.sendJSON(participantJoinedMessage{Type: "participant joined", Participant: p.info()})
	}
	existing := append([]*participant(nil), r.participants...)
	r.participants = append(r.participants, p)
	r.mu.Unlock()
	s.mu.Unlock()

	log.Printf("room %s: %s (%s) joined, %d in room", roomID, p.id, name, len(existing)+1)

	var tracks []*forwardedTrack
	for _, o := range existing {
		tracks = append(tracks, o.publishedTracks()...)
	}
	p.subscribe(tracks...)
	return p
}

func (s *server) leave(p *participant) {
	r := p.room
	s.mu.Lock()
	r.mu.Lock()
	for i, o := range r.participants {
		if o == p {
			r.participants = append(r.participants[:i], r.participants[i+1:]...)
			break
		}
	}
	remaining := len(r.participants)
	if remaining == 0 {
		delete(s.rooms, r.id)
	}
	for _, o := range r.participants {
		o.conn.sendJSON(participantLeftMessage{Type: "participant left", ParticipantID: p.id})
	}
	r.mu.Unlock()
	s.mu.Unlock()

	log.Printf("room %s: %s left, %d in room", r.id, p.id, remaining)
	p.close()
}

// newParticipantID returns a short id that is unique in the room. Caller holds r.mu.
func (r *room) newParticipantID() string {
	for {
		b := make([]byte, 3)
		_, _ = rand.Read(b)
		id := hex.EncodeToString(b)
		taken := false
		for _, p := range r.participants {
			if p.id == id {
				taken = true
				break
			}
		}
		if !taken {
			return id
		}
	}
}

func (r *room) others(p *participant) []*participant {
	r.mu.Lock()
	defer r.mu.Unlock()
	var others []*participant
	for _, o := range r.participants {
		if o != p {
			others = append(others, o)
		}
	}
	return others
}

func (r *room) broadcast(from *participant, msg any) {
	for _, o := range r.others(from) {
		o.conn.sendJSON(msg)
	}
}

func (r *room) setMediaState(p *participant, state MediaState) {
	r.mu.Lock()
	p.state = state
	r.mu.Unlock()
	r.broadcast(p, mediaStateMessage{Type: "media state", ParticipantID: p.id, State: state})
}

// publish forwards a new track of p to everyone else in the room.
func (r *room) publish(t *forwardedTrack) {
	for _, o := range r.others(t.publisher) {
		o.subscribe(t)
	}
}

func (r *room) unpublish(t *forwardedTrack) {
	for _, o := range r.others(t.publisher) {
		o.unsubscribe(t)
	}
}
