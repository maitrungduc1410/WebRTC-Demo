# Windows client

A native Windows client: WinUI 3 (Windows App SDK 2.5) on .NET 10, unpackaged and
self-contained. WebRTC is the prebuilt [webrtc-sdk/libwebrtc](https://github.com/webrtc-sdk/libwebrtc)
release (m150, `libwebrtc.m150.7871.03`), reached through a small C shim so that C# only ever
calls a flat C API. It talks to the same signaling server and interoperates with the web, Android,
iOS and macOS clients, including end-to-end encryption, and joins group calls through the
repository's `sfu-server`.

```
windows/
├── native/RtcShim/           C shim over libwebrtc (CMake): rtc_shim.dll
│   ├── include/rtc_shim.h      the whole C API
│   ├── src/                    peer connection, media, file source (Media Foundation)
│   ├── tests/loopback.cpp      two peers in one process, E2EE, frames, chat
│   ├── libwebrtc.lock.json     pinned release + SHA-256 per asset
│   └── scripts/                fetch-libwebrtc.{ps1,sh}, build-shim.ps1
├── src/
│   ├── WebRtcDemo.Interop/     LibraryImport bindings, SafeHandles, native callbacks
│   ├── WebRtcDemo.Effects/     backgrounds and stickers: ONNX models, compositing (no UI)
│   ├── WebRtcDemo.Core/        signaling, call state machine, media, settings (no UI)
│   └── WebRtcDemo.App/         WinUI 3 app
├── models/                     the effects models as ONNX, and convert.sh that makes them
├── tests/WebRtcDemo.Core.Tests/
├── scripts/verify.sh           everything that can be checked without Windows
└── WebRtcDemo.slnx
```

## Requirements

- Windows 10 1809 or later (Windows 11 recommended for Mica), x64 or arm64
- Visual Studio 2026 (Community is free and installs next to 2022) with two workloads:
  - **WinUI application development** for the C# app (Windows App SDK, .NET 10 SDK)
  - **Desktop development with C++** for `rtc_shim.dll` (MSVC, Windows SDK, C++ CMake tools);
    on an arm64 PC, or to build for one, also *MSVC ARM64 build tools*
- Internet access for the first build: NuGet packages and the pinned libwebrtc release are
  downloaded. Everything else (WebRTC, Windows App SDK, ONNX Runtime, the models) comes with the
  repository or those downloads; nothing else needs installing.

Visual Studio 2022 17.14 may build it, but Microsoft doesn't support targeting .NET 10 there: the
build warns `NETSDK1233` and not every IDE feature works. The scripts run in Windows PowerShell 5.1
or PowerShell 7 and find Visual Studio themselves (2026 first, else 2022).

## Build and run

From any PowerShell window:

```powershell
cd windows

# 1. Download libwebrtc (SHA-256 checked) and build rtc_shim.dll for this PC's architecture.
#    Output: native/RtcShim/out/install/<arch>/{rtc_shim.dll, libwebrtc.dll}
./native/RtcShim/scripts/build-shim.ps1            # or: -Arch x64,arm64 for both

# 2. Build and run the app (it copies both DLLs next to WebRtcDemo.exe).
dotnet run --project src/WebRtcDemo.App -p:Platform=x64
```

Or open `WebRtcDemo.slnx` in Visual Studio, pick **x64** (or **ARM64**) and press F5. Run
`build-shim.ps1` first; the build fails with a clear message if `rtc_shim.dll` is missing.

To ship it, publish the self-contained folder and zip it:

```powershell
dotnet publish src/WebRtcDemo.App -c Release -p:Platform=x64 -o out/WebRtcDemo-x64
```

Start the signaling server (`signaling-server/`, `npm install && npm run dev`) and enter the
address it prints in the lobby under **Signaling server**, e.g. `192.168.1.10:4000`. The address
is normalized the way the web
client does and remembered in `%LOCALAPPDATA%\WebRtcDemo\settings.json`, together with the E2EE
switch and the chosen camera, microphone and speaker. The default is `http://localhost:4000`.

For a group call, start `sfu-server/` (`go run .`), choose **Group call (SFU)** at the top of the
lobby and enter its address under **Group call server**. Like the Android client, a bare host gets
port 4001, `http(s)://` becomes `ws(s)://` and a path is dropped, so `192.168.1.10` means
`ws://192.168.1.10:4001`. It is saved separately from the signaling address, even when it is
localhost; until one is saved (or after the field is cleared) it is port 4001 on the signaling
server's host. The lobby shows each server's status by polling
`GET /` every 5 s (the server must answer with its name, `signaling-server` or `sfu-server`).

Windows asks for camera and microphone access the first time (Settings → Privacy & security →
Camera / Microphone → *Let desktop apps access…* must be on).

## Tests

```powershell
dotnet test --project tests/WebRtcDemo.Core.Tests
```

- Unit tests cover the call state machine (offerer and answerer, the E2EE key before the offer,
  queued ICE, the 300 ms camera rule, a closed socket or a fatal error ending the call, room
  full, hang-up), the signaling messages (shapes the server expects, what the other clients
  send), both address normalizations (expected values from the web and Android code), the lobby,
  avatar colors (same as iOS), settings and image helpers.
- Group calls: the SFU messages, SDP parsing and the receiver → participant mapping by mid (with
  recycled m-lines), the grid layout, the fit/fill state, the active speaker, and the whole flow
  through the view model (publish after `joined`, held and serialized subscribe offers, `pc` on
  candidates, joined/left toasts, chat names, mute/hide everyone for later joiners, every way a
  call ends).
- Effects: the catalog against the repository's `effects/` folder, sticker placement (the same
  cases as Android), the image routines, and the real models on a test portrait (the mask covers
  the person, the face points land on the eyes, nose and mouth) through the whole processor.
