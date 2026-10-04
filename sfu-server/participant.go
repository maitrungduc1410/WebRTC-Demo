package main

import (
	"errors"
	"io"
	"log"
	"sync"
	"sync/atomic"
	"time"

	"github.com/pion/rtcp"
	"github.com/pion/webrtc/v4"
)

const keyFrameRequestInterval = 500 * time.Millisecond

// participant owns the two PeerConnections of one client:
//   - publishPC receives the client's audio and video. The client offers once.
//   - subscribePC sends everyone else's tracks to the client. The server
//     offers, and offers again whenever a track is added or removed.
type participant struct {
	id   string
	name string
	room *room
	conn *connection

	state MediaState // guarded by room.mu

	publishPC   *webrtc.PeerConnection
	subscribePC *webrtc.PeerConnection

	// Remote candidates that arrived before the remote description.
	// Only touched by the WebSocket read loop.
	pendingCandidates map[string][]webrtc.ICECandidateInit

	tracksMu sync.Mutex
	tracks   []*forwardedTrack // published by this participant

	subMu              sync.Mutex
	senders            map[*forwardedTrack]*webrtc.RTPSender // subscribed on subscribePC
	pendingNegotiation bool
	closed             bool
}

// forwardedTrack is one published track, rewritten as a local track that the
// SFU adds to the subscribePC of every other participant.
type forwardedTrack struct {
	publisher *participant
	local     *webrtc.TrackLocalStaticRTP
	kind      webrtc.RTPCodecType
	ssrc      webrtc.SSRC

	lastKeyFrameRequest atomic.Int64
}

func newParticipant(api *webrtc.API, r *room, c *connection, id, name string) (*participant, error) {
	config := webrtc.Configuration{}
	publishPC, err := api.NewPeerConnection(config)
	if err != nil {
		return nil, err
	}
	subscribePC, err := api.NewPeerConnection(config)
	if err != nil {
		_ = publishPC.Close()
		return nil, err
	}
	p := &participant{
		id:                id,
		name:              name,
		room:              r,
		conn:              c,
		publishPC:         publishPC,
		subscribePC:       subscribePC,
		pendingCandidates: map[string][]webrtc.ICECandidateInit{},
		senders:           map[*forwardedTrack]*webrtc.RTPSender{},
	}
	publishPC.OnTrack(p.forward)
	publishPC.OnConnectionStateChange(func(s webrtc.PeerConnectionState) {
		log.Printf("room %s: %s publish %s", r.id, id, s)
	})
	subscribePC.OnConnectionStateChange(func(s webrtc.PeerConnectionState) {
		log.Printf("room %s: %s subscribe %s", r.id, id, s)
		if s == webrtc.PeerConnectionStateConnected {
			p.requestKeyFrames()
		}
	})
	// The network path each connection settles on: the first thing to check when media fails.
	for name, pc := range map[string]*webrtc.PeerConnection{pcPublish: publishPC, pcSubscribe: subscribePC} {
		pc.SCTP().Transport().ICETransport().OnSelectedCandidatePairChange(func(pair *webrtc.ICECandidatePair) {
			log.Printf("room %s: %s %s path %s", r.id, id, name, pair)
		})
	}
	return p, nil
}

func (p *participant) info() ParticipantInfo {
	return ParticipantInfo{ID: p.id, Name: p.name, State: p.state}
}

func (p *participant) publishedTracks() []*forwardedTrack {
	p.tracksMu.Lock()
	defer p.tracksMu.Unlock()
	return append([]*forwardedTrack(nil), p.tracks...)
}

