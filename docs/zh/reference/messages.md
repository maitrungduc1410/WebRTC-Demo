# 消息

两个服务器都通过普通 WebSocket 在 `/ws` 上收发 JSON 文本帧。每条消息都是一个带 `type` 字段的对象，未知的类型会被忽略。

## HTTP {#http}

| 请求 | 响应 | 用途 |
| --- | --- | --- |
| 信令服务器上的 `GET /` | `{"name":"signaling-server","ok":true}` | 大厅的状态指示点，每 5 s 轮询一次 |
| SFU 服务器上的 `GET /` | `{"name":"sfu-server","ok":true}` | 同上，用于多人通话模式 |

## 1:1 通话 {#one-to-one-calls}

信令服务器 [`server.js`](gh:signaling-server/server.js)，默认端口 4000。它把每个房间里的两个 socket 配成一对，大部分消息原样中继给另一方。

| 客户端发送 | 另一个客户端收到 | 用途 |
| --- | --- | --- |
| `join {roomId}` | `peer joined`（发给已在房间里的一端） | 第一个加入的人创建房间，第二个人发起通话 |
| `offer {sdp}` | `offer {sdp}` | SDP offer |
| `answer {sdp}` | `answer {sdp}` | SDP answer |
| `candidate {candidate: {candidate, sdpMid, sdpMLineIndex}}` | 相同 | Trickle ICE |
| `encryption key {key}` | 相同 | 32 字节的 E2EE 密钥材料，base64 编码，在 offer 之前发送。 |
| `encryption key received` | 相同 | 确认消息，只用于打日志 |
| `media state {state: {audio, video, screen}}` | 相同 | 麦克风和摄像头的开关状态，以及是否正在共享内容 |
| `leave` | 无 | 释放位置。直接关闭 socket 效果相同。 |

| 服务器发送 | 时机 |
| --- | --- |
| `error {message, fatal}` | `fatal: true` 会结束通话：`Room is full`、`Missing room id`。`You are already in this room` 不是致命错误。 |

服务器每 25 s ping 一次所有 socket，没有响应的会被断开。

## 多人通话 {#group-calls}

SFU 服务器 [`sfu-server`](gh:sfu-server)，TCP 和 UDP 默认都使用 4001 端口。它不做中继：两个 peer connection 的另一端都是它自己。

| 客户端 → 服务器 | 字段 | 说明 |
| --- | --- | --- |
| `join` | `roomId`、`name`、`e2ee`、`e2eeKey`（base64，开启 `e2ee` 时） | 第一条消息。`name` 是一个标签，比如 `Web`、`Android`、`iOS`。 |
| `offer` | `pc: "publish"`、`sdp` | 收到 `joined` 后发送一次 |
| `answer` | `pc: "subscribe"`、`sdp` | 回复每一个 subscribe offer |
| `candidate` | `pc`、`candidate: {candidate, sdpMid, sdpMLineIndex}` | 两条连接都用它做 trickle ICE |
| `media state` | `state: {audio, video, screen}` | 含义和 300 ms 规则都与 1:1 通话相同 |
| `chat` | `text` | |
| `leave` | 无 | 之后客户端关闭 socket |

| 服务器 → 客户端 | 字段 | 说明 |
| --- | --- | --- |
| `joined` | `participantId`、`participants: [{id, name, state}]`、`e2ee`、`e2eeKey` | `participants` 列出已经在房间里的其他人。`e2eeKey` 是房间密钥。 |
| `answer` | `pc: "publish"`、`sdp` | |
| `offer` | `pc: "subscribe"`、`sdp` | 初次协商和之后每次重新协商。必须逐个回复。 |
| `participant joined` | `participant: {id, name, state}` | |
| `participant left` | `participantId` | 立即移除对应的小窗 |
| `media state` | `participantId`、`state` | |
| `chat` | `participantId`、`name`、`text` | |
| `error` | `message`、`fatal` | 致命错误（`Room is full`、`E2EE setting does not match the room`）会关闭 socket |

服务器不会 trickle 自己的 candidate：它们已经包含在它发出的每个 SDP 中。服务器每 20 s ping 一次。WebSocket 关闭后，对应的参与者会被移除，不支持恢复会话。

### 转发的 track {#forwarded-tracks}

| | 值 |
| --- | --- |
| Stream ID（`msid`） | 发布者的 `participantId` |
| Track ID | `<participantId>-audio` 或 `<participantId>-video` |
| Codec | 仅 VP8 和 Opus |

## 服务器地址 {#server-addresses}

各客户端在大厅中接受的输入，以及补上 `/ws` 之前的解析结果：

| 输入 | 信令服务器，所有客户端 | SFU，Android、iOS、macOS、Windows |
| --- | --- | --- |
| `192.168.1.10` | `http://192.168.1.10`，端口 80 | `ws://192.168.1.10:4001` |
| `192.168.1.10:4000` | `http://192.168.1.10:4000` | `ws://192.168.1.10:4000` |
| `https://example.com` | `https://example.com` | `wss://example.com` |
| `ws://192.168.1.10:4001/ws` | 不接受 | `ws://192.168.1.10:4001` |
| 任意路径 | 丢弃 | 丢弃 |

所以填写信令服务器地址时，以及在 Web 端填写 SFU 地址时，都要带上端口。Web 客户端的默认值是提供页面的主机上的 4000 端口（信令）和 4001 端口（SFU）。
