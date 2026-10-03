# AGENTS.md

## Project Overview

**WebRTC-Demo** is a comprehensive, cross-platform WebRTC demonstration project that showcases real-time peer-to-peer communication capabilities across Web, Android, and iOS platforms. The project implements modern WebRTC APIs (currently using M125) to enable video calls, audio communication, screen sharing, and data channel messaging between multiple clients.

## Architecture

### High-Level Architecture

The project follows a client-server architecture with the following components:

```
┌─────────────────────────────────────────────────────────────┐
│                       Clients                                │
│  ┌──────────┐    ┌──────────┐    ┌──────────┐              │
│  │   Web    │    │  Android │    │   iOS    │              │
│  │ (Vue.js) │    │  (Native)│    │ (Native) │              │
│  └─────┬────┘    └─────┬────┘    └─────┬────┘              │
│        │               │               │                     │
│        └───────────────┴───────────────┘                     │
│                        │                                     │
│                        ▼                                     │
│              ┌─────────────────┐                            │
│              │  Signaling      │                            │
│              │  Server         │                            │
│              │  (Node.js +     │                            │
│              │   Socket.io)    │                            │
│              └─────────────────┘                            │
│                                                              │
│        P2P Connection (WebRTC - STUN/TURN)                  │
│        ════════════════════════════════════                  │
│        Client A ◄──────────────────► Client B               │
└─────────────────────────────────────────────────────────────┘
```

### Components

#### 1. Signaling Server (`signaling-server/`)

**Technology Stack:**
- Node.js
- Express.js
- Socket.io

**Purpose:**
The signaling server facilitates the initial peer discovery and exchange of connection information (SDP offers/answers and ICE candidates) between WebRTC clients. It does not handle the actual media streaming.

**Key Features:**
- Room-based architecture (max 2 participants per room)
- WebSocket-based real-time communication
- SDP offer/answer exchange
- ICE candidate relay
- Room management and participant tracking

**Protocol Flow:**
1. **Join Room**: Client connects and joins a room by ID
2. **Offer Exchange**: First client creates offer when second client joins
3. **Answer Exchange**: Second client responds with answer
4. **ICE Candidate Exchange**: Both peers exchange ICE candidates
5. **P2P Connection**: Direct peer-to-peer connection established

**Server Events:**
- `join room` - Client joins a specific room
- `offer` - Send WebRTC offer to peer
- `answer` - Send WebRTC answer to peer
- `new ice candidate` - Exchange ICE candidates
- `receive encryption key` - Exchange E2EE keys (web, Android, iOS)
- `media state` - Relay `{audio, video, screen}` so the peer knows when the camera or microphone is off
- `send data channel message` - Relay data channel messages
- `leave room` - Leave the current room

#### 2. Web Client (`web/`)

**Technology Stack:**
- Vue.js 3 (Composition API)
- TypeScript
- Vite (build tool)
- Socket.io-client
- Native WebRTC API

**Architecture:**
```
App.vue
├── WebRTC PeerConnection Management
├── Socket.io Connection
├── Media Stream Management
│   ├── Local Camera/Microphone
│   ├── Screen Sharing
│   ├── Backgrounds and effects (src/effects/, MediaPipe + canvas)
│   └── Remote Stream
├── Data Channel
└── E2EE (End-to-End Encryption)
    ├── Main Thread Implementation
    └── Web Worker Implementation
```

**Key Features:**

1. **Audio/Video Control**
   - Toggle microphone on/off
   - Toggle camera on/off
   - Switch between front/back camera (mobile)
   - Switch between microphone and speaker output

2. **Screen Sharing**
   - Desktop/tab/window sharing via `getDisplayMedia()`
   - Automatic track replacement in peer connection
   - Switch back to camera functionality

3. **Data Channel Messaging**
   - Real-time text messaging
   - Low-latency data transfer
   - Chat interface for peer-to-peer communication

