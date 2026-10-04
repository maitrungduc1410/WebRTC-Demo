// Command sfu-server is the optional group call server of WebRTC-Demo.
//
// One process does both jobs: it accepts the signaling WebSocket at /ws and
// forwards the RTP packets of every participant to the others in the room
// (a Selective Forwarding Unit built on Pion). See ARCHITECTURE.md.
package main

import (
	"encoding/json"
	"flag"
	"fmt"
	"log"
	"net"
	"net/http"
	"os"
	"strconv"
)

func main() {
	port := flag.Int("port", envInt("PORT", 4001), "TCP port for HTTP/WebSocket and UDP port for media")
	publicIP := flag.String("public-ip", os.Getenv("PUBLIC_IP"), "IP address to advertise in ICE candidates instead of the local ones (server behind 1:1 NAT)")
	maxParticipants := flag.Int("max-participants", envInt("MAX_PARTICIPANTS", 8), "maximum participants per room")
	flag.Parse()

	api, err := newWebRTCAPI(apiConfig{UDPPort: *port, PublicIP: *publicIP})
	if err != nil {
		log.Fatalf("webrtc: %v", err)
	}

	server := newServer(api, *maxParticipants)
	mux := http.NewServeMux()
	mux.HandleFunc("/", func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		w.Header().Set("Access-Control-Allow-Origin", "*")
		_ = json.NewEncoder(w).Encode(map[string]any{"name": "sfu-server", "ok": true})
	})
	mux.HandleFunc("/ws", server.handleWebSocket)

	addr := fmt.Sprintf(":%d", *port)
	log.Printf("SFU listening on %s (TCP: signaling, UDP: media)", addr)
	for _, ip := range localIPv4s() {
		log.Printf("Network access via: ws://%s:%d/ws", ip, *port)
	}
	log.Fatal(http.ListenAndServe(addr, mux))
}

func envInt(name string, fallback int) int {
	if v, err := strconv.Atoi(os.Getenv(name)); err == nil {
		return v
	}
	return fallback
}

func localIPv4s() []string {
	var ips []string
	addrs, _ := net.InterfaceAddrs()
	for _, a := range addrs {
		if ipNet, ok := a.(*net.IPNet); ok && !ipNet.IP.IsLoopback() && !ipNet.IP.IsLinkLocalUnicast() && ipNet.IP.To4() != nil {
			ips = append(ips, ipNet.IP.String())
		}
	}
	return ips
}
