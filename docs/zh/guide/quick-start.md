# 快速开始

1:1 通话只需要信令服务器，再加上两个进入同一房间的客户端。多人通话还需要 SFU 服务器，见[多人通话](/zh/guide/group-calls)。

## 环境要求 {#requirements}

| 运行 | 需要 |
| --- | --- |
| 信令服务器、Web 客户端 | Node.js 20.19 或更高版本（Vite 7 的要求） |
| Android | Android Studio 和 JDK 17+，一台 Android 7.0（API 24）或更高版本的设备 |
| iOS、macOS | Xcode 26。iOS 应用需要运行 iOS 26 的 iPhone，Mac 应用需要 macOS 26（Liquid Glass 需要 26 系列版本）。依赖通过 Swift Package Manager 获取。 |
| Windows | Windows 10 1809 或更高版本（Mica 需要 Windows 11），Visual Studio 2026，并安装 *WinUI application development* 和 *Desktop development with C++* 工作负载 |
| 多人通话 | Go 1.25 或更高版本 |

## 1. 启动信令服务器 {#_1-start-the-signaling-server}

```sh
cd signaling-server
npm install
npm run dev
```

启动后会打印出客户端应该使用的地址：

```
Signaling server listening on port 4000
Network access via: 192.168.1.10:4000
```

这个服务器就是一个普通的 WebSocket 中继，地址是 `ws://<address>/ws`。想换端口可以设置 `PORT`。

## 2. 启动两个客户端 {#_2-start-two-clients}

任意两个都行，不必是同一个平台。

::: code-group

```sh [Web]
cd web
npm install
npm run dev
# 在两个浏览器窗口中打开 http://localhost:5173
```

```sh [Android]
# 在 Android Studio 中打开 android/，运行 "app" 配置，
# 或者在终端中安装：
cd android
./gradlew :app:installDebug
```

```sh [iOS]
open ios/WebRTCDemo.xcodeproj
# 选择 WebRTCDemo scheme，在真机 iPhone 上运行。
# 摄像头和屏幕共享都需要真实硬件。
```

```sh [macOS]
open ios/WebRTCDemo.xcodeproj
# 选择 WebRTCDemoMac scheme 和 "My Mac" 运行目标。
```

```powershell [Windows]
cd windows
# 下载 libwebrtc（校验 SHA-256）并构建 rtc_shim.dll
./native/RtcShim/scripts/build-shim.ps1
dotnet run --project src/WebRtcDemo.App -p:Platform=x64
```

:::

在 iOS 和 macOS 上，第一次打开工程时 Xcode 会自动解析 WebRTC 的 Swift package。不需要 CocoaPods，也没有 workspace。在 Windows 上，也可以用 Visual Studio 打开 `windows/WebRtcDemo.slnx`，运行 x64 或 ARM64 配置。

## 3. 让客户端连接服务器 {#_3-point-the-clients-at-the-server}

地址设置一次之后，各客户端都会记住。

| 客户端 | 在哪里修改 | 默认值 |
| --- | --- | --- |
| Web | 点击 **Join room** 下方的服务器地址 | 提供页面的主机上的 4000 端口 |
| Android | 点击大厅底部的服务器地址 | [`strings.xml`](gh:android/app/src/main/res/values/strings.xml) 中的 `serverAddress` |
| iOS | 点击大厅底部的服务器地址 | [`SignalingServer.swift`](gh:ios/WebRTCDemo/SignalingServer.swift) 中的 `SignalingServer.defaultURL` |
| macOS | **WebRTC Demo › Settings…**（⌘,） | `http://localhost:4000` |
| Windows | 大厅中的 **Signaling server** | `http://localhost:4000` |

手机访问不到你电脑上的 `localhost`。请填写服务器打印出的局域网地址，并确保手机和电脑连在同一个网络中。

## 4. 加入同一个房间 {#_4-join-the-same-room}

在两个客户端上输入同一个房间 ID 并加入。先加入的一方等待，后加入的一方发起通话。

如果想试试端到端加密，请在加入之前，在**双方**的大厅里都打开 E2EE。如果只有一方打开，另一方会看到黑屏，也听不到声音。

::: tip 不会自动重连
整个通话期间，客户端都会保持信令 socket 处于打开状态。一旦它断开（服务器停止、Wi-Fi 切换到 4G），即使音视频还在正常传输，通话也会结束，并提示 "Lost the connection to the signaling server"。重新加入房间即可。
:::

## 下一步 {#next}

- [使用应用](/zh/guide/using-the-app)：控件、手势和键盘快捷键。
- [多人通话](/zh/guide/group-calls)：两个人以上的通话。
- [工作原理](/zh/how-it-works/)：底层是怎么运作的。
