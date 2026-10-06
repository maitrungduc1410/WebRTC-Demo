---
title: "WebRTC macOS app in SwiftUI with ScreenCaptureKit"
description: "A native macOS WebRTC video call app in SwiftUI and AppKit (not Catalyst), with ScreenCaptureKit screen sharing, E2EE, effects and a floating call window."
---

# macOS

A native SwiftUI app, with AppKit where SwiftUI has no API. It is not Catalyst. It lives in the same Xcode project as the iOS app and compiles most of its code, so signaling, E2EE, effects and group calls work exactly as on [iOS](/platforms/ios). macOS 26 or newer.

<DemoMedia src="/media/share-macos.png" :width="720">
The Mac app with the screen share picker open, showing live thumbnails of the screens and windows you can share.
</DemoMedia>

Open [`ios/WebRTCDemo.xcodeproj`](gh:ios/WebRTCDemo.xcodeproj), pick the `WebRTCDemoMac` scheme and the **My Mac** destination. The server addresses are in **WebRTC Demo › Settings…** (⌘,), and default to `localhost`, so servers on the same Mac work without changes. The first call asks for camera and microphone access, and the first screen share asks for Screen Recording access.

## Shared with iOS {#shared-with-ios}

`CallViewModel`, `LocalMedia`, `WebRTCClient`, `GroupCallClient`, `SignalingSocket`, `FrameEncryption`, `EffectsProcessor` and the shared views are compiled into both apps. Platform code is kept apart with `#if os(macOS)` / `#if os(iOS)`.

## Mac only {#mac-only}

Files in [`ios/WebRTCDemoMac`](gh:ios/WebRTCDemoMac):

| File | What it does |
| --- | --- |
| [`WebRTCDemoMacApp.swift`](gh:ios/WebRTCDemoMac/WebRTCDemoMacApp.swift) | One window, the Settings scene, the Call menu |
| [`MacCallView.swift`](gh:ios/WebRTCDemoMac/MacCallView.swift) | The call window: stage, draggable self view, auto-hiding controls |
| [`ScreenSharePicker.swift`](gh:ios/WebRTCDemoMac/ScreenSharePicker.swift) | Screen and window picker with thumbnails |
| [`ScreenShareCapturer.swift`](gh:ios/WebRTCDemoMac/ScreenShareCapturer.swift) | `SCStream` into the `RTCVideoSource` |
| [`FileVideoCapturer.swift`](gh:ios/WebRTCDemoMac/FileVideoCapturer.swift) | `AVAssetReader` into the `RTCVideoSource`, looping |
| [`FloatingCallWindow.swift`](gh:ios/WebRTCDemoMac/FloatingCallWindow.swift) | The always-on-top mini call window |
| [`MacGroupStage.swift`](gh:ios/WebRTCDemoMac/MacGroupStage.swift) | Group call grid, tiles, people list |
| [`CallCommands.swift`](gh:ios/WebRTCDemoMac/CallCommands.swift) | The Call menu and single-key shortcuts |

## What is different from iOS {#what-is-different-from-ios}

- **Screen sharing.** ScreenCaptureKit: `SCShareableContent` for the list, `SCScreenshotManager` for thumbnails, `SCStream` in NV12, at most 1920 px on the long side, 30 fps. The last frame is repeated every 500 ms while the screen is still, so the encoder keeps producing frames. webrtc-sdk's `RTCDesktopCapturer` isn't used because it relies on APIs that macOS 15 and later no longer support.
- **Video files.** The macOS slice of webrtc-sdk doesn't ship `RTCFileVideoCapturer`, so `FileVideoCapturer` reads files with `AVAssetReader`, paced by presentation time.
- **Video view.** The Mac `RTCMTLVideoView` has no content mode. It is laid out at the frame's aspect ratio, just large enough to cover its bounds, and fit and mirroring are a layer transform around the center.
- **Devices.** Cameras come from `RTCCameraVideoCapturer`, microphones and speakers from the factory's audio device module. Choices are remembered.
- **Floating window.** An `NSPanel` at floating level on all Spaces. It opens by itself when you minimize the call window. [Picture-in-picture](/how-it-works/picture-in-picture#macos)
- **Mirroring.** The capture connection is pinned to unmirrored, so the other side always gets unmirrored frames and stickers the right way round. Only the local views mirror.
- **Effects cadence.** Vision runs on every 2nd frame, and backs off to every 3rd or 4th when a pass is slow, since Intel Macs have no Neural Engine.
- **Sandbox.** App Sandbox with camera, microphone, outgoing and incoming network (ICE checks arrive unsolicited) and user-selected files. There is no audio session to configure on the Mac.