4. **End-to-End Encryption (E2EE)**
   - **Current Status**: Web, Android and iOS interoperate
   - **Algorithm**: AES-128-GCM, key derived with PBKDF2-HMAC-SHA256 (salt `LKFrameEncryptionKey`, 100000 iterations) from 32 bytes of shared key material
   - **Native**: webrtc-sdk `FrameCryptor` (shared key mode, key index 0, no ratchet, no magic bytes)
   - **Web**: Insertable Streams (`createEncodedStreams`, or `RTCRtpScriptTransform` where available), main thread or Web Worker

   **Frame format** (same on every platform):
   ```
   [unencrypted header][AES-GCM ciphertext + 16B tag][IV 12B][IV length = 12][key index]
   ```
   - Unencrypted header: VP8 10 bytes (key frame) / 3 bytes (delta), Opus 1 byte, H264 up to the first slice NAL header + 1
   - The header is the AES-GCM additional data; for H264 the rest is RBSP-escaped

   **E2EE Process**:
   - The peer already in the room generates the key material and sends it via `send encryption key` before the offer
   - The joining peer sets the key when it receives `receive encryption key`
   - Every platform puts VP8 first in the video codec preferences when E2EE is on; iOS does it on every call so screen sharing keeps encoding in the background
   - Frames that cannot be encrypted/decrypted (no key yet, bad frame) are dropped, never forwarded in plain form

   **Files**:
   - `web/src/e2ee.ts` - Frame format, key derivation and stream transformers
   - `web/src/encryptionWorker.ts` - Web Worker for offloading crypto operations
   - `android/.../webrtc/E2eeManager.kt` - Key provider and FrameCryptor creation
   - `ios/WebRTCDemo/PeerConnectionClient.swift` - Key provider and RTCFrameCryptor lifecycle

5. **Media Streams API Integration**
   - `getUserMedia()` for camera/microphone access
   - `getDisplayMedia()` for screen sharing
   - Track replacement for switching sources

#### 3. iOS Client (`ios/`)

**Technology Stack:**
- Swift
- WebRTC framework (native)
- SwiftUI with Liquid Glass (deployment target iOS 26, Xcode 26)
- ReplayKit (for screen broadcasting)

**Project Structure:**
```
WebRTCDemo (Main App)
├── WebRTCDemoApp.swift (@main app, lobby -> call as a full screen cover)
├── LobbyView.swift (Room ID, E2EE toggle, signaling server sheet)
├── SignalingServer.swift (saved server address, normalization)
├── CallViewModel.swift (@Observable call state, Socket.IO signaling)
├── CallView.swift (Call screen: remote stage, draggable PiP, overlays, pickers)
├── CallControls.swift (Glass toolbar, share menu, More sheet)
├── ChatView.swift (Chat sheet)
├── PeerPlaceholderView.swift (Blurred last frame + speaking avatar)
├── VideoView.swift (RTCMTLVideoView wrapper, FrameSnapshotter)
├── PeerConnectionClient.swift (WebRTC logic)
├── EffectsCatalog.swift (bundled effects folder, saved selection, sticker placement)
├── EffectsProcessor.swift (Vision + Core Image backgrounds and face stickers)
├── EffectsSheet.swift (Backgrounds and filters picker with a live preview)
├── RTCCustomFrameCapturer.swift (Custom video capture)
├── RTCFileVideoCapturer+URL.swift (Video file capture)
├── FlutterBroadcastScreenCapturer.h/m (Screen capturer fed by the extension)
├── FlutterSocketConnection.h/m (Unix socket server, from flutter-webrtc)
└── FlutterSocketConnectionFrameReader.h/m (Frame decoding, from flutter-webrtc)

WebRTCDemoScreenBroadcast (Broadcast Extension)
├── SampleHandler.swift (Screen capture handler)
├── SampleUploader.swift (JPEG encoding + framing)
└── SocketConnection.swift (Unix socket client)

WebRTCDemoScreenBroadcastSetupUI (Broadcast Setup)
└── BroadcastSetupViewController.swift
```

**Key Components:**

1. **PeerConnectionClient.swift** (`WebRTCClient`)
   - Manages RTCPeerConnection lifecycle
   - Handles ICE candidate generation and exchange
   - Video/audio track management; exposes the local video track and reports the remote one to its delegate
   - Data channel creation and messaging
   - Camera switching (front/back), remote audio level from `getStats`
   - Owns no views; delegate callbacks can arrive on any thread

2. **CallViewModel.swift**
   - Socket.IO connection and signaling events, including `media state`
   - All call state the UI renders, mutated on the main queue only
   - Camera off: sends `media state` first and disables the track 300 ms later (the reverse when turning on)

