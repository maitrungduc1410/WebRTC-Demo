package main

import (
	"net"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"

	"github.com/pion/ice/v4"
	"github.com/pion/webrtc/v4"
)

// Every 127.x address is local on Linux, so three of them stand in for a
// laptop with several interfaces (Wi-Fi, VPN, link-local) running both the
// SFU and the browser tabs.
var hostIPs = []string{"127.0.0.1", "127.0.0.2", "127.0.0.3"}

func multiHomedMux(t *testing.T, port int) ice.UDPMux {
	t.Helper()
	var muxes []ice.UDPMux
	for _, ip := range hostIPs {
		conn, err := net.ListenUDP("udp4", &net.UDPAddr{IP: net.ParseIP(ip), Port: port})
		if err != nil {
			t.Fatal(err)
		}
		if port == 0 && ip == hostIPs[0] {
			port = conn.LocalAddr().(*net.UDPAddr).Port
		}
		muxes = append(muxes, ice.NewUDPMuxDefault(ice.UDPMuxParams{UDPConn: conn}))
	}
	mux := ice.NewMultiUDPMuxDefault(muxes...)
	t.Cleanup(func() { _ = mux.Close() })
	return mux
}

func startMultiHomedServer(t *testing.T) string {
	t.Helper()
	api, err := newWebRTCAPI(apiConfig{Loopback: true, udpMux: multiHomedMux(t, 0)})
	if err != nil {
		t.Fatal(err)
	}
	mux := http.NewServeMux()
	mux.HandleFunc("/ws", newServer(api, 8).handleWebSocket)
	ts := httptest.NewServer(mux)
	t.Cleanup(ts.Close)
	return "ws" + strings.TrimPrefix(ts.URL, "http") + "/ws"
}

// browserAPI gives each PeerConnection its own socket on every interface, like Chrome.
func browserAPI(t *testing.T) func() *webrtc.API {
	return func() *webrtc.API {
		m := &webrtc.MediaEngine{}
		if err := m.RegisterDefaultCodecs(); err != nil {
			t.Fatal(err)
		}
		se := webrtc.SettingEngine{}
		se.SetIncludeLoopbackCandidate(true)
		se.SetNetworkTypes([]webrtc.NetworkType{webrtc.NetworkTypeUDP4})
		se.SetICEUDPMux(multiHomedMux(t, 0))
		return webrtc.NewAPI(webrtc.WithMediaEngine(m), webrtc.WithSettingEngine(se))
	}
}

func joinFromBrowser(t *testing.T, url, roomID, name string) *testClient {
	t.Helper()
	c, first := dial(t, url, map[string]any{"roomId": roomID, "name": name, "e2ee": false})
	if first["type"] != "joined" {
		t.Fatalf("%s: expected joined, got %v", name, first)
	}
	c.name = name
	c.id = first["participantId"].(string)
	c.joined = first
	c.newAPI = browserAPI(t)
	go c.readLoop()
	c.publish()
	return c
}

func TestIsLinkLocalCandidate(t *testing.T) {
	cases := map[string]bool{
		"candidate:1 1 udp 2122260223 169.254.42.68 52309 typ host generation 0":                 true,
		"a=candidate:1 1 udp 2122260223 169.254.42.68 52309 typ host":                            true,
		"candidate:2 1 udp 2122194687 fe80::1c2a:8ff:fe12:3456 54134 typ host":                   true,
		"candidate:3 1 udp 2122129151 192.168.0.2 55580 typ host generation 0":                   false,
		"candidate:4 1 udp 1686052607 203.0.113.7 61000 typ srflx raddr 192.168.0.2 rport 55580": false,
		"candidate:5 1 udp 2122260223 a2eda6d9-246f-4c8d-82d2-6cbf299dbf9c.local 65434 typ host": false,
		"":        false,
		"garbage": false,
	}
	for candidate, want := range cases {
		if got := isLinkLocalCandidate(candidate); got != want {
			t.Errorf("isLinkLocalCandidate(%q) = %v, want %v", candidate, got, want)
		}
	}
}

func TestTwoTabsOnAMultiHomedHost(t *testing.T) {
	url := startMultiHomedServer(t)
	a := joinFromBrowser(t, url, "room", "A")
	b := joinFromBrowser(t, url, "room", "B")
	a.waitForMedia(b)
	b.waitForMedia(a)
}
