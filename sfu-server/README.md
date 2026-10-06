# sfu-server

The optional group call server of WebRTC-Demo. You only need it for **group calls**; 1:1 calls use the Node signaling server in [`signaling-server`](../signaling-server) and work without it.

It is one Go process built on [Pion](https://github.com/pion/webrtc) that does two jobs on one port:

- **Signaling** over a plain WebSocket at `ws://<host>:4001/ws` (TCP).
- **Media**: a Selective Forwarding Unit. Each participant sends its camera and microphone once, and the server copies the RTP packets to everyone else in the room without decoding them. All peer connections share **UDP port 4001**.

How it works, with diagrams and the message protocol: [ARCHITECTURE.md, section 12](../ARCHITECTURE.md#12-group-call-sfu-optional).

## Requirements

- [Go](https://go.dev/dl/) 1.25 or newer. With Go 1.21 to 1.24 installed, `go` downloads 1.25 by itself the first time (`GOTOOLCHAIN=auto`, the default).
- A machine the phones and browsers can reach on **TCP and UDP port 4001**. On a home or office Wi-Fi, run it on a computer on the same network.

## Run it

```
cd sfu-server
go run .
```

The first run downloads the dependencies. Then it prints something like:

```
SFU listening on :4001 (TCP: signaling, UDP: media)
Network access via: ws://192.168.1.10:4001/ws
```

The `Network access via` lines are the addresses to give the clients. Media uses the same IPv4 addresses; link-local ones (`169.254.x.x`, from iPhone USB or a Thunderbolt bridge) are left out because they do not lead to other devices. Check that it is up from another device with `curl http://192.168.1.10:4001/`, which answers `{"name":"sfu-server","ok":true}`. The web lobby uses the same request to show whether the server is reachable.

To build a binary instead:

```
go build -o sfu-server .
./sfu-server
```

Stop it with `Ctrl+C`. Rooms only live in memory, so nothing is kept between runs.

## Connect the clients

In the lobby of each client, switch to **Group call (SFU)**, set the SFU address once (it is remembered), and join the same room id from every device.

| Client | Default address | What to enter |
|---|---|---|
| Web | Port 4001 on the host that serves the page | `192.168.1.10:4001` (always include the port on the web) |
| Android | Port 4001 on the signaling server's host | `192.168.1.10` (`:4001` is added) or a full `ws://192.168.1.10:4001/ws` |
| iOS | Port 4001 on the signaling server's host | `192.168.1.10` (`:4001` is added) or a full `ws://192.168.1.10:4001/ws` |

If E2EE is on, everyone in the room must turn it on; the first person to join decides.

## Options

Every option is a flag or an environment variable; the flag wins.

| Flag | Environment variable | Default | What it does |
|---|---|---|---|
| `-port` | `PORT` | `4001` | TCP port for HTTP and the WebSocket, and UDP port for media. Clients then need the new port in their address. |
| `-public-ip` | `PUBLIC_IP` | none | IP address put in the ICE candidates instead of the machine's own. Use it when the server is behind a 1:1 NAT, e.g. a cloud VM. |
| `-max-participants` | `MAX_PARTICIPANTS` | `8` | People per room. The next one gets `Room is full`. |

Examples:

```
go run . -max-participants 12
PORT=5000 go run .
go run . -public-ip 203.0.113.7
```

**How many people?** The limit is only a default. There is no simulcast, so every client receives and decodes everyone else's video at full quality: with N people each phone downloads N − 1 videos, and the server sends N × (N − 1) streams. Phones usually struggle before the server does, at around 8 to 10 videos.

## Networking

- **Firewall.** Allow TCP and UDP 4001 (or your `-port`). macOS asks the first time you run it; on Linux with ufw: `sudo ufw allow 4001`. On Windows, allow `sfu-server` / `go` when the firewall prompt appears.
- **Cloud VM.** Open TCP and UDP 4001 in the security group, and start with `-public-ip <the VM's public IP>`. Without it the server advertises its private IP, the WebSocket connects, but no video arrives.
- **IPv4 and UDP only.** There is no TURN server and no TCP fallback for media. Networks that block outgoing UDP (some corporate or guest Wi-Fi) cannot receive video.
- **No TLS.** The server speaks `ws://`, not `wss://`. A web client served over HTTPS cannot open a `ws://` socket, so put a TLS reverse proxy (nginx, Caddy) in front of `/ws` in that case. The media is still DTLS-SRTP encrypted, as in any WebRTC call.
- **No authentication.** Anyone who can reach the port can join any room. It is a demo server; do not expose it to the internet as is.

## Troubleshooting

| Symptom | Likely cause |
|---|---|
| The lobby says it can't reach the group call server | Wrong address, server not running, or TCP 4001 blocked. Try `curl http://<address>:4001/` from the same network. On the web, check that the address has `:4001`. |
| Joined, tiles show names but no video | UDP 4001 blocked, or the server is behind NAT without `-public-ip`. |
| `Room is full` | The room already has `-max-participants` people. |
| `E2EE setting does not match the room` | Someone joined with a different E2EE switch than the person who created the room. |
| `bind: address already in use` | Another process uses port 4001; stop it or use `-port`. |
| `go run .` fails on the `go` version in `go.mod` | Your Go is older than 1.21 and cannot download 1.25 by itself; install a newer Go. |

The server logs every join, leave and published track, prefixed with the room id, which helps to see who is connected.

## Develop

```
go vet ./...
go test -race ./...
```

The tests start the server in process and connect real Pion clients over loopback: three participants exchange media, one leaves and another joins, plus media state, chat, a full room and an E2EE mismatch.

| File | What is in it |
|---|---|
| `main.go` | Flags, `GET /` health check, `/ws` |
| `signaling.go` | Message types, WebSocket read and write loops |
| `room.go` | Rooms, join and leave, forwarding a new track to everyone else |
| `participant.go` | The publish and subscribe peer connections of one participant, RTP forwarding, key frame requests, renegotiation |
| `webrtc.go` | Pion setup: VP8 and Opus, NACK and RTCP interceptors, the shared UDP port |
| `sfu_test.go` | Integration tests |