- With a signaling server on `http://localhost:4000` (or `WEBRTC_DEMO_SIGNALING_URL`), real
  WebSocket clients run the call flow through it, including the E2EE key and a full room.
- With `rtc_shim` next to the tests (automatic on Windows after `build-shim.ps1`), the bindings
  run two encrypted peer connections in-process, and two complete clients call each other
  through the signaling server. With an `sfu-server` on `ws://localhost:4001` (or
  `WEBRTC_DEMO_SFU_URL`), complete group clients join one room, with and without E2EE: two that
  must receive each other's video (a color pushed into what each sends), chat and mute state; and
  four where A, B and C join, B leaves and D joins on B's recycled m-lines, and D's color must
  arrive on D's tile at A and C with B's tile gone.
  These skip, with the reason, when the DLLs or a server are missing.

On Linux (CI, or without a Windows machine) `scripts/verify.sh` builds the shim against the Linux
libwebrtc release, runs its loopback test, runs all the .NET tests against it, and compiles the
app for x64 and arm64. Linux needs an audio server (PulseAudio with a null sink is enough), since
libwebrtc aborts when no audio device exists.

## Features

| Feature | Status |
| --- | --- |
| Join a room, call, hang up (a lost server connection ends the call, as on the other clients) | ✅ |
| Group calls through `sfu-server`: adaptive grid, active speaker, people list, chat with names | ✅ |
| End-to-end encryption (same key derivation as all clients; VP8 is negotiated under E2EE) | ✅ |
| Mute, camera on/off (the peer is told 300 ms before the camera closes) | ✅ |
| Pick camera, microphone and speaker; switch camera; an unplugged microphone or speaker falls back to the default one | ✅ |
| Share a screen or a window, with live thumbnails | ✅ |
| Share a video file (any format Windows can play, e.g. MP4, MOV, WMV; read with Media Foundation and looped) | ✅ |
| Chat over the data channel, unread badge | ✅ |
| Mute their audio, hide their video (local only); in a group call, everyone's, later joiners too | ✅ |
| Fit or fill the remote video (auto-fit for screen shares; double-click to toggle, per tile in a group) | ✅ |
| Placeholder with blurred last frame, gradient avatar, voice ring | ✅ |
| Picture-in-picture (always-on-top compact window; the active speaker in a group call) | ✅ |
| Draggable self view that springs to a corner, labelled "You" with a red mic-off badge while muted | ✅ |
| Your microphone's level on the self view (three bars, also while waiting alone) | ✅ |
| Keyboard: M mute, V camera, C chat, B backgrounds and effects, F fit (1:1 only), P picture-in-picture, Esc closes the chat | ✅ |
| Live server status in the lobby (signaling server or SFU) | ✅ |
| Backgrounds: none, slight blur, blur, pictures, looping videos | ✅ |
| Face-tracked stickers, combined with any background | ✅ |

