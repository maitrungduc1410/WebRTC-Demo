# WebRTC-Demo

<div align="center">
<h3>A comprehensive 1:1 WebRTC demo on Web, Android, iOS and macOS</h3>
<p>Video calls, chat, screen and file sharing, backgrounds and face filters, and end-to-end encryption, with native UIs built in Jetpack Compose (Material 3 Expressive) and SwiftUI (Liquid Glass).</p>
</div>

# Screenshots

<!--
  Screenshot placeholders: drop the images into images/ with the file names below,
  then replace each placeholder line with the <img> tag next to it.
-->

### Android · Jetpack Compose + Material 3 Expressive

| Lobby | In call | Peer camera off | Call options |
|:-----:|:-------:|:---------------:|:------------:|
| _Screenshot: `images/android-lobby.png`_ <!-- <img src="images/android-lobby.png" width="200" /> --> | _Screenshot: `images/android-call.png`_ <!-- <img src="images/android-call.png" width="200" /> --> | _Screenshot: `images/android-camera-off.png`_ <!-- <img src="images/android-camera-off.png" width="200" /> --> | _Screenshot: `images/android-options.png`_ <!-- <img src="images/android-options.png" width="200" /> --> |

### iOS · SwiftUI + Liquid Glass

| Lobby | In call | Peer camera off | Call options |
|:-----:|:-------:|:---------------:|:------------:|
| _Screenshot: `images/ios-lobby.png`_ <!-- <img src="images/ios-lobby.png" width="200" /> --> | _Screenshot: `images/ios-call.png`_ <!-- <img src="images/ios-call.png" width="200" /> --> | _Screenshot: `images/ios-camera-off.png`_ <!-- <img src="images/ios-camera-off.png" width="200" /> --> | _Screenshot: `images/ios-options.png`_ <!-- <img src="images/ios-options.png" width="200" /> --> |

### Web · Vue 3

<img src="images/desktop.png" width="600" />

_Screenshot: `images/web-camera-off.png`, the web client showing a peer with the camera off_ <!-- <img src="images/web-camera-off.png" width="600" /> -->

_Screenshot: `images/web-mobile.png`, the web client in a phone browser_ <!-- <img src="images/web-mobile.png" width="200" /> -->

_Screenshot: `images/web-pip.png`, the call in a floating picture-in-picture window over another tab_ <!-- <img src="images/web-pip.png" width="600" /> -->

### Demo

_Recording: `images/demo-ui.gif`, joining a room, dragging the picture-in-picture, turning the camera off and chatting_ <!-- <img src="images/demo-ui.gif" width="300" /> -->

# Features

| Feature                                                    | Web | iOS | Android | macOS |
|------------------------------------------------------------|-----|-----|---------|-------|
| 1:1 video call, peer to peer                               | ✅   | ✅   | ✅       | ✅     |
| Group call through your own SFU (optional)                 | ✅   | ✅   | ✅       | ✅     |
| Chat over a data channel                                   | ✅   | ✅   | ✅       | ✅     |
| Share your screen or a video file                          | ✅   | ✅   | ✅       | ✅     |
| Virtual backgrounds: blur, pictures and videos             | ✅   | ✅   | ✅       | ✅     |
| Face-tracked stickers                                      | ✅   | ✅   | ✅       | ✅     |
| End-to-end encryption, 1:1 and group                       | ✅   | ✅   | ✅       | ✅     |
| Your own microphone level on your video (three bars)       | ✅   | ✅   | ✅       | ✅     |
| Picture-in-picture (the call in a floating window)         | ✅   | ✅¹  | ✅       | ✅²    |

¹ On iOS, picture-in-picture is available in 1:1 calls only.
² An always-on-top floating window on the Mac.

