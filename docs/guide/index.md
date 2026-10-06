---
title: "What is WebRTC Demo? Open source video call apps"
description: "Open source WebRTC video call app built five times, for the browser, Android, iOS, macOS and Windows. Every app can call the others. Run it and read the code."
---

# What is this?

WebRTC Demo is one video call app built five times: in the browser, on Android, iOS, macOS and Windows. Each one is a native app written with that platform's own tools, and they all talk to the same small servers. Any client can call any other one.

It is not a library. There is nothing to install into your project. It is a set of working apps that you can run, read and copy from.

## Who it is for {#who-it-is-for}

- You want calls in your app and need to see how the pieces fit together on your platform.
- You are bringing WebRTC to a platform that is not here yet (Flutter, React Native, Linux, a TV) and need to know exactly what to send so the existing apps can call yours.
- You want to see how things like E2EE between a browser and a phone, iOS screen sharing in the background, or a small SFU actually work.

## What you get {#what-you-get}

| Feature | Web | iOS | Android | macOS | Windows |
| --- | :-: | :-: | :-: | :-: | :-: |
| 1:1 video call, peer to peer | ✅ | ✅ | ✅ | ✅ | ✅ |
| Group call through your own SFU (optional) | ✅ | ✅ | ✅ | ✅ | ✅ |
| Chat over a data channel | ✅ | ✅ | ✅ | ✅ | ✅ |
| Share your screen or a video file | ✅ | ✅ | ✅ | ✅ | ✅ |
| Virtual backgrounds: blur, pictures and videos | ✅ | ✅ | ✅ | ✅ | ✅ |
| Face-tracked stickers | ✅ | ✅ | ✅ | ✅ | ✅ |
| End-to-end encryption, 1:1 and group | ✅ | ✅ | ✅ | ✅ | ✅ |
| Your own microphone level on your video | ✅ | ✅ | ✅ | ✅ | ✅ |
| Picture-in-picture | ✅ | ✅¹ | ✅ | ✅² | ✅² |

¹ On iOS, picture-in-picture works in 1:1 calls only.
² An always-on-top floating window on the Mac and the compact overlay window on Windows.

## What is in the repository {#what-is-in-the-repository}

| Folder | What it is |
| --- | --- |
| [`signaling-server/`](gh:signaling-server) | Node.js WebSocket relay for 1:1 calls. About 150 lines. |
| [`sfu-server/`](gh:sfu-server) | Optional Go server for group calls, built on Pion. |
| [`web/`](gh:web) | Vue 3 client. |
| [`android/`](gh:android) | Kotlin and Jetpack Compose. |
| [`ios/`](gh:ios) | The iOS and macOS apps, one Xcode project. |
| [`windows/`](gh:windows) | WinUI 3 on .NET, with a small C shim over libwebrtc. |
| [`effects/`](gh:effects) | Backgrounds and stickers that every app bundles. |
| [`tools/`](gh:tools) | Scripts that prepare the backgrounds and draw the app icons. |

## What keeps them working together {#what-keeps-them-working-together}

The five apps share no code across languages. What lets them call each other is a small contract that every client follows:

- the JSON messages on the WebSocket, and their order,
- who makes the offer (always the peer that was in the room first),
- the E2EE frame format and how the key is derived,
- the `media state` message and the 300 ms camera rule,
- for group calls, the SFU's protocol with two peer connections per client.

Follow it and your client can join a call with any of the five apps. [Port to a new platform](/porting) turns it into a checklist.

## How to read these docs {#how-to-read-these-docs}

- [Quick start](/guide/quick-start) gets two clients into a call.
- [How it works](/how-it-works/) has one page per feature. Each page explains the idea once, then shows how every platform does it.
- [Platforms](/platforms/) shows where things live in each app's code.
- [Port to a new platform](/porting) is the checklist for a new client.
- [Reference](/reference/messages) has every message, the versions in use and the known limits.

The same material, as one long file that also works offline, is in [`ARCHITECTURE.md`](gh:ARCHITECTURE.md).

## A demo, not a product {#a-demo-not-a-product}

The code is meant to show common WebRTC use cases and give you ideas. It may have bugs. Some things a production app needs are left out on purpose: there is no TURN server, no reconnect, no authentication, and the E2EE key goes through the server in plain form. See [limitations](/reference/versions#limitations).