3. **CallView.swift and friends**
   - SwiftUI call screen with Liquid Glass controls
   - Local video morphs from full screen into a draggable picture-in-picture when the peer joins
   - Placeholder with the blurred last frame when the peer's video is off or hidden locally

4. **Screen Broadcasting**
   - Uses ReplayKit framework
   - Broadcast Extension for system-level screen capture
   - Separate process for privacy and security

**Key Features:**
- Native WebRTC SDK integration
- Camera switching
- Audio routing (speaker/earpiece)
- Data channel messaging
- **Full-screen broadcasting (via Broadcast Extension)**
- Unix domain socket communication between extension and main app
- Automatic track switching for screen sharing

**Screen Broadcasting Architecture:**
- Uses `RPBroadcastSampleHandler` for system-wide screen capture
- Broadcast Extension captures and encodes frames to JPEG
- Unix domain socket (via App Group) transfers frames to main app
- `FlutterBroadcastScreenCapturer` feeds frames into WebRTC
- The app-side socket and frame reader are copied from the flutter-webrtc plugin; keep their framing logic (a 0-byte read is end of stream and stops the broadcast)
- Darwin notifications coordinate lifecycle between processes
- Automatic camera ↔ screen track switching

**Configuration:**
- Server URL is edited in the lobby and saved in `UserDefaults` (`SignalingServer.swift`); `Info.plist` allows plain HTTP
- Deployment target iOS 26 for every target (Liquid Glass APIs); the pods keep their own minimums
- The Xcode project does not use synchronized folders: add new source files to `project.pbxproj` (or through Xcode)

#### 4. Android Client (`android/`)

**Technology Stack:**
- Kotlin (native Android)
- Jetpack Compose with Material 3 Expressive (`material3` `1.5.0-alpha18`, Compose BOM `2026.06.01`)
- WebRTC framework for Android
- Gradle build system (AGP 8.13.2, Gradle 9.5.1, Kotlin 2.3.0)

**Project Structure:**
```
android/app/src/main/java/com/example/myapplication/
├── MainActivity.kt (hosts LobbyScreen)
├── CallActivity.kt (hosts CallScreen: permissions, pickers, MediaProjection)
├── ScreenCaptureService.kt (foreground service for screen capture)
├── call/CallViewModel.kt (call state as StateFlow, owns PeerConnectionClient + EglBase)
├── effects/EffectsCatalog.kt (reads assets/effects, saved selection)
├── settings/SignalingServer.kt (saved server address, normalization)
├── ui/
│   ├── lobby/LobbyScreen.kt
│   ├── call/ (CallScreen, CallControls, ChatSheet, EffectsSheet, PeerPlaceholder, CallPreviews)
│   ├── video/ (TextureViewRenderer, VideoRenderer, FrameSnapshotter)
│   └── theme/Theme.kt (MaterialExpressiveTheme, dynamic color)
└── webrtc/ (PeerConnectionClient, WebRtcPeer, RtcListener, SignalingHandler, E2eeManager, Mp4VideoCapturer, effects/)
```

**Key Features:**
- Native WebRTC integration for Android
- Camera and microphone access
- Screen sharing (device screen capture)
- Data channel messaging
- Audio/video controls, local-only peer mute and hide video
- Draggable rounded picture-in-picture, fit/fill remote video

**Configuration:**
- Server address is edited in the lobby and saved in `SharedPreferences` (`settings/SignalingServer.kt`); the default is in `res/values/strings.xml`
- Permissions for camera, microphone, and internet in AndroidManifest.xml
- Icons are Material Symbols vector drawables in `res/drawable/ic_*.xml`
- Material 3 Expressive is alpha-only; newer Compose BOMs need AGP 9.1 and compileSdk 37
- Video is rendered with `TextureViewRenderer` (TextureView + EglRenderer) so it can be clipped and animated in Compose; it always center-crops, so `VideoSurface` sizes it to cover the screen at the frame's aspect ratio and animates a scale down for fit
- `CallContent` is stateless with video slots; `CallPreviews.kt` renders it with fake video for previews and screenshot tests

## WebRTC Core Concepts

### Peer Connection Lifecycle

