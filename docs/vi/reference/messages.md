---
title: "Tham khảo message signaling WebRTC"
description: "Toàn bộ message JSON của signaling server và SFU server cho cuộc gọi 1:1 và gọi nhóm, kèm ví dụ và thứ tự gửi."
---

# Messages

Cả hai server đều nói chuyện bằng text frame JSON trên một WebSocket bình thường ở `/ws`. Mỗi message là một object có trường `type`. Type nào không biết thì bị bỏ qua.

## HTTP {#http}

| Request | Kết quả | Dùng cho |
| --- | --- | --- |
| `GET /` trên signaling server | `{"name":"signaling-server","ok":true}` | Chấm trạng thái ở lobby, gọi mỗi 5 s |
| `GET /` trên SFU server | `{"name":"sfu-server","ok":true}` | Như trên, ở chế độ nhóm |

## Cuộc gọi 1:1 {#one-to-one-calls}

Signaling server là [`server.js`](gh:signaling-server/server.js), port mặc định 4000. Nó ghép hai socket trong mỗi phòng và chuyển gần như mọi message, giữ nguyên nội dung, sang socket còn lại.

| Client gửi | Client bên kia nhận | Mục đích |
| --- | --- | --- |
| `join {roomId}` | `peer joined` (peer đã ở trong phòng nhận) | Người vào đầu tiên tạo phòng, người thứ hai bắt đầu cuộc gọi |
| `offer {sdp}` | `offer {sdp}` | SDP offer |
| `answer {sdp}` | `answer {sdp}` | SDP answer |
| `candidate {candidate: {candidate, sdpMid, sdpMLineIndex}}` | giống hệt | Trickle ICE |
| `encryption key {key}` | giống hệt | 32 byte key material cho E2EE, dạng base64. Gửi trước offer. |
| `encryption key received` | giống hệt | Xác nhận đã nhận, chỉ để log |
| `media state {state: {audio, video, screen}}` | giống hệt | Micro và camera đang bật hay tắt, và có đang chia sẻ nội dung không |
| `leave` | không có gì | Giải phóng chỗ trong phòng. Đóng socket cũng có tác dụng như vậy. |

| Server gửi | Khi nào |
| --- | --- |
| `error {message, fatal}` | `fatal: true` thì kết thúc cuộc gọi: `Room is full`, `Missing room id`. `You are already in this room` thì không fatal. |

Cứ 25 s server ping mọi socket một lần và ngắt socket nào không trả lời.

## Cuộc gọi nhóm {#group-calls}

SFU server là [`sfu-server`](gh:sfu-server), port mặc định 4001 cho cả TCP lẫn UDP. Server này không chuyển tiếp message: nó chính là đầu bên kia của cả hai peer connection.

| Client → server | Trường | Ghi chú |
| --- | --- | --- |
| `join` | `roomId`, `name`, `e2ee`, `e2eeKey` (base64, khi có `e2ee`) | Message đầu tiên. `name` là nhãn, ví dụ `Web`, `Android`, `iOS`. |
| `offer` | `pc: "publish"`, `sdp` | Một lần, sau `joined` |
| `answer` | `pc: "subscribe"`, `sdp` | Trả lời cho mọi offer subscribe |
| `candidate` | `pc`, `candidate: {candidate, sdpMid, sdpMLineIndex}` | Trickle ICE cho connection nào cũng được |
| `media state` | `state: {audio, video, screen}` | Cùng ý nghĩa và cùng quy tắc 300 ms như cuộc gọi 1:1 |
| `chat` | `text` | |
| `leave` | không có | Sau đó client đóng socket |

| Server → client | Trường | Ghi chú |
| --- | --- | --- |
| `joined` | `participantId`, `participants: [{id, name, state}]`, `e2ee`, `e2eeKey` | `participants` liệt kê những người khác đã ở trong phòng. `e2eeKey` là key của phòng. |
| `answer` | `pc: "publish"`, `sdp` | |
| `offer` | `pc: "subscribe"`, `sdp` | Lần đầu và mỗi lần renegotiate. Trả lời từng cái một. |
| `participant joined` | `participant: {id, name, state}` | |
| `participant left` | `participantId` | Xóa tile ngay |
| `media state` | `participantId`, `state` | |
| `chat` | `participantId`, `name`, `text` | |
| `error` | `message`, `fatal` | Lỗi fatal (`Room is full`, `E2EE setting does not match the room`) sẽ đóng socket |

Server không trickle candidate của chính nó: các candidate đã nằm sẵn trong mọi SDP server gửi đi. Server ping mỗi 20 s. WebSocket đóng thì người tham gia bị xóa khỏi phòng, và không có cơ chế tiếp tục phiên cũ.

### Track được chuyển tiếp {#forwarded-tracks}

| | Giá trị |
| --- | --- |
| Stream ID (`msid`) | `participantId` của người publish |
| Track ID | `<participantId>-audio` hoặc `<participantId>-video` |
| Codec | Chỉ VP8 và Opus |

## Địa chỉ server {#server-addresses}

Mỗi client chấp nhận những gì ở lobby, trước khi tự thêm `/ws`:

| Bạn nhập | Signaling server, mọi client | SFU trên Android, iOS, macOS, Windows |
| --- | --- | --- |
| `192.168.1.10` | `http://192.168.1.10`, port 80 | `ws://192.168.1.10:4001` |
| `192.168.1.10:4000` | `http://192.168.1.10:4000` | `ws://192.168.1.10:4000` |
| `https://example.com` | `https://example.com` | `wss://example.com` |
| `ws://192.168.1.10:4001/ws` | không chấp nhận | `ws://192.168.1.10:4001` |
| Có path bất kỳ | Bị bỏ | Bị bỏ |

Vậy nên với signaling server, và với SFU trên web, hãy ghi kèm port. Mặc định của web client là port 4000 (signaling) và 4001 (SFU) trên host đang phục vụ trang web.
