---
title: "各平台视频通话的画中画实现"
description: "各应用如何把视频通话放进悬浮窗口：Web 端的 Document Picture-in-Picture、Android 画中画模式、iOS 的 AVKit，以及 macOS 和 Windows 的悬浮窗口。"
---

# 画中画

每个应用都能把通话放进一个浮在其他应用之上的小窗口。各平台有各自的 API，能做的事情差别很大。

<DemoMedia src="/media/pip-web.png" :width="720">
桌面版 Chrome，前台是另一个标签页或应用，通话的画中画悬浮窗口位于最上层：显示对方的视频、你自己的小窗，以及麦克风、摄像头和挂断按钮。
</DemoMedia>

| 平台 | API | 窗口中显示的内容 |
| --- | --- | --- |
| Web（Chrome、Edge） | Document Picture-in-Picture | 一个迷你通话界面：远端视频、你的小窗、麦克风、摄像头和挂断 |
| Web（其他浏览器） | 视频画中画 | 只有远端视频 |
| Android | Activity 画中画 | 远端视频 |
| iOS | `AVPictureInPictureController`，视频通话画中画 | 远端视频（仅 1:1 通话） |
| macOS | 始终置顶的 `NSPanel` | 远端视频、你的小窗，悬停时显示控件 |
| Windows | `CompactOverlay` 窗口 | 远端视频，以及静音、摄像头、返回和离开按钮 |

在多人通话中，窗口显示当前说话的人；没人说话时，显示第一个开着摄像头的人。

## Web {#web}

[`usePictureInPicture.ts`](gh:web/src/composables/usePictureInPicture.ts) 在支持的环境（桌面版 Chromium）中使用 Document Picture-in-Picture API，否则回退到视频画中画。

- `documentPictureInPicture.requestWindow()` 会打开一个始终置顶的窗口。页面的样式表会被复制进去，[`PipView.vue`](gh:web/src/components/call/PipView.vue) 通过 `<Teleport>` 渲染到这个窗口中。它的动画全部用 CSS 实现，因为承载 Vue 应用的标签页此时处于隐藏状态，`requestAnimationFrame` 不会执行。
- 两端连接后，页面会注册 Media Session 的 `enterpictureinpicture` action。这样在 Chrome 134 及以上版本中，切换到其他标签页时窗口会自动打开，和 Google Meet 一样。切换到其他应用时不会自动打开，需要点击按钮（或按 `P`）。
- 其他浏览器会把远端的 `<video>` 元素本身放进画中画（`requestPictureInPicture()`，iOS Safari 上是 `webkitSetPresentationMode('picture-in-picture')`）。

## Android {#android}

[`CallActivity`](gh:android/app/src/main/java/com/example/myapplication/CallActivity.kt) 声明了 `supportsPictureInPicture`，并自行处理尺寸变化，所以进入和退出画中画窗口都不会重建 Activity。

- 两端连接后，在 Android 12+ 上调用 `setAutoEnterEnabled(true)`，用户做回到桌面的手势时，通话会自动进入画中画窗口。更早的版本在 `onUserLeaveHint` 中实现同样的效果。
- 窗口的宽高比跟随远端画面，并限制在系统允许的 1:2.39 到 2.39:1 范围内。
- 窗口中只显示远端视频。本地小窗是隐藏而不是移除，所以退出后它还在原来的角落。
- 关闭窗口会结束通话，因为没有任何机制让摄像头和麦克风在后台保持运行。

## iOS {#ios}

通话使用的是视频通话画中画：一个以 `activeVideoCallSourceView` 为内容源的 `AVPictureInPictureController`，加上一个 `AVPictureInPictureVideoCallViewController`（[`PictureInPicture.swift`](gh:ios/WebRTCDemo/PictureInPicture.swift)）。

- 系统窗口无法显示 Metal 视频视图，所以远端 track 上挂了第二个 sink，为 `AVSampleBufferDisplayLayer` 供帧。硬件解码的帧直接送入；软件解码的 I420 帧（VP8 解码出来的就是这种）会被拷贝到池化的 NV12 像素缓冲区中。
- 两端连接期间会开启 `canStartPictureInPictureAutomaticallyFromInline`，所以离开应用时会自动打开画中画窗口。
- 在支持的设备上，摄像头会话会开启 `isMultitaskingCameraAccessEnabled`，这样通话处于画中画时，对方依然能看到你。

## macOS {#macos}

Mac 没有面向通话的系统画中画，所以 [`FloatingCallWindow.swift`](gh:ios/WebRTCDemoMac/FloatingCallWindow.swift) 实现了一个浮动层级、出现在所有桌面空间（Spaces）中的 `NSPanel`。它显示远端视频和一个迷你的自己画面，悬停时显示控件，最小化通话窗口时会自动打开。

## Windows {#windows}

窗口会切换到 `CompactOverlay` presenter：一个始终置顶的小窗口，显示远端视频以及静音、摄像头、返回和离开按钮。在多人通话中，最后一个人离开时它会自动关闭。
