# 版本与限制

## 版本 {#versions}

| 组件 | 版本 | 在哪里设置 |
| --- | --- | --- |
| webrtc-sdk Android | `io.github.webrtc-sdk:android:150.7871.01` | [`build.gradle.kts`](gh:android/app/build.gradle.kts) |
| webrtc-sdk iOS、macOS | `webrtc-sdk/Specs` `150.7871.01` 二进制包，固定校验和，通过本地 package 引入 | [`Package.swift`](gh:ios/Packages/WebRTC/Package.swift) |
| libwebrtc Windows | `libwebrtc.m150.7871.03`（webrtc-sdk/libwebrtc 发布版），固定 SHA-256 | [`libwebrtc.lock.json`](gh:windows/native/RtcShim/libwebrtc.lock.json) |
| Windows App SDK | `2.5.1`，.NET `10`，CommunityToolkit.Mvvm `8.4.2`，Vortice `3.8.3` | [`Directory.Packages.props`](gh:windows/Directory.Packages.props) |
| Windows 特效 | ONNX Runtime `1.24.4`（配合 Windows ML），模型用 tf2onnx `1.16.1` 转换，opset 17 | [`convert.sh`](gh:windows/models/convert.sh) |
| MediaPipe Android | `com.google.mediapipe:tasks-vision:1.0.0` | [`build.gradle.kts`](gh:android/app/build.gradle.kts) |
| MediaPipe Web | `@mediapipe/tasks-vision`，模型从 `storage.googleapis.com` 加载 | [`package.json`](gh:web/package.json) |
| Jetpack Compose | BOM `2026.06.01`，`material3` `1.5.0-alpha18` | [`build.gradle.kts`](gh:android/app/build.gradle.kts) |
| Android 构建 | AGP `8.13.2`，Gradle `9.5.1`，Kotlin `2.3.0`，compileSdk 36，minSdk 24 | [`android/`](gh:android) |
| iOS、macOS | Deployment target 26.0，Xcode 26 | [`WebRTCDemo.xcodeproj`](gh:ios/WebRTCDemo.xcodeproj) |
| sfu-server | Go 1.25，`pion/webrtc/v4` `v4.2.22`，`gorilla/websocket` `v1.5.3` | [`go.mod`](gh:sfu-server/go.mod) |

所有原生 WebRTC 版本都构建自 webrtc-sdk fork 的 m150 分支。

## 构建命令 {#build-commands}

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

## 限制 {#limitations}

以下都是已知问题，而且大多是有意为之，毕竟这只是一个 Demo。

- **默认只支持 1:1。** 默认房间只能容纳两个人。多人通话需要额外部署可选的 SFU，默认每个房间 8 人。
- **没有 TURN 服务器。** 在对称型 NAT 或严格的防火墙之后，通话可能会失败。
- **不会自动重连。** 信令 socket 关闭就会结束通话。
- **信令没有安全保护。** 使用明文 HTTP 和 WebSocket，没有身份认证。任何知道房间 ID 的人都能加入。
- **E2EE 密钥以明文形式经过服务器。** 真实的应用应该使用密钥协商（比如 ECDH）或通过其他渠道共享的口令。
- **E2EE 在大厅中选择**，通话中不能切换。双方必须选择相同的设置。
- **多人通话：没有 simulcast，也没有下行自适应。** 每个订阅者收到的都是发布者唯一的那一路 stream，网速慢的订阅者无法请求更低的层级。
- **多人通话：只有一台服务器，只支持 IPv4 UDP。** 客户端必须能直接访问 UDP 4001。房间保存在单个进程的内存中。
- **多人通话：聊天经由服务器转发**，在信令 WebSocket 上以明文传输，这一点与 1:1 的 data channel 不同。
- **iOS 始终发送 VP8。** 它是软件编码的，比 H264 硬件编码更耗 CPU 和电量，但正是这样才能让屏幕共享在后台持续工作。
- **macOS 屏幕共享需要屏幕录制权限**，授权之后必须重新启动应用。
- **Windows 在 CPU 上合成特效。** 模型可以使用 GPU 或 NPU，但混合、模糊和颜色转换都在 C# 中单线程运行，每帧 720p 大约需要 13 ms，所以在较慢的电脑上，开启特效后发送的帧率会降低。
- **Windows 播放视频文件**时，只支持该电脑上 Media Foundation 能解码的格式。
