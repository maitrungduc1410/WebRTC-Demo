---
title: "Ví dụ WebRTC Android với Kotlin và Jetpack Compose"
description: "App gọi video WebRTC native cho Android viết bằng Kotlin và Jetpack Compose trên webrtc-sdk M150, có chia sẻ màn hình, chat, E2EE, effect và picture-in-picture."
---

# Android

App Kotlin dùng Jetpack Compose và Material 3 Expressive, chạy trên webrtc-sdk `150.7871.01`. Cần Android 7.0 (API 24) trở lên.

<DemoMedia src="/media/android-call.png" :width="320">
Android trong cuộc gọi 1:1, màn hình dọc: video của người kia chiếm cả màn hình, tile của bạn ở một góc, toolbar nổi hiện ở phía dưới.
</DemoMedia>

Chạy configuration `app` từ Android Studio, hoặc chạy `./gradlew :app:installDebug` trong `android/`.

## Code nằm ở đâu {#where-things-are}

Mọi đường dẫn đều nằm dưới [`android/app/src/main/java/com/example/myapplication`](gh:android/app/src/main/java/com/example/myapplication).

| File | Làm gì |
| --- | --- |
| [`MainActivity.kt`](gh:android/app/src/main/java/com/example/myapplication/MainActivity.kt) | Chứa lobby viết bằng Compose, khởi động `CallActivity` |
| [`CallActivity.kt`](gh:android/app/src/main/java/com/example/myapplication/CallActivity.kt) | Chứa màn hình cuộc gọi: quyền truy cập, các hộp chọn, `MediaProjection`, PiP |
| [`call/`](gh:android/app/src/main/java/com/example/myapplication/call) | `BaseCallViewModel` (state và điều khiển media dùng chung), `CallViewModel` (1:1), `GroupCallViewModel` (nhóm) |
| [`webrtc/LocalMedia.kt`](gh:android/app/src/main/java/com/example/myapplication/webrtc/LocalMedia.kt) | Factory, capturer, source, track, effect, quy tắc 300 ms. Dùng chung cho cả hai engine. |
| [`webrtc/PeerConnectionClient.kt`](gh:android/app/src/main/java/com/example/myapplication/webrtc/PeerConnectionClient.kt) | Engine 1:1 |
| [`webrtc/WebRtcPeer.kt`](gh:android/app/src/main/java/com/example/myapplication/webrtc/WebRtcPeer.kt) | Một `PeerConnection`, kèm data channel và các cryptor của nó |
| [`webrtc/SignalingSocket.kt`](gh:android/app/src/main/java/com/example/myapplication/webrtc/SignalingSocket.kt) | JSON qua WebSocket của OkHttp, callback chạy trên main thread. Cả hai engine đều dùng. |
| [`webrtc/sfu/GroupCallClient.kt`](gh:android/app/src/main/java/com/example/myapplication/webrtc/sfu/GroupCallClient.kt) | Engine nhóm: connection publish và subscribe |
| [`webrtc/E2eeManager.kt`](gh:android/app/src/main/java/com/example/myapplication/webrtc/E2eeManager.kt) | Key provider cho `FrameCryptor` |
| [`webrtc/effects/`](gh:android/app/src/main/java/com/example/myapplication/webrtc/effects) | Effects processor bằng GLES, segmenter và face tracker của MediaPipe |
| [`ui/video/TextureViewRenderer.kt`](gh:android/app/src/main/java/com/example/myapplication/ui/video/TextureViewRenderer.kt) | View hiển thị video |

## Cách Android làm từng phần {#how-it-does-each-part}

- **Chuyển nguồn.** Camera và màn hình mỗi thứ có một track mới, đặt lên cùng một sender bằng `RtpSender.setTrack()`. File thì dùng lại `VideoSource` của camera với một capturer khác. [Chuyển nguồn video](/vi/how-it-works/media-sources)
- **Chia sẻ màn hình.** `MediaProjection` kèm một foreground service, [`ScreenCaptureService.kt`](gh:android/app/src/main/java/com/example/myapplication/ScreenCaptureService.kt). Service vẫn sống khi bạn chia sẻ một app khác, nên nó biết khi máy xoay và đổi kích thước virtual display theo.
- **E2EE.** `FrameCryptor` của webrtc-sdk với [các tùy chọn dùng chung](/vi/how-it-works/e2ee#the-key). [Mã hóa đầu cuối](/vi/how-it-works/e2ee)
- **Effect.** Một `VideoProcessor` gắn trên `VideoSource` của camera. Mọi thứ chạy bằng shader GLES trên texture của camera, chỉ có một bản copy nhỏ được đọc ngược ra cho model. [Phông nền và effect](/vi/how-it-works/effects#android)
- **Picture-in-picture.** Trên Android 12+, app tự vào PiP khi bạn vuốt về màn hình chính. [Picture-in-picture](/vi/how-it-works/picture-in-picture#android)

## Render video {#video-rendering}

Video được vẽ bằng `TextureViewRenderer`, một `TextureView` được `EglRenderer` cấp frame. Khác với `SurfaceViewRenderer`, `TextureView` có thể bị clip, bo góc và animate cùng với phần còn lại của cây Compose, là thứ mà view tự xem hình nổi của bạn cần.

Renderer luôn fill kín view của nó. Vì vậy view được layout theo đúng tỉ lệ của frame, vừa đủ lớn để phủ kín màn hình, còn "fit" thì thu nhỏ nó lại bằng `graphicsLayer`. Nhờ vậy chuyển giữa fit và fill chỉ là một animation chạy trên GPU, không bao giờ phải đổi kích thước `TextureView`. iOS, macOS và Windows cũng dùng cách này.

## Thread {#threads}

| Thread | Việc |
| --- | --- |
| Main | UI, mọi lời gọi vào `PeerConnection`, callback của socket |
| Signaling thread của WebRTC | Callback của `PeerConnection.Observer`, được chuyển sang main |
| `CaptureThread` | Frame camera và phần việc GL của effect |
| `SegmenterInference`, `FaceTracker` | Các model MediaPipe |

Khi cuộc gọi đã kết thúc thì callback bị bỏ qua, vì gọi vào một `PeerConnection` native đã bị dispose sẽ làm crash cả process.

## Ghi chú {#notes}

- `CallViewModel` sống sót qua lần xoay màn hình và nắm WebRTC client cùng `EglBase` dùng chung, nên cuộc gọi vẫn chạy trong lúc activity được tạo lại. Thực ra `CallActivity` tự xử lý việc đổi kích thước, nên trên thực tế nó không bị tạo lại.
- `Camera2Session` gắn hướng xoay của thiết bị vào từng frame, nên cầm điện thoại kiểu gì thì bên kia cũng thấy hình thẳng.
- Material 3 Expressive chỉ có trong các bản alpha `material3` 1.5, đang ghim ở `1.5.0-alpha18`. Đọc [Xử lý sự cố](/vi/guide/troubleshooting#android) trước khi nâng Compose.
- Các model MediaPipe (`selfie_segmenter.tflite`, `face_landmarker.task`) được lưu không nén trong assets của APK.
