# Messages

Both servers speak JSON text frames over a plain WebSocket at `/ws`. Every message is an object with a `type`. Unknown types are ignored.

## HTTP {#http}

| Request | Answer | Used for |
| --- | --- | --- |
| `GET /` on the signaling server | `{"name":"signaling-server","ok":true}` | The lobby's status dot, polled every 5 s |
| `GET /` on the SFU server | `{"name":"sfu-server","ok":true}` | Same, in group mode |

## 1:1 calls {#one-to-one-calls}

The signaling server, [`server.js`](gh:signaling-server/server.js), default port 4000. It pairs two sockets per room and relays most messages unchanged to the other one.

| Client sends | Other client receives | Purpose |
| --- | --- | --- |
| `join {roomId}` | `peer joined` (the peer already in the room) | The first joiner creates the room, the second one starts the call |
| `offer {sdp}` | `offer {sdp}` | SDP offer |
| `answer {sdp}` | `answer {sdp}` | SDP answer |
| `candidate {candidate: {candidate, sdpMid, sdpMLineIndex}}` | the same | Trickle ICE |
| `encryption key {key}` | the same | 32 bytes of E2EE key material, base64. Sent before the offer. |
| `encryption key received` | the same | Acknowledgement, only logged |
| `media state {state: {audio, video, screen}}` | the same | Mic and camera on or off, and whether content is shared |
| `leave` | nothing | Frees the seat. Closing the socket does the same. |

| Server sends | When |
| --- | --- |
| `error {message, fatal}` | `fatal: true` ends the call: `Room is full`, `Missing room id`. `You are already in this room` is not fatal. |

The server pings every socket every 25 s and drops one that doesn't answer.

## Group calls {#group-calls}

The SFU server, [`sfu-server`](gh:sfu-server), default port 4001 for both TCP and UDP. It doesn't relay: it is the other end of both peer connections.

| Client → server | Fields | Notes |
| --- | --- | --- |
| `join` | `roomId`, `name`, `e2ee`, `e2eeKey` (base64, when `e2ee`) | First message. `name` is a label such as `Web`, `Android`, `iOS`. |
| `offer` | `pc: "publish"`, `sdp` | Once, after `joined` |
| `answer` | `pc: "subscribe"`, `sdp` | Answer to every subscribe offer |
| `candidate` | `pc`, `candidate: {candidate, sdpMid, sdpMLineIndex}` | Trickle ICE for either connection |
| `media state` | `state: {audio, video, screen}` | Same meaning and same 300 ms rule as in 1:1 calls |
| `chat` | `text` | |
| `leave` | none | Then the client closes the socket |

| Server → client | Fields | Notes |
| --- | --- | --- |
| `joined` | `participantId`, `participants: [{id, name, state}]`, `e2ee`, `e2eeKey` | `participants` lists the others already in the room. `e2eeKey` is the room key. |
| `answer` | `pc: "publish"`, `sdp` | |
| `offer` | `pc: "subscribe"`, `sdp` | Initial and every renegotiation. Answer them one at a time. |
| `participant joined` | `participant: {id, name, state}` | |
| `participant left` | `participantId` | Remove the tile at once |
| `media state` | `participantId`, `state` | |
| `chat` | `participantId`, `name`, `text` | |
| `error` | `message`, `fatal` | Fatal errors (`Room is full`, `E2EE setting does not match the room`) close the socket |

The server doesn't trickle its own candidates: they are already in every SDP it sends. It pings every 20 s. A closed WebSocket removes the participant, and there is no session resume.

### Forwarded tracks {#forwarded-tracks}

| | Value |
| --- | --- |
| Stream ID (`msid`) | The publisher's `participantId` |
| Track ID | `<participantId>-audio` or `<participantId>-video` |
| Codecs | VP8 and Opus only |

## Server addresses {#server-addresses}

What each client accepts in the lobby, before it adds `/ws`:

| Input | Signaling server, every client | SFU on Android, iOS, macOS, Windows |
| --- | --- | --- |
| `192.168.1.10` | `http://192.168.1.10`, port 80 | `ws://192.168.1.10:4001` |
| `192.168.1.10:4000` | `http://192.168.1.10:4000` | `ws://192.168.1.10:4000` |
| `https://example.com` | `https://example.com` | `wss://example.com` |
| `ws://192.168.1.10:4001/ws` | not accepted | `ws://192.168.1.10:4001` |
| Any path | Dropped | Dropped |

So for the signaling server, and for the SFU on the web, include the port. The web client's defaults are port 4000 (signaling) and 4001 (SFU) on the host that serves the page.
