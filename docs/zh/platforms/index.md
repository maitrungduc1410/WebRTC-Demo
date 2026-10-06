# 平台

五个应用，各自用自己平台的工具编写。它们在不同语言之间不共享代码，共享的只有[约定](/zh/guide/#what-keeps-them-working-together)、[`effects`](gh:effects) 目录和应用图标。iOS 和 macOS 是例外：Mac 应用编译了大部分 iOS 代码。

<PlatformPicker />

## 先读哪一个 {#which-one-to-read-first}

即使你的目标是别的平台，也建议从 **Web 客户端**开始读。浏览器的 WebRTC API 是各原生 SDK 效仿的参照，代码很短，而且 [`useCall.ts`](gh:web/src/call/useCall.ts) 一个文件就包含了完整的 1:1 通话。然后再读和你的平台最接近的那个应用：

| 你要开发的是 | 推荐阅读 |
| --- | --- |
| Flutter、React Native，或任何基于 webrtc-sdk 的应用 | [Android](/zh/platforms/android) 和 [iOS](/zh/platforms/ios)：同一个 SDK 家族 |
| 直接基于 libwebrtc 的桌面应用 | [Windows](/zh/platforms/windows)：C shim 展示了一次通话所需的全部原生 API |
| 其他基于浏览器的应用 | [Web](/zh/platforms/web) |
| Apple 平台 | [iOS](/zh/platforms/ios) 和 [macOS](/zh/platforms/macos) |

## 各应用的设置保存在哪里 {#where-each-app-keeps-its-settings}

| 应用 | 存储位置 | 保存的内容 |
| --- | --- | --- |
| Web | `localStorage` | 服务器地址、特效选择 |
| Android | `SharedPreferences` | 服务器地址、特效选择 |
| iOS、macOS | `UserDefaults` | 服务器地址、特效选择，Mac 上还有设备选择 |
| Windows | `%LOCALAPPDATA%\WebRtcDemo\settings.json` | 服务器地址、E2EE 开关、设备、特效选择 |

## 依赖库 {#libraries}

| 应用 | WebRTC |
| --- | --- |
| Web | 浏览器自带 |
| Android | [`io.github.webrtc-sdk:android`](https://github.com/webrtc-sdk) `150.7871.01` |
| iOS、macOS | [`webrtc-sdk/Specs`](https://github.com/webrtc-sdk/Specs) `150.7871.01`，通过本地 Swift package [`ios/Packages/WebRTC`](gh:ios/Packages/WebRTC) 引入 |
| Windows | [`webrtc-sdk/libwebrtc`](https://github.com/webrtc-sdk/libwebrtc) `m150.7871.03`，通过 C shim 调用 |

四个原生版本都构建自 webrtc-sdk fork 的同一个 m150 分支，所以它们的 `FrameCryptor` 完全一致。完整版本信息见[版本与限制](/zh/reference/versions)。
