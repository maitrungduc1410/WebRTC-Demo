package main

import (
	"net"
	"strings"

	"github.com/pion/ice/v4"
	"github.com/pion/interceptor"
	"github.com/pion/webrtc/v4"
)

type apiConfig struct {
	// UDPPort is the single UDP port every PeerConnection shares (0 = random).
	UDPPort int
	// PublicIP replaces the host candidates when the server sits behind a 1:1 NAT.
	PublicIP string
	// Loopback also listens on 127.0.0.1, used by the tests.
	Loopback bool
	// udpMux replaces the per-interface sockets, so tests can fake a multi-homed host.
	udpMux ice.UDPMux
}

// newWebRTCAPI builds the Pion API shared by all PeerConnections.
//
// The SFU only accepts VP8 and Opus: every client can encode and decode them,
// the E2EE frame format keeps the VP8 payload header readable, and forwarding
// never has to translate between codecs.
func newWebRTCAPI(cfg apiConfig) (*webrtc.API, error) {
	media := &webrtc.MediaEngine{}
	videoFeedback := []webrtc.RTCPFeedback{
		{Type: webrtc.TypeRTCPFBGoogREMB},
		{Type: webrtc.TypeRTCPFBCCM, Parameter: "fir"},
		{Type: webrtc.TypeRTCPFBNACK},
		{Type: webrtc.TypeRTCPFBNACK, Parameter: "pli"},
	}
	if err := media.RegisterCodec(webrtc.RTPCodecParameters{
		RTPCodecCapability: webrtc.RTPCodecCapability{
			MimeType: webrtc.MimeTypeVP8, ClockRate: 90000, RTCPFeedback: videoFeedback,
		},
		PayloadType: 96,
	}, webrtc.RTPCodecTypeVideo); err != nil {
		return nil, err
	}
	if err := media.RegisterCodec(webrtc.RTPCodecParameters{
		RTPCodecCapability: webrtc.RTPCodecCapability{
			MimeType: webrtc.MimeTypeOpus, ClockRate: 48000, Channels: 2, SDPFmtpLine: "minptime=10;useinbandfec=1",
		},
		PayloadType: 111,
	}, webrtc.RTPCodecTypeAudio); err != nil {
		return nil, err
	}

	// NACK generator/responder, RTCP reports and TWCC feedback to the publishers.
	interceptors := &interceptor.Registry{}
	if err := webrtc.RegisterDefaultInterceptors(media, interceptors); err != nil {
		return nil, err
	}

	settings := webrtc.SettingEngine{}
	muxOptions := []ice.UDPMuxFromPortOption{
		ice.UDPMuxFromPortWithNetworks(ice.NetworkTypeUDP4),
		ice.UDPMuxFromPortWithIPFilter(func(ip net.IP) bool { return !ip.IsLinkLocalUnicast() }),
	}
	if cfg.Loopback {
		muxOptions = append(muxOptions, ice.UDPMuxFromPortWithLoopback())
		settings.SetIncludeLoopbackCandidate(true)
	}
	var udpMux ice.UDPMux = cfg.udpMux
	if udpMux == nil {
		var err error
		if udpMux, err = ice.NewMultiUDPMuxFromPort(cfg.UDPPort, muxOptions...); err != nil {
			return nil, err
		}
	}
	settings.SetICEUDPMux(udpMux)
	settings.SetNetworkTypes([]webrtc.NetworkType{webrtc.NetworkTypeUDP4})
	if cfg.PublicIP != "" {
		if err := settings.SetICEAddressRewriteRules(webrtc.ICEAddressRewriteRule{
			External:        []string{cfg.PublicIP},
			AsCandidateType: webrtc.ICECandidateTypeHost,
		}); err != nil {
			return nil, err
		}
	}

	return webrtc.NewAPI(
		webrtc.WithMediaEngine(media),
		webrtc.WithInterceptorRegistry(interceptors),
		webrtc.WithSettingEngine(settings),
	), nil
}

// isLinkLocalCandidate reports whether an ICE candidate line uses a link-local
// address (169.254.0.0/16 or fe80::/10).
//
// Link-local addresses come from interfaces like iPhone USB or a Thunderbolt
// bridge and never lead to a client through a real network. On a Mac running
// both the SFU and Chrome, ICE settled on them and the media then stopped, so
// the SFU neither listens on them nor checks the clients' ones.
func isLinkLocalCandidate(candidate string) bool {
	// candidate:<foundation> <component> <transport> <priority> <address> <port> typ ...
	fields := strings.Fields(strings.TrimPrefix(candidate, "a="))
	if len(fields) < 5 {
		return false
	}
	ip := net.ParseIP(fields[4])
	return ip != nil && ip.IsLinkLocalUnicast()
}
