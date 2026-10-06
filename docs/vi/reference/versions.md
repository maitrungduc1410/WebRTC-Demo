# Phiên bản và giới hạn

## Phiên bản {#versions}

| Thành phần | Phiên bản | Đặt ở đâu |
| --- | --- | --- |
| webrtc-sdk Android | `io.github.webrtc-sdk:android:150.7871.01` | [`build.gradle.kts`](gh:android/app/build.gradle.kts) |
| webrtc-sdk iOS, macOS | Binary `webrtc-sdk/Specs` `150.7871.01`, ghim checksum, qua một local package | [`Package.swift`](gh:ios/Packages/WebRTC/Package.swift) |
| libwebrtc Windows | `libwebrtc.m150.7871.03` (release của webrtc-sdk/libwebrtc), ghim SHA-256 | [`libwebrtc.lock.json`](gh:windows/native/RtcShim/libwebrtc.lock.json) |
| Windows App SDK | `2.5.1`, .NET `10`, CommunityToolkit.Mvvm `8.4.2`, Vortice `3.8.3` | [`Directory.Packages.props`](gh:windows/Directory.Packages.props) |
| Effect trên Windows | ONNX Runtime `1.24.4` với Windows ML, model chuyển đổi bằng tf2onnx `1.16.1`, opset 17 | [`convert.sh`](gh:windows/models/convert.sh) |
| MediaPipe Android | `com.google.mediapipe:tasks-vision:1.0.0` | [`build.gradle.kts`](gh:android/app/build.gradle.kts) |
| MediaPipe Web | `@mediapipe/tasks-vision`, model lấy từ `storage.googleapis.com` | [`package.json`](gh:web/package.json) |
| Jetpack Compose | BOM `2026.06.01`, `material3` `1.5.0-alpha18` | [`build.gradle.kts`](gh:android/app/build.gradle.kts) |
| Build Android | AGP `8.13.2`, Gradle `9.5.1`, Kotlin `2.3.0`, compileSdk 36, minSdk 24 | [`android/`](gh:android) |
| iOS, macOS | Deployment target 26.0, Xcode 26 | [`WebRTCDemo.xcodeproj`](gh:ios/WebRTCDemo.xcodeproj) |
| sfu-server | Go 1.25, `pion/webrtc/v4` `v4.2.22`, `gorilla/websocket` `v1.5.3` | [`go.mod`](gh:sfu-server/go.mod) |

Mọi bản build WebRTC native đều lấy từ nhánh m150 của bản fork webrtc-sdk.

## Lệnh build {#build-commands}

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

## Giới hạn {#limitations}

Đây là những giới hạn đã biết, và phần lớn là cố ý. Đây là một demo.

- **Mặc định là 1:1.** Phòng mặc định chứa hai người. Cuộc gọi nhóm cần SFU (không bắt buộc), mặc định 8 người mỗi phòng.
- **Không có TURN server.** Cuộc gọi có thể thất bại khi nằm sau symmetric NAT hoặc firewall chặt.
- **Không tự kết nối lại.** Socket signaling đóng thì cuộc gọi kết thúc.
- **Signaling không được bảo mật.** HTTP và WebSocket thường, không có xác thực. Ai có room ID cũng vào được.
- **Key E2EE đi qua server ở dạng chưa mã hóa.** Một app thật nên dùng cơ chế thỏa thuận key (ví dụ ECDH) hoặc một passphrase chia sẻ qua kênh khác.
- **E2EE được chọn ở lobby** và không bật tắt được trong lúc gọi. Cả hai peer phải chọn cùng một thiết lập.
- **Cuộc gọi nhóm: không có simulcast, không điều chỉnh chiều tải xuống.** Mỗi subscriber nhận đúng một stream duy nhất của từng người publish. Subscriber có mạng chậm không xin được layer thấp hơn.
- **Cuộc gọi nhóm: một server, chỉ IPv4 UDP.** Client phải truy cập thẳng được UDP 4001. Phòng chỉ nằm trong bộ nhớ của một process.
- **Cuộc gọi nhóm: chat đi qua server** dưới dạng text thuần trên WebSocket signaling, khác với data channel của cuộc gọi 1:1.
- **iOS luôn gửi VP8.** VP8 được encode bằng phần mềm, nên tốn CPU và pin hơn H264 phần cứng. Nhưng chính nhờ vậy chia sẻ màn hình mới chạy được khi app ở chế độ nền.
- **Chia sẻ màn hình trên macOS cần quyền Screen Recording**, và phải mở lại app sau khi cấp quyền.
- **Windows ghép effect trên CPU.** Model có thể chạy trên GPU hoặc NPU, nhưng phần trộn hình, làm mờ và chuyển đổi màu chạy bằng C# trên một thread, khoảng 13 ms cho mỗi frame 720p, nên máy chậm sẽ gửi effect với frame rate thấp hơn.
- **Windows chỉ phát file video** ở những định dạng mà Media Foundation trên máy đó decode được.
