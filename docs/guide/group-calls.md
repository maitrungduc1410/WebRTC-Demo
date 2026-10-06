# Group calls

Everything in the [quick start](/guide/quick-start) is enough for 1:1 calls. To call with more people, also start the SFU server. It handles both signaling and media for group calls, so the Node signaling server isn't used in this mode.

## Start the SFU server {#start-the-sfu-server}

You need [Go](https://go.dev/dl/) 1.25 or newer. With Go 1.21 to 1.24, `go` downloads 1.25 by itself the first time.

```sh
cd sfu-server
go run .
```

```
SFU listening on :4001 (TCP: signaling, UDP: media)
Network access via: ws://192.168.1.10:4001/ws
```

Clients must reach **TCP and UDP port 4001** on that machine: TCP for the WebSocket, UDP for the media of every peer connection. Check it from another device with `curl http://192.168.1.10:4001/`, which answers `{"name":"sfu-server","ok":true}`.

## Join from the clients {#join-from-the-clients}

In the lobby of any client, switch to **Group call (SFU)**, set the SFU address once, and join the same room ID from every device.

| Client | Default address | What to enter |
| --- | --- | --- |
| Web | Port 4001 on the host that serves the page | `192.168.1.10:4001` (always include the port on the web) |
| Android, iOS, Windows | Port 4001 on the signaling server's host | `192.168.1.10` (`:4001` is added) or a full `ws://192.168.1.10:4001/ws` |
| macOS | `http://localhost:4001`, in Settings (⌘,) | Same as iOS |

E2EE works in group calls too. Everyone in the room must use the same setting; the first person to join decides.

## Options {#options}

Every option is a flag or an environment variable. The flag wins.

| Flag | Variable | Default | What it does |
| --- | --- | --- | --- |
| `-port` | `PORT` | `4001` | TCP port for HTTP and the WebSocket, and UDP port for media |
| `-public-ip` | `PUBLIC_IP` | none | Address put in the ICE candidates instead of the machine's own. Use it behind a 1:1 NAT, like a cloud VM. |
| `-max-participants` | `MAX_PARTICIPANTS` | `8` | People per room. The next one gets `Room is full`. |

```sh
go run . -max-participants 12
PORT=5000 go run .
go run . -public-ip 203.0.113.7
```

## How many people? {#how-many-people}

The limit is only a default. There is no simulcast, so every client receives and decodes everyone else's video at full quality: with N people each phone downloads N − 1 videos and the server sends N × (N − 1) streams. Phones usually struggle before the server does, at around 8 to 10 videos. [Group calls (SFU)](/how-it-works/group-calls) explains why and has a calculator.

## Networking {#networking}

- **Firewall.** Allow TCP and UDP 4001 (or your `-port`). macOS asks the first time. On Linux with ufw: `sudo ufw allow 4001`.
- **Cloud VM.** Open TCP and UDP 4001 in the security group and start with `-public-ip <the VM's public IP>`. Without it the WebSocket connects but no video arrives.
- **IPv4 and UDP only.** There is no TURN server and no TCP fallback for media. Networks that block outgoing UDP can't receive video.
- **No TLS.** The server speaks `ws://`. A web client served over HTTPS can't open a `ws://` socket, so put a TLS reverse proxy (nginx, Caddy) in front of `/ws` in that case.
- **No authentication.** Anyone who can reach the port can join any room. Don't expose it to the internet as is.

More details are in [`sfu-server/README.md`](gh:sfu-server/README.md). If something goes wrong, see [Troubleshooting](/guide/troubleshooting#group-calls).
