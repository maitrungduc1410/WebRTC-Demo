# Versions and limitations

## Versions {#versions}

| Component | Version | Where it is set |
| --- | --- | --- |
| webrtc-sdk Android | `io.github.webrtc-sdk:android:150.7871.01` | [`build.gradle.kts`](gh:android/app/build.gradle.kts) |
| webrtc-sdk iOS, macOS | `webrtc-sdk/Specs` `150.7871.01` binary, checksum pinned, through a local package | [`Package.swift`](gh:ios/Packages/WebRTC/Package.swift) |
| libwebrtc Windows | `libwebrtc.m150.7871.03` (webrtc-sdk/libwebrtc release), SHA-256 pinned | [`libwebrtc.lock.json`](gh:windows/native/RtcShim/libwebrtc.lock.json) |
| Windows App SDK | `2.5.1`, .NET `10`, CommunityToolkit.Mvvm `8.4.2`, Vortice `3.8.3` | [`Directory.Packages.props`](gh:windows/Directory.Packages.props) |
| Windows effects | ONNX Runtime `1.24.4` with Windows ML, models converted with tf2onnx `1.16.1`, opset 17 | [`convert.sh`](gh:windows/models/convert.sh) |
| MediaPipe Android | `com.google.mediapipe:tasks-vision:1.0.0` | [`build.gradle.kts`](gh:android/app/build.gradle.kts) |
| MediaPipe Web | `@mediapipe/tasks-vision`, models from `storage.googleapis.com` | [`package.json`](gh:web/package.json) |
| Jetpack Compose | BOM `2026.06.01`, `material3` `1.5.0-alpha18` | [`build.gradle.kts`](gh:android/app/build.gradle.kts) |
| Android build | AGP `8.13.2`, Gradle `9.5.1`, Kotlin `2.3.0`, compileSdk 36, minSdk 24 | [`android/`](gh:android) |
| iOS, macOS | Deployment target 26.0, Xcode 26 | [`WebRTCDemo.xcodeproj`](gh:ios/WebRTCDemo.xcodeproj) |
| sfu-server | Go 1.25, `pion/webrtc/v4` `v4.2.22`, `gorilla/websocket` `v1.5.3` | [`go.mod`](gh:sfu-server/go.mod) |

All native WebRTC builds come from the m150 branch of the webrtc-sdk fork.

## Build commands {#build-commands}

```text
signaling-server:  npm install && npm run dev                  (port 4000)
sfu-server:        go run .   (optional, group calls)           (TCP + UDP 4001)
                   go test -race ./...
web:               npm install && npm run dev                  (http://localhost:5173)
android:           ./gradlew :app:assembleDebug
ios / macOS:       open ios/WebRTCDemo.xcodeproj; scheme WebRTCDemo or WebRTCDemoMac
windows:           ./native/RtcShim/scripts/build-shim.ps1
                   dotnet run --project src/WebRtcDemo.App -p:Platform=x64
docs:              cd docs && npm install && npm run dev
```

## Limitations {#limitations}

These are known and mostly on purpose. It is a demo.

- **1:1 by default.** A default room holds two people. Group calls need the optional SFU, with 8 people per room by default.
- **No TURN server.** Calls can fail behind symmetric NATs or strict firewalls.
- **No reconnect.** A closed signaling socket ends the call.
- **Signaling isn't secured.** Plain HTTP and WebSocket, no authentication. Anyone with the room ID can join.
- **The E2EE key goes through the server in plain form.** A real app should use a key agreement (for example ECDH) or a passphrase shared out of band.
- **E2EE is chosen in the lobby** and can't be toggled during a call. Both peers must choose the same setting.
- **Group calls: no simulcast and no downlink adaptation.** Every subscriber gets each publisher's single stream. A slow subscriber can't ask for a lower layer.
- **Group calls: one server, IPv4 UDP only.** Clients must reach UDP 4001 directly. Rooms live in one process's memory.
- **Group calls: chat goes through the server** in plain text over the signaling WebSocket, unlike the 1:1 data channel.
- **iOS always sends VP8.** It is encoded in software, so it uses more CPU and battery than hardware H264. That is what keeps screen sharing alive in the background.
- **macOS screen sharing needs Screen Recording permission**, and the app must be relaunched after granting it.
- **Windows composites effects on the CPU.** The models can use the GPU or NPU, but blending, blur and color conversion run in C# on one thread, about 13 ms per 720p frame, so a slow PC sends effects at a lower frame rate.
- **Windows plays video files** only in formats Media Foundation can decode on that PC.