## How it is put together

**Native shim.** libwebrtc's API is C++ classes built with MSVC and a static CRT, so the shim
links it from C++ (`/MT`, `RTC_DESKTOP_DEVICE`, `LIB_WEBRTC_API_DLL`) and exports plain C:
opaque handles with explicit release, UTF-8 strings, and callbacks with a `void* user` argument.
Asynchronous calls (create offer, set description, stats) complete through callbacks. A closed
peer connection rejects calls instead of crashing. Remote and local video come out as BGRA via
sinks that downscale and rotate. E2EE uses libwebrtc's frame cryptor with the key provider
options every client uses (ARCHITECTURE.md §9.3). The camera, the screen/window capturer and a
"custom" source (fed I420 frames) are all video sources; the file source reads with Media
Foundation on its own thread.

**Interop.** `LibraryImport` (source-generated, trimming and AOT safe), one `SafeHandle` type per
native object, and `UnmanagedCallersOnly` callbacks that find their managed target through an id
registry (no GCHandles to leak). Exceptions never unwind into native code.

**Core.** `SignalingClient` opens one WebSocket per call to `ws(s)://host/ws` and speaks the
server's JSON messages (`join`, `peer joined`, `offer`, `answer`, `candidate`, `encryption key`,
`encryption key received`, `media state`, `leave`, `error`). There is no reconnect: the server
frees the seat when the socket closes, so a closed socket ends the call with a message, as on the
other clients. `ServerProbe` checks a server's `GET /` for the lobby. `CallViewModel` is the call
state machine, the same flow as the web and iOS clients: the peer already in the room makes the
offer, the E2EE key goes before it, and the peer hears about a camera turning off 300 ms before
it closes. `NativeCallMedia` owns devices and one peer connection at a time, and switches between
camera, screen and file by swapping the video sender's track, which avoids renegotiation and keeps
the sender's cryptor. Everything in Core is UI-thread code with no WinUI dependency, so it is
unit tested anywhere.

**App.** WinUI 3 with the UI built in C# (no XAML pages). That keeps it to plain C# that the
compiler checks on any OS (`scripts/verify.sh`), and bindings are explicit
(`Ui/Bindings.cs`). Video goes to a `SwapChainPanel`: frames are uploaded into a D3D11 composition
swap chain the size of the frame, and the swap chain's matrix scales it to cover the view. As on
iOS, Android and macOS, fit only scales that cover-sized panel down, on its composition visual,
with a spring (damping 0.86, 0.5 s period), so double-clicking a remote video (or a screen share
starting or stopping) zooms smoothly on the GPU without a layout pass or a redraw. It jumps on
the first frame, when Windows turns animations off, and when the fit is chosen in reaction to a
new frame size or a resized window (e.g. a rotated phone). Frames
arrive on a WebRTC thread into a one-slot mailbox and are drawn on the next composition tick, so a
busy UI drops frames rather than queueing them.

**Microphone level.** As on the web, Android and iOS, the self view shows three white bars
(gains 0.6, 1, 0.6; 3 px wide, 3 to 12 px tall) in a 28 px dark circle at its bottom right while
the mic is on, and the "You" pill narrows to make room; muted, the circle shrinks away (the pill's
red badge says it) and pops back with the web's bouncy curve. Every 80 ms `CallViewModel` reads
the peak since the last read, maps -50..-10 dBFS onto 0..1 and keeps at least 0.75 of the
previous level, so the bars fall smoothly; they ease to each new height over 100 ms and jump
when Windows turns animations off. In a call the peak is the sending connection's audio
media-source `audioLevel` (`rtc_pc_get_local_audio_level`, shim ABI 5), the 1:1 or the group
publish connection (a group call shows nothing until that opens, as on iOS). Waiting alone in a
1:1 room there is no connection, so `WasapiMicMeter`
captures the selected microphone (by its endpoint ID, the default communications device while
none is picked) in shared mode on its own thread. It restarts when the microphone changes (a pick
or an unplug fallback), and it stops when you mute, leave, or the call's connection opens, before
WebRTC starts recording. The other clients' large variant, for a self view that fills the stage,
isn't used: here the self view is always a corner tile, and the waiting card holds the stage.

