# Android

A Kotlin app with Jetpack Compose and Material 3 Expressive, on webrtc-sdk `150.7871.01`. Android 7.0 (API 24) or newer.

<DemoMedia src="/media/android-call.png" :width="320">
Android in a 1:1 call, portrait: the other person's video fills the screen, your tile in a corner, and the floating toolbar visible at the bottom.
</DemoMedia>

Run the `app` configuration from Android Studio, or `./gradlew :app:installDebug` in `android/`.

## Where things are {#where-things-are}

All paths are under [`android/app/src/main/java/com/example/myapplication`](gh:android/app/src/main/java/com/example/myapplication).

| File | What it does |
| --- | --- |
| [`MainActivity.kt`](gh:android/app/src/main/java/com/example/myapplication/MainActivity.kt) | Hosts the Compose lobby, starts `CallActivity` |
| [`CallActivity.kt`](gh:android/app/src/main/java/com/example/myapplication/CallActivity.kt) | Hosts the call screen: permissions, pickers, `MediaProjection`, PiP |
| [`call/`](gh:android/app/src/main/java/com/example/myapplication/call) | `BaseCallViewModel` (shared state and media controls), `CallViewModel` (1:1), `GroupCallViewModel` (group) |
| [`webrtc/LocalMedia.kt`](gh:android/app/src/main/java/com/example/myapplication/webrtc/LocalMedia.kt) | Factory, capturers, sources, tracks, effects, the 300 ms rule. Shared by both engines. |
| [`webrtc/PeerConnectionClient.kt`](gh:android/app/src/main/java/com/example/myapplication/webrtc/PeerConnectionClient.kt) | The 1:1 engine |
| [`webrtc/WebRtcPeer.kt`](gh:android/app/src/main/java/com/example/myapplication/webrtc/WebRtcPeer.kt) | One `PeerConnection`, its data channel and cryptors |
| [`webrtc/SignalingSocket.kt`](gh:android/app/src/main/java/com/example/myapplication/webrtc/SignalingSocket.kt) | JSON over an OkHttp WebSocket, callbacks on the main thread. Used by both engines. |
| [`webrtc/sfu/GroupCallClient.kt`](gh:android/app/src/main/java/com/example/myapplication/webrtc/sfu/GroupCallClient.kt) | The group engine: publish and subscribe connections |
| [`webrtc/E2eeManager.kt`](gh:android/app/src/main/java/com/example/myapplication/webrtc/E2eeManager.kt) | The `FrameCryptor` key provider |
| [`webrtc/effects/`](gh:android/app/src/main/java/com/example/myapplication/webrtc/effects) | GLES effects processor, MediaPipe segmenter and face tracker |
| [`ui/video/TextureViewRenderer.kt`](gh:android/app/src/main/java/com/example/myapplication/ui/video/TextureViewRenderer.kt) | The video view |

## How it does each part {#how-it-does-each-part}

- **Switching sources.** The camera and the screen each get a new track, put on the same sender with `RtpSender.setTrack()`. A file reuses the camera's `VideoSource` with another capturer. [Switching video sources](/how-it-works/media-sources)
- **Screen sharing.** `MediaProjection` with a foreground service, [`ScreenCaptureService.kt`](gh:android/app/src/main/java/com/example/myapplication/ScreenCaptureService.kt). It stays alive while you share another app, so it hears about rotation and resizes the virtual display.
- **E2EE.** `FrameCryptor` from webrtc-sdk with the [shared options](/how-it-works/e2ee#the-key). [End-to-end encryption](/how-it-works/e2ee)
- **Effects.** A `VideoProcessor` on the camera's `VideoSource`. Everything runs in GLES shaders on the camera texture, and only a small copy is read back for the models. [Backgrounds and effects](/how-it-works/effects#android)
- **Picture-in-picture.** Enters by itself on the home gesture on Android 12+. [Picture-in-picture](/how-it-works/picture-in-picture#android)

## Video rendering {#video-rendering}

Video is drawn by `TextureViewRenderer`, a `TextureView` fed by an `EglRenderer`. Unlike `SurfaceViewRenderer`, a `TextureView` can be clipped, rounded and animated with the rest of the Compose tree, which the floating self view needs.

The renderer always fills its view. So the view is laid out at the frame's aspect ratio, just large enough to cover the screen, and "fit" scales it down with a `graphicsLayer`. Switching between fit and fill is then a GPU-only animation that never resizes the `TextureView`. iOS, macOS and Windows use the same idea.

## Threads {#threads}

| Thread | Work |
| --- | --- |
| Main | UI, every `PeerConnection` call, socket callbacks |
| WebRTC signaling thread | `PeerConnection.Observer` callbacks, which hop to main |
| `CaptureThread` | Camera frames and the effects' GL work |
| `SegmenterInference`, `FaceTracker` | The MediaPipe models |

Callbacks are dropped once a call has ended, because calling a disposed native `PeerConnection` crashes the process.

## Notes {#notes}

- `CallViewModel` survives rotation and owns the WebRTC client and the shared `EglBase`, so the call keeps running while the activity is recreated. `CallActivity` handles size changes itself, so in practice it isn't recreated at all.
- `Camera2Session` tags every frame with the device rotation, so the other side sees an upright picture whichever way the phone is held.
- Material 3 Expressive is only in the `material3` 1.5 alphas, pinned to `1.5.0-alpha18`. See [Troubleshooting](/guide/troubleshooting#android) before bumping Compose.
- The MediaPipe models (`selfie_segmenter.tflite`, `face_landmarker.task`) are stored uncompressed in the APK's assets.
