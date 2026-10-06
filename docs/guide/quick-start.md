---
title: "Quick start: run a WebRTC video call in minutes"
description: "Start the Node.js signaling server, open the web client in two browser windows and make your first WebRTC call. Then add Android, iOS, macOS or Windows."
---

# Quick start

A 1:1 call needs the signaling server and two clients in the same room. Group calls also need the SFU server, see [Group calls](/guide/group-calls).

## Requirements {#requirements}

| To run | You need |
| --- | --- |
| Signaling server, web client | Node.js 20.19 or newer (Vite 7 needs it) |
| Android | Android Studio with JDK 17+, a device on Android 7.0 (API 24) or newer |
| iOS, macOS | Xcode 26. The iOS app needs an iPhone on iOS 26, the Mac app needs macOS 26 (Liquid Glass needs the 26 releases). Dependencies come from Swift Package Manager. |
| Windows | Windows 10 1809 or newer (11 for Mica), Visual Studio 2026 with the *WinUI application development* and *Desktop development with C++* workloads |
| Group calls | Go 1.25 or newer |

## 1. Start the signaling server {#_1-start-the-signaling-server}

```sh
cd signaling-server
npm install
npm run dev
```

It prints the address the clients should use:

```
Signaling server listening on port 4000
Network access via: 192.168.1.10:4000
```

The server is a plain WebSocket relay on `ws://<address>/ws`. Set `PORT` to use another port.

## 2. Start two clients {#_2-start-two-clients}

Any two will do. They don't have to be the same platform.

::: code-group

```sh [Web]
cd web
npm install
npm run dev
# open http://localhost:5173 in two browser windows
```

```sh [Android]
# Open android/ in Android Studio and run the "app" configuration,
# or install from a terminal:
cd android
./gradlew :app:installDebug
```

```sh [iOS]
open ios/WebRTCDemo.xcodeproj
# Pick the WebRTCDemo scheme and run on a real iPhone.
# The camera and screen sharing need real hardware.
```

```sh [macOS]
open ios/WebRTCDemo.xcodeproj
# Pick the WebRTCDemoMac scheme and the "My Mac" destination.
```

```powershell [Windows]
cd windows
# Downloads libwebrtc (SHA-256 checked) and builds rtc_shim.dll
./native/RtcShim/scripts/build-shim.ps1
dotnet run --project src/WebRtcDemo.App -p:Platform=x64
```

:::

On iOS and macOS, Xcode resolves the WebRTC Swift package the first time you open the project. There is no CocoaPods and no workspace. On Windows you can also open `windows/WebRtcDemo.slnx` in Visual Studio and run the x64 or ARM64 configuration.

## 3. Point the clients at the server {#_3-point-the-clients-at-the-server}

Each client remembers the address once you set it.

| Client | Where to change it | Default |
| --- | --- | --- |
| Web | Click the server address under **Join room** | Port 4000 on the host that serves the page |
| Android | Tap the server address at the bottom of the lobby | `serverAddress` in [`strings.xml`](gh:android/app/src/main/res/values/strings.xml) |
| iOS | Tap the server address at the bottom of the lobby | `SignalingServer.defaultURL` in [`SignalingServer.swift`](gh:ios/WebRTCDemo/SignalingServer.swift) |
| macOS | **WebRTC Demo › Settings…** (⌘,) | `http://localhost:4000` |
| Windows | **Signaling server** in the lobby | `http://localhost:4000` |

A phone can't reach `localhost` on your computer. Give it the LAN address the server printed, and keep both on the same network.

## 4. Join the same room {#_4-join-the-same-room}

Type the same room ID on both clients and join. The first one waits, the second one starts the call.

To try end-to-end encryption, turn on E2EE in the lobby on **both** sides before joining. If only one side has it on, the other side sees black video and hears nothing.

::: tip No reconnect
The clients keep the signaling socket open for the whole call. If it drops (server stopped, Wi-Fi to 4G switch), the call ends with "Lost the connection to the signaling server", even if audio and video were still flowing. Join the room again.
:::

## Next {#next}

- [Using the app](/guide/using-the-app): controls, gestures and keyboard shortcuts.
- [Group calls](/guide/group-calls): more than two people.
- [How it works](/how-it-works/): what happens under the hood.
