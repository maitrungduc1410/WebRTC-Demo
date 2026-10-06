# Android

一个使用 Jetpack Compose 和 Material 3 Expressive 的 Kotlin 应用，基于 webrtc-sdk `150.7871.01`，支持 Android 7.0（API 24）及以上版本。

<DemoMedia src="/media/android-call.png" :width="320">
Android 竖屏进行 1:1 通话：对方的视频铺满屏幕，你的小窗在一角，底部显示悬浮工具栏。
</DemoMedia>

在 Android Studio 中运行 `app` 配置，或在 `android/` 目录下执行 `./gradlew :app:installDebug`。

## 代码结构 {#where-things-are}

以下路径都位于 [`android/app/src/main/java/com/example/myapplication`](gh:android/app/src/main/java/com/example/myapplication) 下。

| 文件 | 作用 |
| --- | --- |
| [`MainActivity.kt`](gh:android/app/src/main/java/com/example/myapplication/MainActivity.kt) | 承载 Compose 大厅，启动 `CallActivity` |
| [`CallActivity.kt`](gh:android/app/src/main/java/com/example/myapplication/CallActivity.kt) | 承载通话界面：权限、选择器、`MediaProjection`、画中画 |
| [`call/`](gh:android/app/src/main/java/com/example/myapplication/call) | `BaseCallViewModel`（共享状态和媒体控制）、`CallViewModel`（1:1）、`GroupCallViewModel`（多人） |
| [`webrtc/LocalMedia.kt`](gh:android/app/src/main/java/com/example/myapplication/webrtc/LocalMedia.kt) | Factory、采集器、source、track、特效、300 ms 规则。两个引擎共用。 |
| [`webrtc/PeerConnectionClient.kt`](gh:android/app/src/main/java/com/example/myapplication/webrtc/PeerConnectionClient.kt) | 1:1 通话引擎 |
| [`webrtc/WebRtcPeer.kt`](gh:android/app/src/main/java/com/example/myapplication/webrtc/WebRtcPeer.kt) | 一个 `PeerConnection` 及其 data channel 和 cryptor |
| [`webrtc/SignalingSocket.kt`](gh:android/app/src/main/java/com/example/myapplication/webrtc/SignalingSocket.kt) | 基于 OkHttp WebSocket 收发 JSON，回调在主线程执行。两个引擎共用。 |
| [`webrtc/sfu/GroupCallClient.kt`](gh:android/app/src/main/java/com/example/myapplication/webrtc/sfu/GroupCallClient.kt) | 多人通话引擎：publish 和 subscribe 连接 |
| [`webrtc/E2eeManager.kt`](gh:android/app/src/main/java/com/example/myapplication/webrtc/E2eeManager.kt) | `FrameCryptor` 的 key provider |
| [`webrtc/effects/`](gh:android/app/src/main/java/com/example/myapplication/webrtc/effects) | GLES 特效处理器、MediaPipe 人像分割和人脸追踪 |
| [`ui/video/TextureViewRenderer.kt`](gh:android/app/src/main/java/com/example/myapplication/ui/video/TextureViewRenderer.kt) | 视频视图 |

## 各部分的实现 {#how-it-does-each-part}

- **切换视频源。** 摄像头和屏幕各自创建新的 track，通过 `RtpSender.setTrack()` 放到同一个 sender 上。文件复用摄像头的 `VideoSource`，只是换一个采集器。[切换视频源](/zh/how-it-works/media-sources)
- **屏幕共享。** `MediaProjection` 加前台服务，见 [`ScreenCaptureService.kt`](gh:android/app/src/main/java/com/example/myapplication/ScreenCaptureService.kt)。共享其他应用期间，这个服务会一直存活，因此能感知屏幕旋转并调整虚拟显示器的尺寸。
- **E2EE。** webrtc-sdk 的 `FrameCryptor`，使用[统一的选项](/zh/how-it-works/e2ee#the-key)。[端到端加密](/zh/how-it-works/e2ee)
- **特效。** 在摄像头的 `VideoSource` 上设置一个 `VideoProcessor`。所有处理都在 GLES 着色器中基于摄像头纹理完成，只有一份小尺寸副本会被读回给模型。[背景与特效](/zh/how-it-works/effects#android)
- **画中画。** 在 Android 12+ 上，做回到桌面的手势时会自动进入。[画中画](/zh/how-it-works/picture-in-picture#android)

## 视频渲染 {#video-rendering}

视频由 `TextureViewRenderer` 绘制，它是一个由 `EglRenderer` 供帧的 `TextureView`。和 `SurfaceViewRenderer` 不同，`TextureView` 可以和 Compose 树中的其他元素一起裁剪、加圆角和做动画，可拖动的自己画面正需要这一点。

渲染器总是铺满自己的视图。所以视图按画面的宽高比布局，尺寸刚好能盖住整个屏幕，"fit" 则通过 `graphicsLayer` 把它缩小。这样 fit 和 fill 之间的切换就是一个纯 GPU 动画，永远不需要调整 `TextureView` 的尺寸。iOS、macOS 和 Windows 用的也是同样的思路。

## 线程 {#threads}

| 线程 | 工作 |
| --- | --- |
| 主线程 | UI、所有 `PeerConnection` 调用、socket 回调 |
| WebRTC 信令线程 | `PeerConnection.Observer` 回调，会切换到主线程处理 |
| `CaptureThread` | 摄像头帧和特效的 GL 处理 |
| `SegmenterInference`、`FaceTracker` | MediaPipe 模型 |

通话结束后的回调会被丢弃，因为调用已释放的原生 `PeerConnection` 会导致进程崩溃。

## 备注 {#notes}

- `CallViewModel` 在屏幕旋转后依然存活，持有 WebRTC 客户端和共享的 `EglBase`，所以 Activity 重建期间通话不会中断。`CallActivity` 自行处理尺寸变化，所以实际上根本不会被重建。
- `Camera2Session` 会给每一帧标记设备的旋转角度，所以无论手机怎么拿，对方看到的画面都是正的。
- Material 3 Expressive 只存在于 `material3` 1.5 的 alpha 版本中，这里固定为 `1.5.0-alpha18`。升级 Compose 之前请先看[常见问题](/zh/guide/troubleshooting#android)。
- MediaPipe 模型（`selfie_segmenter.tflite`、`face_landmarker.task`）以不压缩的形式存放在 APK 的 assets 中。