**Effects.** "Backgrounds and effects" (toolbar, More, or `B`) opens a panel in the chat's
place with a live preview of what is sent and two tabs, Backgrounds and Filters, reading the
repository's [`effects`](../effects) folder (linked into the app output). Only the camera is
processed, and not while sharing. The models are MediaPipe's `selfie_segmenter` and
`face_landmarker` (the files the Android app bundles), converted to ONNX by
[`models/convert.sh`](models/convert.sh), and run by ONNX Runtime through Windows ML, which
picks the GPU or NPU provider certified for the PC and falls back to the CPU. The camera's
frames (up to 720p) go to `EffectsProcessor`: the person mask and the face points run on their
own threads on a 512 px copy, while every frame is composited on the CPU and pushed as I420 into
a custom source whose track replaces the camera's on the sender. The choice is saved, and a call
that starts with an effect sends no video until the first mask is ready.

## Group calls

`GroupCallClient` does what the web's `useGroupCall.ts` does over the SFU's WebSocket: it joins as
"Windows", and after `joined` (with the room's key under E2EE) and local media it sends its media
state and offers a **publish** connection (send-only audio and video, VP8 first, sender cryptors
with E2EE). The server offers a **subscribe** connection and re-offers whenever tracks change;
those offers are answered one at a time, and ones that arrive before our connections exist wait.
ICE candidates carry `pc: "publish" | "subscribe"`. Every receiver belongs to the participant
whose id is the stream id in the latest offer, looked up by mid, because the SFU recycles m-lines
of people who left. With E2EE the receivers get their cryptors before we answer, so no frame is
decrypted with the wrong state. Audio levels come per receiver every 300 ms and give the active
speaker (above 0.03, held for 1.2 s). A fatal server error, a closed socket, a failed media
connection or a subscribe offer we can't answer (the server holds every later one until it is
answered) ends the call, and the lobby shows why.

On screen:

- The others fill an adaptive grid (the column count that gives the largest tiles, last row
  centred). A tile fills its cell, and fits (letterboxed) while that participant presents.
  **Double-click** a tile to switch it between fit and fill, with a spring zoom; that lasts
  until they start or stop presenting. F does nothing in a group call.
- Each tile shows "name · short id" after one status mark: a red mic-off circle while muted, a
  presenting glyph while presenting, else a small green dot. The marks swap with the web's
  bouncy scale and fade, and the label pill resizes smoothly with them. A green ring marks the
  active speaker. Without video (none yet, camera off, or hidden by you) it shows their avatar with
  a voice ring.
- While you are alone, a card says how others join. The people button in the top bar opens the
  list: you first and highlighted, then everyone with their mic and presenting state; it updates
  while open. Your own
  tile is labelled "You · Windows · id".
- Chat goes through the SFU, and each message shows its sender. **Mute everyone** and **Hide
  everyone's video** (More menu) are local only and also cover people who join later.
- E2EE, device pickers, screen, window and file sharing, and backgrounds and effects work as in a
  1:1 call; they act on the publish connection's senders.
- Picture-in-picture shows one participant: the active speaker, else the first with video, else
  the first. A grid at that size shows nobody well. It closes by itself when the last one leaves.
- The controls stay visible during a group call, as on the web.

## Troubleshooting

- **"Couldn't load that effect"**: the picture, video or model could not be opened, and the
  previous choice comes back. Video backgrounds use Media Foundation, so they play only in formats
  this PC can decode. A model that its GPU provider rejects is loaded on the CPU instead. If a
  model fails during the call, video is held (the peer never sees the room) while that model is
  reloaded on the CPU and the effect tried once more. If that fails too, the camera turns off with
  this message and the choice is kept; turning the camera back on tries again.