// forward copies the RTP packets of a published track to its local track,
// which writes them to every subscriber. The payload is never decoded, so
// E2EE frames pass through untouched.
func (p *participant) forward(remote *webrtc.TrackRemote, _ *webrtc.RTPReceiver) {
	kind := remote.Kind()
	// Stream id = participant id: that is how clients map a track to a tile.
	// The track id must be unique in the room too: libwebrtc names a new remote
	// receiver after it, and two receivers with one id overwrite each other.
	local, err := webrtc.NewTrackLocalStaticRTP(remote.Codec().RTPCodecCapability, p.id+"-"+kind.String(), p.id)
	if err != nil {
		log.Printf("room %s: %s %s: %v", p.room.id, p.id, kind, err)
		return
	}
	t := &forwardedTrack{publisher: p, local: local, kind: kind, ssrc: remote.SSRC()}

	p.tracksMu.Lock()
	p.tracks = append(p.tracks, t)
	p.tracksMu.Unlock()
	log.Printf("room %s: %s publishes %s (%s)", p.room.id, p.id, kind, remote.Codec().MimeType)
	p.room.publish(t)

	for {
		pkt, _, err := remote.ReadRTP()
		if err != nil {
			break
		}
		// Extension ids were negotiated on the publisher's connection and mean
		// nothing on the subscribers' ones; Pion adds its own (transport-cc).
		pkt.Header.Extension = false
		pkt.Header.Extensions = nil
		if err := local.WriteRTP(pkt); err != nil && !errors.Is(err, io.ErrClosedPipe) {
			break
		}
	}

	p.tracksMu.Lock()
	for i, o := range p.tracks {
		if o == t {
			p.tracks = append(p.tracks[:i], p.tracks[i+1:]...)
			break
		}
	}
	p.tracksMu.Unlock()
	p.room.unpublish(t)
}

// requestKeyFrame asks the publisher for a key frame (PLI), at most once per
// keyFrameRequestInterval: one key frame serves every subscriber.
func (t *forwardedTrack) requestKeyFrame() {
	if t.kind != webrtc.RTPCodecTypeVideo {
		return
	}
	now := time.Now().UnixNano()
	last := t.lastKeyFrameRequest.Load()
	if now-last < int64(keyFrameRequestInterval) || !t.lastKeyFrameRequest.CompareAndSwap(last, now) {
		return
	}
	_ = t.publisher.publishPC.WriteRTCP([]rtcp.Packet{&rtcp.PictureLossIndication{MediaSSRC: uint32(t.ssrc)}})
}

func (p *participant) requestKeyFrames() {
	p.subMu.Lock()
	tracks := make([]*forwardedTrack, 0, len(p.senders))
	for t := range p.senders {
		tracks = append(tracks, t)
	}
	p.subMu.Unlock()
	for _, t := range tracks {
		t.requestKeyFrame()
	}
}

// subscribe adds tracks to the subscribePC and renegotiates once.
func (p *participant) subscribe(tracks ...*forwardedTrack) {
	p.subMu.Lock()
	added := 0
	for _, t := range tracks {
		if p.closed || p.senders[t] != nil {
			continue
		}
		sender, err := p.subscribePC.AddTrack(t.local)
		if err != nil {
			log.Printf("room %s: %s subscribe to %s: %v", p.room.id, p.id, t.publisher.id, err)
			continue
		}
		p.senders[t] = sender
		added++
		go readSenderRTCP(sender, t)
	}
	p.subMu.Unlock()
	if added > 0 {
		p.negotiate()
	}
}

func (p *participant) unsubscribe(t *forwardedTrack) {
	p.subMu.Lock()
	sender := p.senders[t]
	delete(p.senders, t)
	if sender != nil && !p.closed {
		if err := p.subscribePC.RemoveTrack(sender); err != nil {
			log.Printf("room %s: %s unsubscribe: %v", p.room.id, p.id, err)
		}
	}
	p.subMu.Unlock()
	if sender != nil {
		p.negotiate()
	}
}

// readSenderRTCP drains RTCP from a subscriber (needed for NACK handling in
// the interceptors) and relays its key frame requests to the publisher.
func readSenderRTCP(sender *webrtc.RTPSender, t *forwardedTrack) {
	for {
		packets, _, err := sender.ReadRTCP()
		if err != nil {
			return
		}
		for _, pkt := range packets {
			switch pkt.(type) {
			case *rtcp.PictureLossIndication, *rtcp.FullIntraRequest:
				t.requestKeyFrame()
			}
		}
	}
}