```
1. Initialize RTCPeerConnection
         │
         ▼
2. Add Local Media Tracks (getUserMedia)
         │
         ▼
3. Create Offer (Caller) / Wait for Offer (Callee)
         │
         ▼
4. Set Local Description (SDP)
         │
         ▼
5. Send SDP to Remote Peer (via signaling)
         │
         ▼
6. Receive Remote SDP & Set Remote Description
         │
         ▼
7. Exchange ICE Candidates
         │
         ▼
8. ICE Connection Established
         │
         ▼
9. Media Flows Directly Between Peers
```

### ICE (Interactive Connectivity Establishment)

The project uses Google's public STUN server:
- STUN Server: `stun:stun.l.google.com:19302`
- Purpose: NAT traversal, discover public IP addresses
- No TURN server configured (may fail behind restrictive NATs)

### Data Channel

- **Use Case**: Text messaging between peers
- **Setup**: The offerer creates the channel before its first offer; opening chat never renegotiates
- **Reliability**: Configurable (reliable/unreliable)
- **Ordering**: Can be ordered or unordered
- **Low Latency**: Direct P2P, bypasses signaling server

## Setup and Installation

### Prerequisites
- Node.js 20.19+ and npm/yarn
- iOS: Xcode 26, CocoaPods, a device on iOS 26+
- Android: Android Studio, Android SDK (compileSdk 36), JDK 17+
- Modern web browser with WebRTC support

### Quick Start

1. **Start Signaling Server**
   ```bash
   cd signaling-server
   npm install
   npm run dev
   ```
   Server runs on port 4000 and displays local IP address

2. **Start Web Client**
   ```bash
   cd web
   npm install
   npm run dev
   ```
   Available at `http://localhost:5173`. It connects to port 4000 on the same host; change the address in the lobby if the server runs elsewhere

3. **iOS Setup**
   ```bash
   cd ios
   pod install
   ```
   - Open `WebRTCDemo.xcworkspace` in Xcode
   - Build and run, then set the signaling server address in the lobby

4. **Android Setup**
   - Open project in Android Studio
   - Build and run, then set the signaling server address in the lobby

### Usage Flow

1. Start signaling server and note the IP address
2. Launch any two clients (Web, iOS, or Android)
3. Enter the same **Room ID** on both clients
4. First client waits in the room
5. Second client joins and connection is established
6. Start communicating with audio, video, and messages

## Technical Deep Dive

### End-to-End Encryption Implementation (Web)

The E2EE implementation uses the **Insertable Streams API** (also known as WebRTC Encoded Transform):

```typescript
const options = { kind, getKey: () => encryptionKey, getCodecMap: () => codecMap };

// For sender (encoding)
const senderStreams = sender.createEncodedStreams();
encryptStream(options, senderStreams.readable, senderStreams.writable);

// For receiver (decoding)
const receiverStreams = receiver.createEncodedStreams();
decryptStream(options, receiverStreams.readable, receiverStreams.writable);
```

**Transform Pipeline**:
- Intercept encoded video/audio frames
- Apply AES-GCM encryption/decryption in the native FrameCryptor format
- IV and trailer are appended to each frame
- Frames remain opaque to intermediaries

**Worker Architecture** (optional):
```
Main Thread                    Encryption Worker
───────────                    ─────────────────
Generate Key ──────────────► Store Key
                              
Encoded Frame ────────────► Encrypt Frame
                              │
                              ▼
Encrypted Frame ◄───────── Return Encrypted
```

### Room Management

Rooms are temporary and in-memory:
- **Maximum Capacity**: 2 participants
- **Lifecycle**: Created when first user joins, implicitly destroyed when empty
- **Identification**: String-based room IDs
- **Validation**: Prevents duplicate joins, full room notifications

### Media Stream Handling

**Web Client Flow**:
1. Request permissions: `navigator.mediaDevices.getUserMedia()`
2. Create local stream with constraints (resolution, frame rate)
3. Add tracks to peer connection: `peerConnection.addTrack()`
4. Display local preview: `videoElement.srcObject = stream`
5. Receive remote stream via `track` event
6. Render remote video

**Native Clients**:
- Use platform-specific WebRTC SDK APIs
- Similar flow with native API equivalents
- Hardware acceleration for encoding/decoding

## Features Comparison

