# 移植到新平台

如果你想开发第六个客户端（比如基于 Flutter、React Native 或 Qt，或者运行在 Linux、电视上），并让它能和现有的五个应用通话，这一页就是为你准备的。下面按大致的开发顺序，列出了客户端需要做的所有事情。做完一项勾掉一项，勾选状态会保存在当前浏览器中。

你的平台只需要提供三样东西：一个 WebRTC 实现（浏览器自带的、[webrtc-sdk](https://github.com/webrtc-sdk) 或 libwebrtc）、一个 WebSocket 客户端和一个 JSON 解析器，别的都不需要。建议对照 [Web 客户端](/zh/platforms/web)阅读这份清单：它的 1:1 通话引擎 [`useCall.ts`](gh:web/src/call/useCall.ts) 在一个文件里完成了下面的每一步。

<PortingChecklist>

### 1. 信令与通话 {#_1-signaling-and-the-call}

- 用户加入时，建立到 `ws://<host>:<port>/ws` 的 WebSocket 连接，并发送 `{"type":"join","roomId":"..."}`。用户离开时，先发送 `{"type":"leave"}`，再关闭连接。
- 收到 `fatal: true` 的 `error` 时，结束通话并显示其中的 `message`（比如 `Room is full`）。
- ICE server 使用 `stun:stun.l.google.com:19302`。
- 收到 `peer joined` 时，你就是 **offer 方**。创建 peer connection，添加麦克风 track 和一个 `sendrecv` 视频 transceiver（带上摄像头 track，没有摄像头就不带），创建名为 `"MyApp Channel"` 的 data channel，然后发送 `{"type":"offer","sdp":"..."}`。
- 如果你是第二个加入的，等待 `offer`。把它设为 remote description，添加你的 track；如果你还没有摄像头，把 offer 中的 `recvonly` 视频 transceiver 改成 `sendrecv`；然后发送 `{"type":"answer","sdp":"..."}`。
- 每个本地 ICE candidate 都以 `{"type":"candidate","candidate":{"candidate":"...","sdpMid":"0","sdpMLineIndex":0}}` 的形式发送。在 remote description 设置好之前，收到的远端 candidate 先放进队列。
- 通话中信令 socket 关闭时，结束通话并告知用户，不要重连。
- 来自已关闭或已被替换的 peer connection 的回调，一律忽略。

### 2. 摄像头和麦克风 {#_2-camera-and-microphone}

- 两端连接成功后发送一次 `{"type":"media state","state":{"audio":true,"video":true,"screen":false}}`，之后每次状态变化时再发送。
- 关闭摄像头：先发送状态，300 ms 后禁用 track，然后释放摄像头。
- 打开摄像头：先打开摄像头并启用 track，300 ms 后再发送状态。
- 对端的状态为 `video: false` 时，显示占位画面而不是对方的视频。状态为 `screen: true` 时，以 fit 方式显示对方的视频，而不是裁剪。
- 静音对方的音频或隐藏对方的视频只在本地生效，不发送任何消息。

### 3. 共享与切换视频源 {#_3-sharing-and-switching-sources}

- 在**同一个视频 sender** 上切换摄像头、屏幕和视频文件（`replaceTrack()`、`setTrack()`，或者让不同的采集器向同一个 video source 供帧）。切换时绝不重新协商。
- 永远不替换麦克风 track，这样共享期间静音依然有效。
- 共享屏幕或文件期间，在 `media state` 中设置 `screen: true`。

### 4. 聊天 {#_4-chat}

- 如果你是 answer 方，从 `ondatachannel` 中拿到通道。
- 每条聊天消息都作为一条普通的 UTF-8 文本消息在通道上发送，发送前去掉首尾空白。收到的文本原样显示。
- 如果 peer connection 还在而通道关闭了，视为对方已挂断。

### 5. 端到端加密 {#_5-end-to-end-encryption}

- 如果开启了 E2EE 且你是 offer 方，生成 32 个随机字节，以 `{"type":"encryption key","key":"<base64>"}` 的形式在 offer **之前**发送。
- 收到 `encryption key` 时，设置密钥并回复 `{"type":"encryption key received"}`。
- 使用 webrtc-sdk 或 libwebrtc 时，用 `FrameCryptor` 并配置这些 [key provider 选项](/zh/how-it-works/e2ee#the-key)：共享密钥，salt 为 `LKFrameEncryptionKey`，ratchet window 为 0，不使用 magic bytes，failure tolerance 为 -1，key ring size 为 16，PBKDF2，key index 为 0。
- 没有 FrameCryptor 时，自己实现[帧格式](/zh/how-it-works/e2ee#the-frame-format)：用 PBKDF2-HMAC-SHA256 迭代 100 000 次派生出 AES-128-GCM 密钥，开头是各 codec 不加密的头部（作为附加数据），后面依次是密文和 16 字节的 tag、IV（12 字节）、`0x0C` 和 key index `0`。该页的交互演示展示了具体的字节。
- 给每个 sender 挂上加密器，给每个 receiver 挂上解密器，并立即启用。
- 在创建 offer 或 answer 之前，把 VP8 排在视频 codec 首选项的第一位。
- 无法加密或解密的帧一律丢弃，绝不以明文形式发送或解码。

### 6. 多人通话（可选） {#_6-group-calls-optional}

- 连接 `ws://<host>:4001/ws` 上的 SFU，发送 `{"type":"join","roomId":"...","name":"YourPlatform","e2ee":false}`。开启 E2EE 时，设置 `e2ee: true`，并把 32 个随机字节（base64）放在 `e2eeKey` 中。
- 收到 `joined` 后，用其中的 `e2eeKey` 作为房间密钥，发送你的 `media state`，然后创建 **publish** 连接：一个 `sendonly` 音频 transceiver 和一个 `sendonly` 视频 transceiver，VP8 排首位，挂好加密器。以 `pc: "publish"` 发送一次 offer，之后永远不再对它重新协商。
- 以 `pc: "subscribe"` 回复每一个 `offer`，按顺序逐个处理。设置好 remote description 之后、回复 answer 之前，给每个 receiver 挂上解密器。
- 发送的每个 candidate 都要带上 `pc: "publish"` 或 `pc: "subscribe"`，收到的 candidate 也按 `pc` 分发到对应的连接。
- 按 stream ID（`streams[0].id`）把每个远端 track 映射到对应的参与者。注意处理被复用的 m-line：重新协商之后，同一个 transceiver 可能承载的是另一个人的 track。
- 根据 `joined` 和 `participant joined` 创建小窗，收到 `participant left` 时移除。track 可能在参与者信息之前或之后到达。
- 聊天消息以 `{"type":"chat","text":"..."}` 的形式发送。收到的聊天消息带有发送者的 `participantId` 和 `name`。
- 大约每 300 ms 从每个远端音频 receiver 的统计信息中读取一次 `audioLevel`，用来找出当前说话的人。

### 7. 与现有应用对照测试 {#_7-check-it-against-the-existing-apps}

- 分别以两种角色呼叫 Web 客户端：一次是你的客户端先进入房间，一次是后加入。
- 关闭再打开摄像头。Web 客户端会显示你的占位画面，恢复时不会出现黑屏或画面卡住。
- 共享屏幕。Web 客户端会以 fit 方式显示。
- 双向发送聊天消息。
- 双方都开启 E2EE：音视频双向正常。然后只在一方开启：另一方看不到也听不到任何内容，这是预期行为。
- 开启 E2EE 呼叫一个 Android 或 iOS 客户端，验证与原生 `FrameCryptor` 的互通。
- 用 Web 客户端和一个原生客户端加入多人通话，分别在开启和关闭 E2EE 的情况下测试。离开后再重新加入。
- 在通话中停掉信令服务器。你的客户端应该结束通话并给出提示。

</PortingChecklist>

## 建议 {#tips}

- **先不做 E2EE 和特效。** 一个能和 Web 客户端互通的普通 1:1 通话，是其他所有功能的基础。
- **记录收发的每一条消息。** 信令服务器也会记录加入事件。大多数互通 bug 都是少了某个字段，或者消息顺序不对。
- **在多人通话的 `join` 中写上你的平台名**（`name`）。其他应用会把它显示在你的小窗和成员列表中。
- **如果你基于 webrtc-sdk**，Android 和 iOS 的代码几乎可以逐行对照，因为它们用的是同一个 SDK。如果直接基于 libwebrtc，可以看看 Windows 的 [C shim 头文件](gh:windows/native/RtcShim/include/rtc_shim.h)，里面列出了一个完整客户端需要的所有原生调用。

所有消息、字段和错误都列在[消息](/zh/reference/messages)中。