- **"rtc_shim.dll or libwebrtc.dll is missing"**: run `native/RtcShim/scripts/build-shim.ps1`
  for the architecture you build (`-Arch x64` / `-Arch arm64`), then rebuild the app.
- **"…cannot be loaded because running scripts is disabled on this system"** (`PSSecurityException`,
  `UnauthorizedAccess`): run `Set-ExecutionPolicy -Scope Process Bypass` in that PowerShell window
  first, or run the script as
  `powershell -ExecutionPolicy Bypass -File native/RtcShim/scripts/build-shim.ps1`, or allow local
  scripts once with `Set-ExecutionPolicy -Scope CurrentUser RemoteSigned`. With the last one, a
  repository downloaded as a ZIP also needs `Get-ChildItem -Recurse -Filter *.ps1 | Unblock-File`.
- **`NETSDK1233` warning**: the solution was opened in Visual Studio 2022; use Visual Studio 2026.
- **`rtc_shim ABI … does not match`**: the DLL is older than the bindings; rebuild the shim.
- **No camera or microphone**: check the Windows privacy settings above; the call still works
  receive-only, and a notice on the call screen says what is missing.
- **Your own video stays black**: check that the Camera app shows the camera, that no other app is
  using it, and that *Let desktop apps access your camera* is on. If several cameras are listed,
  pick another one from the menu next to the camera button: a virtual camera (OBS and the like)
  sends black while its app isn't running, and an infrared camera has no picture. The camera is
  read through Media Foundation, as the Camera app does: the shim asks for the format closest to
  1280×720 at 30 fps and, when no frame arrives within 4 s, tries the next one (up to four, down
  to 640×480). Some drivers send nothing at 720p, such as the FaceTime HD camera under Boot Camp, so
  turning such a camera on can take several seconds. A camera Media Foundation can't read (most
  virtual cameras) goes through libwebrtc's DirectShow capturer instead. In a Debug run, set
  `WEBRTC_DEMO_LOG=info` before starting the app: the Output window then shows `<camera> through
  Media Foundation, NV12 1280x720@30` (the format in use), each format that sent nothing, and
  `…; capturing through DirectShow` with the reason when it falls back.
- **A black toolbar above the window content while debugging**: that is Visual Studio's XAML
  in-app toolbar (Live Visual Tree, element selection), not part of the app. Turn it off in Tools ›
  Options › Debugging › XAML Hot Reload › *Show runtime tools in application*.
- **Microphone or speaker unplugged mid-call**: within about 2 s the call moves to the Windows
  default communications device (the first in the list if that is unknown) and says "Switched
  to …"; with no microphone left it turns the mic off and tells the others. The audio sender, its
  E2EE cryptor and the connection stay as they are. The saved choice is kept, so the device is
  used again on the next call. Until a device is picked, calls use (and the menu shows) the
  default communications device, and move with it when Windows changes the default.
- **"Can't reach the server"** / **"Can't reach the group call server."**: the server must be
  reachable from this PC (same network, port 4000 for signaling or 4001 for the SFU open in the
  firewall of the machine running it). Use its LAN IP, not `localhost`, when it runs elsewhere.
- **"That group call uses end-to-end encryption…"** (or doesn't): everyone in a group room must
  use the same E2EE setting; the first one in decides.
- **Group call joins but nobody's video arrives**: `sfu-server` advertises the host's
  non-loopback IPv4 addresses as its ICE candidates; a machine without one (or a firewall on its
  UDP ports) leaves the media connection failing, which ends the call with "Couldn't connect the
  media to the group call server."
- **Black remote video with E2EE**: both sides need E2EE on (they exchange the key when the call
  starts). A key mismatch shows no video rather than an error.
- **Download fails in `fetch-libwebrtc`**: the release asset is pinned by SHA-256 in
  `native/RtcShim/libwebrtc.lock.json`; a proxy that rewrites downloads will fail the check.