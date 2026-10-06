# Platforms

Five apps, each written with its own platform's tools. They share no code across languages, only the [contract](/guide/#what-keeps-them-working-together), the [`effects`](gh:effects) folder and the app icon. iOS and macOS are the exception: the Mac app compiles most of the iOS code.

<PlatformPicker />

## Which one to read first {#which-one-to-read-first}

Start with the **web client**, even if you target something else. The browser's WebRTC API is the reference that the native SDKs copy, the code is short, and [`useCall.ts`](gh:web/src/call/useCall.ts) holds the whole 1:1 call in one file. Then read the app closest to your platform:

| You are building for | Read |
| --- | --- |
| Flutter, React Native, or anything on top of webrtc-sdk | [Android](/platforms/android) and [iOS](/platforms/ios): same SDK family |
| A desktop app on libwebrtc directly | [Windows](/platforms/windows): the C shim shows the whole native API surface a call needs |
| Another browser-based app | [Web](/platforms/web) |
| Apple platforms | [iOS](/platforms/ios) and [macOS](/platforms/macos) |

## Where each app keeps its settings {#where-each-app-keeps-its-settings}

| App | Storage | What is saved |
| --- | --- | --- |
| Web | `localStorage` | Server addresses, effect choice |
| Android | `SharedPreferences` | Server addresses, effect choice |
| iOS, macOS | `UserDefaults` | Server addresses, effect choice, devices on the Mac |
| Windows | `%LOCALAPPDATA%\WebRtcDemo\settings.json` | Server addresses, E2EE switch, devices, effect choice |

## Libraries {#libraries}

| App | WebRTC |
| --- | --- |
| Web | The browser |
| Android | [`io.github.webrtc-sdk:android`](https://github.com/webrtc-sdk) `150.7871.01` |
| iOS, macOS | [`webrtc-sdk/Specs`](https://github.com/webrtc-sdk/Specs) `150.7871.01`, through the local Swift package [`ios/Packages/WebRTC`](gh:ios/Packages/WebRTC) |
| Windows | [`webrtc-sdk/libwebrtc`](https://github.com/webrtc-sdk/libwebrtc) `m150.7871.03`, through a C shim |

All four native builds come from the same m150 branch of the webrtc-sdk fork, which is what makes their `FrameCryptor` identical. Full versions are in [Versions and limitations](/reference/versions).