| Feature                    | Web | Android | iOS |
|---------------------------|-----|---------|-----|
| Audio Control             | ✅  | ✅      | ✅  |
| Video Control             | ✅  | ✅      | ✅  |
| Camera Switching          | ✅  | ✅      | ✅  |
| Speaker/Microphone Toggle | ✅  | ✅      | ✅  |
| Screen Sharing            | ✅  | ✅      | ✅  |
| Data Channel Messaging    | ✅  | ✅      | ✅  |
| End-to-End Encryption     | ✅  | ✅      | ✅  |
| Backgrounds (blur, picture, video) | ✅  | ✅      | ✅  |
| Face Stickers             | ✅  | ✅      | ✅  |
| Stream Video File         | ✅  | ✅      | ✅  |
| Camera/Mic State to Peer  | ✅  | ✅      | ✅  |
| Hide Remote Video Locally | ✅  | ✅      | ✅  |

✅ = Supported | ❌ = Not Supported | 🚧 = In Progress

## Known Issues & Troubleshooting

### iOS Issues

**Issue 1: Minimum Deployment Target**
- **Error**: "Compiling for iOS 11.0, but module..."
- **Solution**: Update pod's Minimum Deployment to latest iOS version in Xcode project settings

**Issue 2: Sandbox rsync Error**
- **Error**: `Sandbox: rsync.samba(13105)...`
- **Solution**: Set `ENABLE_USER_SCRIPT_SANDBOXING` to `No` in Xcode build options

### General Issues

- **Connection Fails**: Check firewall settings, ensure signaling server is accessible
- **No Video/Audio**: Verify permissions granted for camera/microphone
- **NAT Traversal**: May need TURN server for restrictive networks
- **E2EE Performance**: Use Web Worker mode to prevent UI freezing

## Future Enhancements

1. **E2EE Key Agreement**
   - Replace sending raw key material through the signaling server with ECDH or a passphrase

2. **Stream Video Files**
   - Play video files to remote peer
   - Use `RTCVideoSource` with custom capturer

3. **Multi-Party Conferencing**
   - Mesh, SFU, or MCU architecture
   - More than 2 participants per room

4. **Recording**
   - Record calls locally
   - Server-side recording option

5. **TURN Server Integration**
   - Better connectivity in restrictive networks
   - Fallback for failed STUN connections

6. **Advanced Features**
   - Simulcast for adaptive bitrate
   - SVC (Scalable Video Coding)
   - Noise suppression

## Security Considerations

### Current Implementation

- **E2EE**: Web, Android and iOS; the key material goes through the signaling server in plain form
- **Signaling**: Unencrypted WebSocket (not production-ready)
- **Authentication**: No user authentication implemented
- **Room Access**: Anyone with room ID can join

### Production Recommendations

1. **Use WSS (WebSocket Secure)** for signaling
2. **Implement authentication** (JWT, OAuth)
3. **Room access control** with passwords or invitations
4. **HTTPS** for web client
5. **E2EE key agreement** instead of sending the key through signaling
6. **Validate and sanitize** all inputs
7. **Rate limiting** on signaling server

## Development Guidelines

### Code Organization

- **Separation of Concerns**: WebRTC logic separated from UI
- **Reusability**: Platform-specific wrappers around WebRTC
- **Error Handling**: Comprehensive error handling for network issues
- **Logging**: Debug logs for troubleshooting
- **Shared effects**: Backgrounds and stickers live once in `effects/` at the repository root and are bundled by all three apps. Add backgrounds through `effects-source/` and `tools/prepare_effects.py` (see the README); keep the sticker placement code (`placement.ts`, `StickerPlacement.kt`, `StickerPlacement` in `EffectsCatalog.swift`) in sync across platforms

### Testing Strategies

1. **Local Testing**: Multiple browser tabs/windows
2. **Network Testing**: Different networks (WiFi, cellular)
3. **Cross-Platform**: Test web-to-native, native-to-native
4. **Load Testing**: Signaling server under multiple rooms
5. **Edge Cases**: Disconnections, reconnections, poor network

## Contributing

When contributing to this project:

1. Follow the existing code style
2. Test across all platforms when possible
3. Update this documentation for significant changes
4. Handle errors gracefully
5. Add logging for debugging purposes

## License

Refer to the LICENSE file in the repository root.

## Disclaimer

This project is intended for **educational and demonstration purposes** to showcase WebRTC capabilities across multiple platforms. It may contain bugs and is not production-ready. Use with caution in production environments.

---

**Last Updated**: October 2026  
**WebRTC Version**: M150 (webrtc-sdk `150.7871.01`)  
**Maintainer**: Project repository maintainers