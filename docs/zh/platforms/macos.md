---
title: "WebRTC macOS 应用：SwiftUI 与 ScreenCaptureKit"
description: "使用 SwiftUI 和 AppKit 编写的 macOS 原生 WebRTC 视频通话应用（不是 Catalyst），用 ScreenCaptureKit 共享屏幕，支持端到端加密、特效和悬浮通话窗口。"
---

# macOS

一个原生 SwiftUI 应用，SwiftUI 没有对应 API 的地方使用 AppKit，不是 Catalyst。它和 iOS 应用位于同一个 Xcode 工程中，并编译了 iOS 的大部分代码，所以信令、E2EE、特效和多人通话的工作方式与 [iOS](/zh/platforms/ios) 完全相同。要求 macOS 26 或更高版本。

<DemoMedia src="/media/share-macos.png" :width="720">
Mac 应用打开了屏幕共享选择器，显示可共享的屏幕和窗口的实时缩略图。
</DemoMedia>

打开 [`ios/WebRTCDemo.xcodeproj`](gh:ios/WebRTCDemo.xcodeproj)，选择 `WebRTCDemoMac` scheme 和 **My Mac** 运行目标。服务器地址在 **WebRTC Demo › Settings…**（⌘,）中设置，默认是 `localhost`，所以运行在同一台 Mac 上的服务器无需任何修改即可使用。第一次通话时会请求摄像头和麦克风权限，第一次共享屏幕时会请求屏幕录制权限。

## 与 iOS 共享的部分 {#shared-with-ios}

`CallViewModel`、`LocalMedia`、`WebRTCClient`、`GroupCallClient`、`SignalingSocket`、`FrameEncryption`、`EffectsProcessor` 以及共享的视图会同时编译进两个应用。平台相关的代码用 `#if os(macOS)` / `#if os(iOS)` 隔开。

## Mac 独有的部分 {#mac-only}

文件位于 [`ios/WebRTCDemoMac`](gh:ios/WebRTCDemoMac)：

| 文件 | 作用 |
| --- | --- |
| [`WebRTCDemoMacApp.swift`](gh:ios/WebRTCDemoMac/WebRTCDemoMacApp.swift) | 单窗口、Settings scene、Call 菜单 |
| [`MacCallView.swift`](gh:ios/WebRTCDemoMac/MacCallView.swift) | 通话窗口：主画面、可拖动的自己画面、自动隐藏的控件 |
| [`ScreenSharePicker.swift`](gh:ios/WebRTCDemoMac/ScreenSharePicker.swift) | 带缩略图的屏幕和窗口选择器 |
| [`ScreenShareCapturer.swift`](gh:ios/WebRTCDemoMac/ScreenShareCapturer.swift) | 把 `SCStream` 送入 `RTCVideoSource` |
| [`FileVideoCapturer.swift`](gh:ios/WebRTCDemoMac/FileVideoCapturer.swift) | 用 `AVAssetReader` 读取文件并送入 `RTCVideoSource`，循环播放 |
| [`FloatingCallWindow.swift`](gh:ios/WebRTCDemoMac/FloatingCallWindow.swift) | 始终置顶的迷你通话窗口 |
| [`MacGroupStage.swift`](gh:ios/WebRTCDemoMac/MacGroupStage.swift) | 多人通话的网格、小窗、成员列表 |
| [`CallCommands.swift`](gh:ios/WebRTCDemoMac/CallCommands.swift) | Call 菜单和单键快捷键 |

## 与 iOS 的不同之处 {#what-is-different-from-ios}

- **屏幕共享。** 使用 ScreenCaptureKit：`SCShareableContent` 获取列表，`SCScreenshotManager` 生成缩略图，`SCStream` 以 NV12 格式采集，长边最大 1920 px，30 fps。屏幕静止时每 500 ms 重复发送一次最后一帧，让编码器持续输出。没有使用 webrtc-sdk 的 `RTCDesktopCapturer`，因为它依赖的 API 在 macOS 15 及以后已经不再支持。
- **视频文件。** webrtc-sdk 的 macOS 版本没有提供 `RTCFileVideoCapturer`，所以 `FileVideoCapturer` 用 `AVAssetReader` 读取文件，并按显示时间戳控制节奏。
- **视频视图。** Mac 上的 `RTCMTLVideoView` 没有 content mode。它按画面的宽高比布局，尺寸刚好能盖住所在区域，fit 和镜像则通过以中心为原点的 layer transform 实现。
- **设备。** 摄像头来自 `RTCCameraVideoCapturer`，麦克风和扬声器来自 factory 的音频设备模块。选择会被记住。
- **悬浮窗口。** 一个浮动层级、出现在所有桌面空间（Spaces）中的 `NSPanel`，最小化通话窗口时会自动打开。[画中画](/zh/how-it-works/picture-in-picture#macos)
- **镜像。** 采集连接固定为不镜像，所以对方收到的画面始终是未镜像的，贴纸的方向也是正确的。只有本地视图会做镜像。
- **特效频率。** Vision 每 2 帧运行一次，某次处理较慢时会降到每 3 帧或 4 帧一次，因为 Intel Mac 没有神经网络引擎。
- **沙盒。** App Sandbox 开启了摄像头、麦克风、出站和入站网络（ICE 连通性检查会主动到达）以及用户选择的文件。Mac 上没有需要配置的音频会话。
