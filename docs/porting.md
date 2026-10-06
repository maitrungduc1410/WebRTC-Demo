# Port to a new platform

This page is for building a sixth client, for example in Flutter, React Native, Qt, on Linux or on a TV, that can call the five existing apps. It lists everything your client has to do, in the order you will probably build it. Tick items off as you go. The list is saved in this browser.

You need three things from your platform: a WebRTC implementation (the browser's, [webrtc-sdk](https://github.com/webrtc-sdk) or libwebrtc), a WebSocket client and a JSON parser. Nothing else is required. Read the [web client](/platforms/web) alongside this list: its 1:1 engine, [`useCall.ts`](gh:web/src/call/useCall.ts), does every step below in one file.

<PortingChecklist>

### 1. Signaling and the call {#_1-signaling-and-the-call}

- Open a WebSocket to `ws://<host>:<port>/ws` when the user joins and send `{"type":"join","roomId":"..."}`. Close it when they leave, after sending `{"type":"leave"}`.
- On `error` with `fatal: true`, end the call and show its `message` (for example `Room is full`).
- Use `stun:stun.l.google.com:19302` as the ICE server.
- On `peer joined` you are the **offerer**. Create the peer connection, add the microphone track and a `sendrecv` video transceiver (with the camera track, or none if there is no camera), create the data channel `"MyApp Channel"`, then send `{"type":"offer","sdp":"..."}`.
- If you joined second, wait for `offer`. Set it as the remote description, add your tracks, turn the offer's `recvonly` video transceiver into `sendrecv` if you have no camera yet, and send `{"type":"answer","sdp":"..."}`.
- Send each local ICE candidate as `{"type":"candidate","candidate":{"candidate":"...","sdpMid":"0","sdpMLineIndex":0}}`. Queue remote candidates until the remote description is set.
- When the signaling socket closes during a call, end the call and tell the user. Don't reconnect.
- Ignore every callback from a peer connection you have already closed or replaced.

### 2. Camera and microphone {#_2-camera-and-microphone}

- Send `{"type":"media state","state":{"audio":true,"video":true,"screen":false}}` once the peers connect, and again on every change.
- Camera off: send the state first, disable the track 300 ms later, then release the camera.
- Camera on: open the camera and enable the track first, send the state 300 ms later.
- When the other side's state says `video: false`, show a placeholder instead of their video. When it says `screen: true`, show their video fit instead of cropped.
- Mute the other person's audio or hide their video locally only. Send nothing.

### 3. Sharing and switching sources {#_3-sharing-and-switching-sources}

- Switch between camera, screen and video file on the **same video sender** (`replaceTrack()`, `setTrack()`, or one video source fed by different capturers). Never renegotiate to switch.
- Never replace the microphone track, so muting keeps working while sharing.
- Set `screen: true` in `media state` while a screen or a file is shared.

### 4. Chat {#_4-chat}

- If you answered, take the channel from `ondatachannel`.
- Send each chat message as one plain UTF-8 text message on the channel, trimmed. Show received text as is.
- If the channel closes while the peer connection is still up, treat it as the other side hanging up.

### 5. End-to-end encryption {#_5-end-to-end-encryption}

- If E2EE is on and you are the offerer, generate 32 random bytes and send them as `{"type":"encryption key","key":"<base64>"}` **before** the offer.
- On `encryption key`, set the key and reply `{"type":"encryption key received"}`.
- With webrtc-sdk or libwebrtc, use `FrameCryptor` with the [key provider options](/how-it-works/e2ee#the-key): shared key, salt `LKFrameEncryptionKey`, ratchet window 0, no magic bytes, failure tolerance -1, key ring size 16, PBKDF2, key index 0.
- Without a FrameCryptor, implement the [frame format](/how-it-works/e2ee#the-frame-format) yourself: PBKDF2-HMAC-SHA256 with 100 000 iterations to an AES-128-GCM key, the per-codec unencrypted header in front as additional data, then the ciphertext with its 16-byte tag, the IV (12 bytes), `0x0C` and key index `0`. The interactive demo on that page shows the exact bytes.
- Attach an encryptor to every sender and a decryptor to every receiver, and enable them at once.
- Put VP8 first in the video codec preferences before creating the offer or the answer.
- Drop any frame you can't encrypt or decrypt. Never send or decode it in plain form.

### 6. Group calls (optional) {#_6-group-calls-optional}

- Connect to the SFU at `ws://<host>:4001/ws` and send `{"type":"join","roomId":"...","name":"YourPlatform","e2ee":false}`. With E2EE, set `e2ee: true` and add 32 random bytes as `e2eeKey` (base64).
- On `joined`, use its `e2eeKey` as the room key, send your `media state`, then create the **publish** connection: one `sendonly` audio and one `sendonly` video transceiver, VP8 first, encryptors attached. Offer once with `pc: "publish"` and never renegotiate it.
- Answer every `offer` with `pc: "subscribe"`, one at a time, in order. Attach decryptors to every receiver after setting the remote description and before answering.
- Add `pc: "publish"` or `pc: "subscribe"` to every candidate you send, and route received candidates by their `pc`.
- Map each remote track to a participant by its stream ID (`streams[0].id`). Handle reused m-lines: the same transceiver can carry someone else's track after a renegotiation.
- Create tiles from `joined` and `participant joined`, remove them on `participant left`. Tracks can arrive before or after the participant.
- Send chat as `{"type":"chat","text":"..."}`. Received chat carries the sender's `participantId` and `name`.
- Read `audioLevel` from each remote audio receiver's stats every 300 ms or so to find the active speaker.

### 7. Check it against the existing apps {#_7-check-it-against-the-existing-apps}

- Call the web client in both roles: once with your client in the room first, once joining second.
- Turn your camera off and on. The web client shows your placeholder and comes back without a black or frozen frame.
- Share your screen. The web client shows it fit.
- Chat in both directions.
- Turn E2EE on on both sides: audio and video work both ways. Then turn it on one side only: the other side sees and hears nothing, which is expected.
- Call an Android or iOS client with E2EE on, to check against the native `FrameCryptor`.
- Join a group call with the web client and one native client, with and without E2EE. Leave and join again.
- Stop the signaling server during a call. Your client ends the call with a message.

</PortingChecklist>

## Tips {#tips}

- **Start without E2EE and without effects.** A plain 1:1 call that interoperates with the web client is the foundation for everything else.
- **Log every message you send and receive.** The signaling server logs joins too. Most interop bugs are a missing field or a message in the wrong order.
- **Name your platform** in the group `join` (`name`). The other apps show it on your tile and in the people list.
- **If you are on webrtc-sdk**, the Android and iOS code maps almost line by line, since they use the same SDK. On raw libwebrtc, read the Windows [C shim header](gh:windows/native/RtcShim/include/rtc_shim.h) for the list of native calls a full client needs.

Every message, field and error is listed in [Messages](/reference/messages).