Native clients use [webrtc-sdk](https://github.com/webrtc-sdk) `150.7871.01` (Android `io.github.webrtc-sdk:android`, iOS and macOS the [`webrtc-sdk/Specs`](https://github.com/webrtc-sdk/Specs) binary through the local Swift package `ios/Packages/WebRTC`).

See [ARCHITECTURE.md](ARCHITECTURE.md) for how the signaling server and the clients work, with diagrams.

## Tech stack

| Part             | Stack |
|------------------|-------|
| Signaling server | Node.js, WebSocket (`ws`) |
| SFU server (optional) | Go, [Pion](https://github.com/pion/webrtc), WebSocket |
| Web              | Vue 3, Vite, Tailwind CSS, shadcn-vue, Lucide, Motion, MediaPipe |
| Android          | Kotlin, Jetpack Compose, Material 3 Expressive, MediaPipe |
| iOS              | Swift, SwiftUI, Liquid Glass, Vision, Core Image |
| macOS            | Swift, SwiftUI + AppKit, Liquid Glass, ScreenCaptureKit, Vision, Core Image (same Xcode project as iOS) |

# Disclaimer
This is intended to show common use cases of WebRTC cross platforms and to give you some ideas, it may have bugs, use with caution!

# Setup

## Requirements

- Node.js 20.19+ for the signaling server and the web client (Vite 7 needs it)
- Android: Android Studio with JDK 17+, a device on Android 7.0 (API 24) or newer
- iOS / macOS: Xcode 26. iOS needs a device on **iOS 26** or newer, the Mac app needs **macOS 26** or newer (Liquid Glass needs the 26 releases). Dependencies come from Swift Package Manager; CocoaPods is no longer used

## Start signaling server
First you need to start the signaling server, Open terminal at `signaling-server` and run:
```
npm install # or yarn install (to install dependencies)
npm run dev # or yarn dev
```
Once started the address of the signaling server will be printed in your terminal. Something like `192.168.1.1:4000`

The server is a plain WebSocket relay on `ws://<address>/ws` (JSON messages, see [ARCHITECTURE.md](ARCHITECTURE.md#3-signaling-server)). Set `PORT` to use another port.

> **No reconnect.** Clients keep the signaling socket open for the whole call. If it drops (server stopped, network switch, phone offline for a while), the call ends with "Lost the connection to the signaling server", even if audio and video were still flowing. Just join the room again. Group calls behave the same way with the SFU server.

## Start clients
The usage of all clients are same, you just need to join clients in same room by input same roomID.

### Web
To start web client, open terminal at `web` and run:
```
npm install

npm run dev
```
Then open 2 browsers at `localhost:5173` to test

> The web client connects to port 4000 on the host that serves the page. If the signaling server runs elsewhere, click the server address under **Join room** and enter the address printed by the server. It is saved in the browser.

### Android

Run the `app` configuration from Android Studio. In the lobby, tap the signaling server address at the bottom and enter the address printed when you start the signaling server. The app remembers it. The default is `serverAddress` in `android/app/src/main/res/values/strings.xml`.

### iOS

Open `ios/WebRTCDemo.xcodeproj` (there is no workspace and no `pod install` any more). Xcode resolves the Swift package (WebRTC `150.7871.01`, `ios/Packages/WebRTC`) on first open. Pick the `WebRTCDemo` scheme and run on a device (the camera and screen sharing need real hardware). In the lobby, tap the signaling server address at the bottom and enter the address printed when you start the signaling server. The app remembers it. The default is `SignalingServer.defaultURL` in `ios/WebRTCDemo/SignalingServer.swift`.

### macOS

Open `ios/WebRTCDemo.xcodeproj`, pick the `WebRTCDemoMac` scheme and the `My Mac` destination, and run. It is a native SwiftUI/AppKit app (not Catalyst) that shares the call logic with the iOS app. The signaling and SFU server addresses are in **WebRTC Demo › Settings…** (⌘,); the defaults are `http://localhost:4000` and `http://localhost:4001`, so servers started on the same Mac work without changes. The first call asks for camera and microphone access; the first screen share asks for Screen Recording access in System Settings.

## Group calls (optional)

Everything above is enough for 1:1 calls. To call with more people, also start the SFU server. It needs [Go](https://go.dev/dl/) 1.24 or newer (an older Go downloads 1.24 by itself):

```
cd sfu-server
go run .
```

It prints addresses like `ws://192.168.1.1:4001/ws`. It handles both signaling and media for group calls, so the Node signaling server is not needed for them. Clients must reach **TCP and UDP port 4001** on that machine. On a cloud VM behind NAT, start it with `go run . -public-ip <public IP>`. Options, firewall and cloud setup, and troubleshooting are in [`sfu-server/README.md`](sfu-server/README.md).

A room holds 8 people by default; change it with `-max-participants` (or `MAX_PARTICIPANTS`), e.g. `go run . -max-participants 12`. It is not a hard limit of the design, but every client receives and decodes everyone else's video at full quality (there is no simulcast), so phones and bandwidth give out as rooms grow. See [More than 2 peers in a room](#more-than-2-peers-in-a-room).

In the lobby of any client, switch to **Group call (SFU)**, set the SFU server address once (it is saved, like the signaling address), and join the same room id from every device. E2EE works in group calls too; everyone in the room must use the same E2EE setting.

# Using the call screen

The Android and iOS call screens work the same way:

- While you are alone, your camera fills the screen and a card shows the room ID with a copy button. When the other peer joins, your video shrinks into a picture-in-picture.
- Tap the video to show or hide the controls. They hide by themselves after a few seconds.
- Double-tap the remote video to switch between fit (whole frame, letterboxed) and fill (cropped to the screen). Screen shares start in fit mode, and so does camera video held the other way round from your screen (a portrait peer on a landscape screen, for example). Rotating starts over from that default.
- Both screens can be used in landscape. The lobby puts the form next to the title, and the call controls stay clear of the camera cutout and the navigation bar.
- Drag the picture-in-picture to any corner. To switch between the front and back cameras, double-tap it or use the button at the top right; the preview flips over to the new camera.
- The toolbar has the microphone, camera, share, chat and more buttons, and hang up. "More" holds the speaker, effects, peer audio, peer video, fit/fill and camera switch options.
- Muting the peer's audio or hiding their video only affects your device. The peer is not told.
- When the peer turns their camera off, or you hide their video, you see a blurred copy of their last frame with their avatar. The ring around the avatar pulses while they speak.

The web call screen follows the same layout in desktop and phone browsers, with a few differences:

- Move the mouse to show the controls. Toolbar buttons have tooltips with keyboard shortcuts: `M` microphone, `V` camera, `C` chat, `B` backgrounds and effects, `F` fit/fill, `P` picture-in-picture.
- On screens 1024 px and wider, chat opens as a side panel; on smaller screens it opens as a bottom sheet. On phones, "More" opens a sheet with the remaining options.
- The picture-in-picture button opens the call in a floating window that stays on top of other tabs and apps. In Chrome and Edge it is a full mini call window (remote video, your video, mic, camera and hang-up buttons), and since Chrome 134 it opens by itself when you switch to another tab during a call. Other browsers float the remote video only.

The macOS call window follows the web desktop layout:

- The video fills the window under the title bar. Move the pointer to show the controls; they hide after about four seconds without movement.
- Toolbar buttons have tooltips with the same keyboard shortcuts as the web client: `M` microphone, `V` camera, `C` chat, `B` backgrounds and effects, `F` fit/fill, `P` floating window. In a group call, as on the web, `F` is off (double-click a tile instead), the controls stay visible, and the floating window shows whoever is speaking. On the Mac they are also in the **Call** menu (plus ⇧⌘S share, ⌘O share a video file, ⇧⌘E leave) and are off while you type in the chat.
- Microphone, speaker and camera are chosen from the menus next to the microphone and camera buttons. Share opens a picker with live thumbnails of your screens and windows.
- In wide windows chat opens as a side panel. Drag your self view to any corner; double-click the remote video to switch between fit and fill.
- The floating window keeps the call on top of other apps with mute, camera, return and hang-up controls (an always-on-top panel). It also opens when you minimize the call window.

## Group call screen

In a group call every client shows the other participants in a grid, with the same controls as a 1:1 call:

- Each tile is labelled with the participant's platform and a short id (for example `Android · 3f2a1c`), shows a mic-off icon when their microphone is off, and the avatar placeholder when their camera is off.
- A green ring marks who is speaking, from the received audio level.
- Tap the people count next to the timer to see everyone in the room, with your own label first and highlighted, so you can find your tile on the other devices. On web it opens a dialog, and your label is also shown next to "You" on your own tile, as on macOS.
- People who share their screen are shown fit (whole frame); everyone else fills the tile. Double-tap a tile to switch (double-click on web and macOS).
- Your own video stays the draggable picture-in-picture tile. While you are alone, the room id card is shown.
- Chat goes through the SFU server and shows who sent each message. In More, muting peer audio or hiding peer video applies to everyone.
- On web and iOS, group tiles show the avatar without the blurred last frame (Android keeps it). On iOS, the system picture-in-picture window is only available in 1:1 calls.

# Troubleshooting

## iOS / macOS - Swift packages fail to resolve

In Xcode use **File › Packages › Reset Package Caches**, then **Resolve Package Versions**.

WebRTC comes from the local package `ios/Packages/WebRTC`, not straight from [`webrtc-sdk/Specs`](https://github.com/webrtc-sdk/Specs). The Specs manifest for `150.7871.01` declares `swift-tools-version:5.9` but uses `.visionOS(.v26)`, which only exists from PackageDescription 6.2, so Xcode fails with "'v26' is unavailable" and "Missing package product 'WebRTC'". The local package downloads the same `WebRTC.xcframework.zip` with the same checksum. To upgrade WebRTC, change the URL and checksum in `ios/Packages/WebRTC/Package.swift` (the checksum is the `sha256` of the zip), or switch back to the Specs package once a release has a valid manifest.

## iOS - No audio in calls, video works

Symptoms: in 1:1 and group calls the others never hear the iPhone and the iPhone plays nothing (no green speaking border on it either), while video works both ways. The SFU log shows `publishes video` but no `publishes audio` for the iPhone, and the Xcode console shows:

```
Failed to set category and mode: The operation couldn’t be completed. (OSStatus error -50.)
Failed to configure audio session.
InitRecording: InitPlayOrRecord failed for InitRecording!
```

The cause is the WebRTC Swift package (`webrtc-sdk/Specs`), which is the [webrtc-sdk](https://github.com/webrtc-sdk/webrtc) fork, not upstream WebRTC. When the audio device starts, WebRTC applies `RTCAudioSessionConfiguration.webRTC()` to the audio session:

- Upstream WebRTC defaults it to category `playAndRecord`, mode `voiceChat`.
- The fork copies the session's *current* category and mode instead (at least since M125). At launch that is `soloAmbient` / `default`, a playback-only category, so the microphone never opens. Since M150 the fork also adds the `allowBluetoothHFP` option, which is only valid with recording categories, so iOS rejects the whole configuration with `-50`, the session stays `soloAmbient`, and WebRTC tears the audio unit down: no recording and no playout.

The fork expects the app to configure the session itself, so `CallViewModel.configureCallAudio()` sets the WebRTC configuration to `playAndRecord` + `voiceChat` + `allowBluetoothHFP` before every call. Keep it when upgrading the package. The Mac app has no audio session, so this applies to iOS only. If the configuration is ever rejected again, the call shows the toast "Call audio didn't start: iOS refused the audio settings".

## Android - Compose or Material 3 dependency needs a newer compileSdk or AGP

The Android app uses Material 3 Expressive, which is only published in the `material3` 1.5 alphas. It is pinned to `1.5.0-alpha18` with the Compose BOM `2026.06.01`, the newest versions that build with AGP 8.13 and compileSdk 36. Newer versions require AGP 9.1 and compileSdk 37, so upgrade those first if you bump the Compose dependencies.

# Discussion

## Camera and microphone state

When a peer turns its camera or microphone off, it sends a `media state` event (`{ audio, video, screen }`) through the signaling server, which relays it to the other peer. Each client sends it again when the peers connect, so a late joiner gets the current state.

- Turning the camera off sends the state first and disables the track 300 ms later. Turning it on enables the track first and sends the state 300 ms later. Either way the peer switches to the placeholder (or back to video) without showing a black or frozen frame.
- The blurred placeholder comes from a tiny copy (36 px wide) of the last real frame, taken every 500 ms. Near-black frames, like the ones a disabled track produces, are skipped.
- The pulsing ring follows the remote `audioLevel` from `getStats()` (`inbound-rtp`, audio), polled only while the placeholder is visible.

## End to end encryption on WebRTC

Web, Android and iOS can talk to each other with E2EE on. Android/iOS use the `FrameCryptor` built into webrtc-sdk, and the web client (`web/src/e2ee.ts`, Insertable Streams in a worker) produces exactly the same frame format:

```
[unencrypted header][AES-128-GCM ciphertext + 16B tag][IV 12B][IV length = 12][key index]
```

- The unencrypted header is the VP8 payload header (10 bytes for key frames, 3 for delta frames), the Opus TOC byte (1 byte), or H264 data up to the first slice NAL header + 1 byte. It is authenticated as AES-GCM additional data. For H264 the rest of the frame is RBSP-escaped.
- The AES key is derived with `PBKDF2-HMAC-SHA256(material, "LKFrameEncryptionKey", 100000)`, 128 bits. Key provider options are the same on every platform: shared key, key index 0, no ratchet, no magic bytes.
- The peer already in the room generates 32 random bytes of key material and sends them with the `encryption key` message (base64) before the offer. When E2EE is on, every platform prefers VP8.
- Both peers must enable E2EE. The key goes through the signaling server in plain form, which is fine for a demo; a real app should use a key agreement (e.g. ECDH) or a passphrase shared out of band.

## Screen sharing on iOS

Learn from [Flutter WebRTC Demo](https://github.com/flutter-webrtc/flutter-webrtc/wiki/iOS-Screen-Sharing), we share screen on iOS using a Broadcast Extension. It works both inside the app and after going back to the home screen or another app.

- H264 is encoded by the VideoToolbox hardware encoder, which iOS invalidates while the app is in the background, so the remote side would see a frozen picture. iOS therefore always negotiates **VP8** (software encoded), with or without E2EE. The cost is more CPU and battery than hardware H264.
- The `audio` and `voip` background modes keep the app (and the socket that receives frames from the extension) alive while it is in the background.
- To stop sharing, tap the share button again in the app or stop the broadcast from Control Center.
- The app and the extension must share the same App Group (`group.com.ducmai.webrtc.broadcast` in both `.entitlements` files, `RTCAppGroupIdentifier` in `Info.plist` and `SampleHandler.swift`). If you change the team or bundle id, update all of them, and `RTCScreenSharingExtension` / `preferredExtension` with the new extension bundle id.
- The app side of the socket (`FlutterSocketConnection*`) is copied from the [flutter-webrtc plugin](https://github.com/flutter-webrtc/flutter-webrtc/tree/main/ios/flutter_webrtc/Sources/flutter_webrtc/Broadcast); the LiveKit example only contains the extension side. Keep its frame reading logic as is: if the reader ever asks the stream for 0 bytes, iOS reports end of stream and the broadcast stops right after the first frame.

## More than 2 peers in a room

The default call is 1:1 and peer to peer. Group calls go through `sfu-server`, a small Selective Forwarding Unit written for this demo with [Pion](https://github.com/pion/webrtc), instead of a ready-made media server. That keeps every client on standard WebRTC APIs, with no SFU SDK, so you can read how a group call works end to end:

- **Why not mesh?** With N people, mesh makes every phone encode and upload N − 1 streams. Through an SFU each client uploads one stream and the server copies its packets to the others.
- **Two PeerConnections per client.** The client always offers on the *publish* connection, which is never renegotiated. The server always offers on the *subscribe* connection, and renegotiates it when people join or leave. Nobody switches between offering and answering, and the outgoing camera is never touched by a renegotiation.
- **Whose track is this?** The server sets each forwarded track's stream id to the publisher's participant id, so `ontrack` tells the client which tile it belongs to.
- **E2EE still works.** Frames are encrypted before packetization, so the server forwards ciphertext. The room creator's key material is the room key.
- **What a production SFU adds.** Simulcast and per-subscriber layer selection, TURN, several servers, authentication, key rotation. If you need those, look at [LiveKit](https://github.com/livekit/livekit), [mediasoup](https://mediasoup.org/) or [Janus](https://janus.conf.meetecho.com/).

Details, diagrams and the signaling protocol are in [ARCHITECTURE.md, section 12](ARCHITECTURE.md#12-group-call-sfu-optional).

## Backgrounds and effects

"Backgrounds and effects" (under More, or `B` on the web) shows a live preview and two tabs: **Backgrounds** (none, slight blur, blur, pictures and looping videos) and **Filters** (stickers that follow your face, like headphones, a crown or glasses). A background and a sticker can be combined. The choice is remembered, and when you join with an effect on, nothing is sent until it is ready, so the peer never sees your real background first.

Effects only apply to the camera, not to screen share or file share.

| | Web | Android | iOS |
|---|---|---|---|
| Person mask | MediaPipe `ImageSegmenter` (`selfie_segmenter`) | MediaPipe `tasks-vision` (`selfie_segmenter`) | Vision `VNGeneratePersonSegmentationRequest` |
| Face points | MediaPipe `FaceLandmarker` | MediaPipe `FaceLandmarker` (`face_landmarker.task`) | Vision `VNDetectFaceLandmarksRequest` |
| Compositing | canvas 2D | GLES shaders on the camera texture | Core Image on a Metal `CIContext` |
| Video backgrounds | hidden `<video>` | `MediaPlayer` into an OES texture | `AVPlayer` + `AVPlayerItemVideoOutput` |

The macOS app compiles the iOS effects code (Vision + Core Image), so it offers the same backgrounds and stickers.

On the Mac the picker is a side panel in place of the chat (420 pt wide, like the web sheet), opened from its toolbar button, the menu (Call ▸ Backgrounds and Effects…) or `B`, and closed with Esc, its close button or the toolbar button. As on the web, `B` only opens it, `C` shows or hides the chat, and `F` and `P` work once the other person has joined. Item names show as tooltips, and the panel closes and is unavailable while you present.

### Adding backgrounds

All the apps bundle the [`effects`](effects) folder at the repository root, so a background added there shows up everywhere.

1. Put the original pictures (`.jpg` `.jpeg` `.png` `.webp`) and videos (`.mp4` `.mov` `.webm` `.mkv`) in `effects-source/` at the repository root (ignored by git). Name them in kebab-case after what they show, e.g. `cozy-living-room.jpg`, `beach-sunset.mp4`. The name becomes the id, and the title shown in the app ("Cozy living room").
2. Run `python3 tools/prepare_effects.py` (needs `ffmpeg` and `ffprobe`). Pictures are resized to 1920 px on the long side; videos to 1280 px, at most 15 s, 30 fps, H.264 without audio. Each gets a 320×180 thumbnail, and `effects/backgrounds.json` is rewritten. Use `--force` to encode everything again.
3. Optionally edit the `name` fields in `effects/backgrounds.json`; they are kept the next time the script runs.

Stickers are listed in `effects/stickers.json`. Sizes and offsets are measured in distances between the eyes, from the eyes, nose or mouth (`anchor`); a positive `offsetY` moves the sticker up the face.

### Credits

- Stickers are based on [Noto Emoji](https://github.com/googlefonts/noto-emoji) (Apache License 2.0, see [`effects/stickers/LICENSE`](effects/stickers/LICENSE)). The headphones were reshaped and recoloured.
- Background pictures and videos come from [Pexels](https://www.pexels.com) and [Pixabay](https://pixabay.com/), under the [Pexels license](https://www.pexels.com/license/) and the [Pixabay Content License](https://pixabay.com/service/license-summary/).
- The MediaPipe models (`selfie_segmenter`, `face_landmarker`) are bundled with the Android app and loaded from MediaPipe's model storage on the web; see their model cards for terms.

## App icon

The iOS, macOS and Android apps share one icon, drawn by [`tools/make_app_icons.py`](tools/make_app_icons.py): an indigo-to-violet plate, a frosted glass disc and a white camera. Run `python3 tools/make_app_icons.py` (needs `pip install pillow numpy`) after changing its colors or shapes; it rewrites:

- iOS: `AppIcon` with light, dark and tinted variants.
- macOS: `MacAppIcon`, 16 to 1024 px on Apple's grid with a drop shadow.
- Android: an adaptive icon (vector background and foreground, plus a monochrome layer for themed icons), WebP launchers for Android 7, and the 512 px Play Store image.

At 32 px and below the glass disc is dropped and the camera is drawn larger, so it stays readable.