// negotiate sends a new subscribe offer. If the previous offer is still
// waiting for its answer, the new one goes out as soon as the answer arrives.
func (p *participant) negotiate() {
	p.subMu.Lock()
	defer p.subMu.Unlock()
	if p.closed {
		return
	}
	pc := p.subscribePC
	if pc.SignalingState() != webrtc.SignalingStateStable {
		p.pendingNegotiation = true
		return
	}
	if len(pc.GetTransceivers()) == 0 {
		return
	}
	offer, err := pc.CreateOffer(nil)
	if err != nil {
		log.Printf("room %s: %s create offer: %v", p.room.id, p.id, err)
		return
	}
	// The server does not trickle: its SDP carries all of its candidates.
	gathered := webrtc.GatheringCompletePromise(pc)
	if err := pc.SetLocalDescription(offer); err != nil {
		log.Printf("room %s: %s set local offer: %v", p.room.id, p.id, err)
		return
	}
	<-gathered
	p.conn.sendJSON(sdpMessage{Type: "offer", PC: pcSubscribe, SDP: pc.LocalDescription().SDP})
}

func (p *participant) handleSubscribeAnswer(msg inMessage) {
	if msg.PC != pcSubscribe {
		p.conn.sendError("Only subscribe answers are expected", false)
		return
	}
	p.subMu.Lock()
	if p.closed {
		p.subMu.Unlock()
		return
	}
	err := p.subscribePC.SetRemoteDescription(webrtc.SessionDescription{Type: webrtc.SDPTypeAnswer, SDP: msg.SDP})
	again := p.pendingNegotiation
	p.pendingNegotiation = false
	p.subMu.Unlock()
	if err != nil {
		log.Printf("room %s: %s set subscribe answer: %v", p.room.id, p.id, err)
		p.conn.sendError("Invalid subscribe answer", false)
		return
	}
	p.addPendingCandidates(pcSubscribe, p.subscribePC)
	// Tracks added by this negotiation start with a key frame.
	p.requestKeyFrames()
	if again {
		p.negotiate()
	}
}

func (p *participant) handlePublishOffer(msg inMessage) {
	if msg.PC != pcPublish {
		p.conn.sendError("Only publish offers are expected", false)
		return
	}
	pc := p.publishPC
	if err := pc.SetRemoteDescription(webrtc.SessionDescription{Type: webrtc.SDPTypeOffer, SDP: msg.SDP}); err != nil {
		log.Printf("room %s: %s set publish offer: %v", p.room.id, p.id, err)
		p.conn.sendError("Invalid publish offer: "+err.Error(), false)
		return
	}
	answer, err := pc.CreateAnswer(nil)
	if err != nil {
		p.conn.sendError("Cannot answer publish offer: "+err.Error(), false)
		return
	}
	gathered := webrtc.GatheringCompletePromise(pc)
	if err := pc.SetLocalDescription(answer); err != nil {
		p.conn.sendError("Cannot answer publish offer: "+err.Error(), false)
		return
	}
	<-gathered
	p.conn.sendJSON(sdpMessage{Type: "answer", PC: pcPublish, SDP: pc.LocalDescription().SDP})
	p.addPendingCandidates(pcPublish, pc)
}

func (p *participant) handleCandidate(msg inMessage) {
	if msg.Candidate == nil || msg.Candidate.Candidate == "" || isLinkLocalCandidate(msg.Candidate.Candidate) {
		return
	}
	var pc *webrtc.PeerConnection
	switch msg.PC {
	case pcPublish:
		pc = p.publishPC
	case pcSubscribe:
		pc = p.subscribePC
	default:
		return
	}
	if pc.RemoteDescription() == nil {
		p.pendingCandidates[msg.PC] = append(p.pendingCandidates[msg.PC], *msg.Candidate)
		return
	}
	if err := pc.AddICECandidate(*msg.Candidate); err != nil {
		log.Printf("room %s: %s add %s candidate: %v", p.room.id, p.id, msg.PC, err)
	}
}

func (p *participant) addPendingCandidates(name string, pc *webrtc.PeerConnection) {
	for _, c := range p.pendingCandidates[name] {
		if err := pc.AddICECandidate(c); err != nil {
			log.Printf("room %s: %s add %s candidate: %v", p.room.id, p.id, name, err)
		}
	}
	delete(p.pendingCandidates, name)
}

func (p *participant) close() {
	p.subMu.Lock()
	p.closed = true
	p.subMu.Unlock()
	// Closing publishPC ends the forward loops, which unpublish the tracks
	// from everyone else's subscribePC.
	_ = p.publishPC.Close()
	_ = p.subscribePC.Close()
}
