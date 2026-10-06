---
title: "Picture-in-picture for video calls on every platform"
description: "How each app keeps a call in a floating window: Document Picture-in-Picture on the web, Android PiP mode, AVKit on iOS, floating windows on desktop."
---

# Picture-in-picture

Every app can keep the call in a small window on top of other apps. Each platform has its own API for it, and they differ a lot in what they allow.

<DemoMedia src="/media/pip-web.png" :width="720">
Chrome on a desktop with another tab or app in front, and the call's floating picture-in-picture window on top: the other person's video, your small tile and the mic, camera and hang up buttons.
</DemoMedia>

| Platform | API | What the window shows |
| --- | --- | --- |
| Web (Chrome, Edge) | Document Picture-in-Picture | A mini call: remote video, your tile, mic, camera and hang up |
| Web (other browsers) | Video picture-in-picture | The remote video only |
| Android | Activity picture-in-picture | The remote video |
| iOS | `AVPictureInPictureController`, video call PiP | The remote video (1:1 calls only) |
| macOS | An always-on-top `NSPanel` | Remote video, your tile, controls on hover |
| Windows | `CompactOverlay` window | Remote video with mute, camera, back and leave |

In a group call the window shows the active speaker, else the first person with a camera.

## Web {#web}

[`usePictureInPicture.ts`](gh:web/src/composables/usePictureInPicture.ts) uses the Document Picture-in-Picture API where it exists (Chromium desktop) and falls back to video picture-in-picture.

- `documentPictureInPicture.requestWindow()` opens an always-on-top window. The page's stylesheets are copied into it, and [`PipView.vue`](gh:web/src/components/call/PipView.vue) is rendered there through `<Teleport>`. Its animations are CSS only, because the tab that owns the Vue app is hidden and its `requestAnimationFrame` doesn't run.
- While a peer is connected, the page registers the `enterpictureinpicture` Media Session action. Chrome 134 and later then opens the window by itself when you switch to another tab, like Google Meet. Switching to another app doesn't, the button (or `P`) does.
- Other browsers put the remote `<video>` itself into picture-in-picture (`requestPictureInPicture()`, or `webkitSetPresentationMode('picture-in-picture')` on iOS Safari).

## Android {#android}

[`CallActivity`](gh:android/app/src/main/java/com/example/myapplication/CallActivity.kt) declares `supportsPictureInPicture` and handles size changes itself, so entering and leaving the window never recreates it.

- Once a peer is connected, `setAutoEnterEnabled(true)` on Android 12+ moves the call into the window on the home gesture. Older versions do the same from `onUserLeaveHint`.
- The window's aspect ratio follows the remote frame, clamped to the 1:2.39 to 2.39:1 range the system accepts.
- In the window only the remote video is shown. The local tile is hidden rather than removed, so it keeps its corner.
- Closing the window ends the call, since nothing keeps the camera and microphone alive in the background.

## iOS {#ios}

The call uses video call PiP: an `AVPictureInPictureController` with an `activeVideoCallSourceView` content source and an `AVPictureInPictureVideoCallViewController` ([`PictureInPicture.swift`](gh:ios/WebRTCDemo/PictureInPicture.swift)).

- The system window can't show the Metal video view, so a second sink on the remote track feeds an `AVSampleBufferDisplayLayer`. Hardware-decoded frames go in as they are. Software-decoded I420 frames, which is what VP8 produces, are copied into pooled NV12 pixel buffers.
- `canStartPictureInPictureAutomaticallyFromInline` is on while a peer is connected, so leaving the app opens the window.
- The camera session turns on `isMultitaskingCameraAccessEnabled` where it is supported, so the other person keeps seeing you while the call is in PiP.

## macOS {#macos}

The Mac has no system PiP for calls, so [`FloatingCallWindow.swift`](gh:ios/WebRTCDemoMac/FloatingCallWindow.swift) is an `NSPanel` at floating level that joins all Spaces. It shows the remote video with a mini self view and hover controls, and opens by itself when you minimize the call window.

## Windows {#windows}

The window switches to the `CompactOverlay` presenter: a small always-on-top window with the remote video and mute, camera, back and leave buttons. In a group call it closes by itself when the last person leaves.
