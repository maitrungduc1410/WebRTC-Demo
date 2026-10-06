---
title: "WebRTC troubleshooting: connection, audio and build problems"
description: "Fixes for common problems: the server can't be reached, no audio on iOS, Swift packages that fail to resolve, missing Windows DLLs and group call issues."
---

# Troubleshooting

## Every platform {#every-platform}

**"Can't reach the server".** The server must be reachable from the device: same network, port 4000 (signaling) or 4001 (SFU) open in the firewall of the machine running it. Use its LAN IP, not `localhost`, when it runs on another machine. `curl http://<address>:4000/` should answer `{"name":"signaling-server","ok":true}`.

**"Lost the connection to the signaling server".** The signaling socket closed during the call. There is no reconnect by design. Join the room again.

**"Room is full".** A 1:1 room holds two people. Use another room ID, or a [group call](/guide/group-calls).

**Black video and no sound with E2EE.** Both sides need E2EE on. A key mismatch shows no video rather than an error, because frames that can't be decrypted are dropped.

**The call never connects on some networks.** There is no TURN server, so calls can fail behind symmetric NATs or strict firewalls. Try both devices on the same Wi-Fi first.

## Group calls {#group-calls}

| Symptom | Likely cause |
| --- | --- |
| The lobby can't reach the group call server | Wrong address, server not running, or TCP 4001 blocked. Try `curl http://<address>:4001/` from the same network. On the web, check that the address has `:4001`. |
| Joined, tiles show names but no video | UDP 4001 blocked, or the server is behind NAT without `-public-ip`. |
| `Room is full` | The room already has `-max-participants` people. |
| `E2EE setting does not match the room` | Someone joined with a different E2EE switch than the person who created the room. |
| `bind: address already in use` | Another process uses port 4001. Stop it or use `-port`. |
| `go run .` fails on the Go version | Your Go is older than 1.21 and can't download 1.25 by itself. Install a newer Go. |

The server logs every join, leave and published track, prefixed with the room ID.

## iOS and macOS {#ios-and-macos}

**Swift packages fail to resolve.** In Xcode use **File › Packages › Reset Package Caches**, then **Resolve Package Versions**. WebRTC comes from the local package [`ios/Packages/WebRTC`](gh:ios/Packages/WebRTC), not straight from `webrtc-sdk/Specs`: the Specs manifest for `150.7871.01` doesn't resolve ("'v26' is unavailable"). The local package downloads the same binary with the same checksum.

**iOS: no audio in calls, video works.** The others never hear the iPhone and it plays nothing, and the Xcode console shows `Failed to set category and mode ... OSStatus error -50`. The webrtc-sdk fork doesn't configure the audio session the way upstream WebRTC does, so the app sets it to `playAndRecord` + `voiceChat` before every call in `CallViewModel.configureCallAudio()`. Keep that when you upgrade the package. If iOS rejects the settings again, the call shows "Call audio didn't start: iOS refused the audio settings".

**macOS: screen sharing shows nothing.** Grant Screen Recording in System Settings › Privacy & Security, then relaunch the app.

## Android {#android}

**A Compose or Material 3 dependency needs a newer compileSdk or AGP.** Material 3 Expressive is only in the `material3` 1.5 alphas. The app pins `1.5.0-alpha18` with Compose BOM `2026.06.01`, the newest that build with AGP 8.13 and compileSdk 36. Newer ones need AGP 9.1 and compileSdk 37, so upgrade those first.

## Windows {#windows}

**"rtc_shim.dll or libwebrtc.dll is missing".** Run `native/RtcShim/scripts/build-shim.ps1` for the architecture you build (`-Arch x64` or `-Arch arm64`), then rebuild the app.

**"…cannot be loaded because running scripts is disabled on this system".** Run `Set-ExecutionPolicy -Scope Process Bypass` in that PowerShell window first, or `powershell -ExecutionPolicy Bypass -File native/RtcShim/scripts/build-shim.ps1`.

**`NETSDK1233` warning.** The solution was opened in Visual Studio 2022. Use Visual Studio 2026.

**`rtc_shim ABI … does not match`.** The DLL is older than the bindings. Rebuild the shim.

**No camera or microphone.** Settings → Privacy & security → Camera / Microphone → turn on *Let desktop apps access…*. The call still works receive-only, and the call screen says what is missing.

**"No camera found" while the camera works elsewhere.** Windows gives a camera to one app at a time. Close the browser tab or app that has it, then press `V`.

**Your own video stays black.** Check that the Camera app shows the camera and nothing else uses it. A virtual camera (OBS and the like) sends black while its app isn't running, and an infrared camera has no picture, so pick another one from the menu next to the camera button. Some drivers send nothing at 720p, like the FaceTime HD camera under Boot Camp, so turning such a camera on can take several seconds while the app tries smaller formats.

**The others can't hear you.** Rebuild the shim with `build-shim.ps1`. An older one leaves echo cancellation to Windows' voice-capture DMO, which doesn't capture in calls. Also check the input level and mute in the microphone's **Levels** tab in the Sound control panel.

**A microphone or speaker is unplugged mid-call.** Within about 2 seconds the call moves to the Windows default communications device and says "Switched to …".

**"Couldn't load that effect".** The picture, video or model could not be opened, and the previous choice comes back. Video backgrounds play only in formats Media Foundation can decode on that PC.

More Windows notes are in [`windows/README.md`](gh:windows/README.md#troubleshooting).
