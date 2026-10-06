---
title: "WebRTC 常见问题：连接、音频与构建"
description: "常见问题的解决办法：连不上服务器、iOS 没有声音、Swift 包无法解析、Windows 缺少 DLL，以及多人通话的问题。"
---

# 常见问题

## 所有平台 {#every-platform}

**"Can't reach the server"（无法连接服务器）。** 设备必须能访问到服务器：处于同一网络，并且运行服务器的机器在防火墙中开放了 4000 端口（信令）或 4001 端口（SFU）。服务器运行在另一台机器上时，请使用它的局域网 IP，而不是 `localhost`。`curl http://<address>:4000/` 应该返回 `{"name":"signaling-server","ok":true}`。

**"Lost the connection to the signaling server"（与信令服务器断开连接）。** 信令 socket 在通话中关闭了。按设计不会自动重连，重新加入房间即可。

**"Room is full"（房间已满）。** 1:1 房间最多容纳两人。换一个房间 ID，或者改用[多人通话](/zh/guide/group-calls)。

**开启 E2EE 后黑屏且没有声音。** 双方都需要打开 E2EE。密钥不匹配时不会报错，只会没有画面，因为无法解密的帧会被直接丢弃。

**在某些网络下始终无法接通。** 没有 TURN 服务器，所以在对称型 NAT 或严格的防火墙之后，通话可能会失败。可以先让两台设备连接同一个 Wi-Fi 试试。

## 多人通话 {#group-calls}

| 现象 | 可能的原因 |
| --- | --- |
| 大厅连不上多人通话服务器 | 地址填错、服务器没有运行，或者 TCP 4001 被屏蔽。在同一网络中试试 `curl http://<address>:4001/`。如果是 Web 端，检查地址里是否带了 `:4001`。 |
| 加入成功，小窗显示名字但没有视频 | UDP 4001 被屏蔽，或者服务器位于 NAT 之后却没有设置 `-public-ip`。 |
| `Room is full` | 房间人数已达到 `-max-participants`。 |
| `E2EE setting does not match the room` | 有人加入时的 E2EE 开关和房间创建者的不一致。 |
| `bind: address already in use` | 4001 端口被其他进程占用。停掉那个进程，或者用 `-port` 换个端口。 |
| `go run .` 因 Go 版本报错 | 你的 Go 版本低于 1.21，无法自动下载 1.25。请安装更新的 Go。 |

服务器会记录每一次加入、离开和发布的 track，日志以房间 ID 作为前缀。

## iOS 和 macOS {#ios-and-macos}

**Swift package 解析失败。** 在 Xcode 中依次执行 **File › Packages › Reset Package Caches** 和 **Resolve Package Versions**。WebRTC 来自本地 package [`ios/Packages/WebRTC`](gh:ios/Packages/WebRTC)，而不是直接来自 `webrtc-sdk/Specs`，因为 `150.7871.01` 的 Specs manifest 无法解析（"'v26' is unavailable"）。本地 package 下载的是同一个二进制文件，校验和也相同。

**iOS：通话中没有声音，但视频正常。** 其他人听不到 iPhone 的声音，iPhone 自己也不播放任何声音，Xcode 控制台显示 `Failed to set category and mode ... OSStatus error -50`。webrtc-sdk fork 配置音频会话的方式与上游 WebRTC 不同，所以应用会在每次通话前，在 `CallViewModel.configureCallAudio()` 中把它设置为 `playAndRecord` + `voiceChat`。升级 package 时要保留这段逻辑。如果 iOS 再次拒绝这些设置，通话界面会提示 "Call audio didn't start: iOS refused the audio settings"。

**macOS：屏幕共享没有画面。** 在"系统设置 › 隐私与安全性"中授予屏幕录制权限，然后重新启动应用。

## Android {#android}

**某个 Compose 或 Material 3 依赖要求更高的 compileSdk 或 AGP。** Material 3 Expressive 只存在于 `material3` 1.5 的 alpha 版本中。应用固定使用 `1.5.0-alpha18` 和 Compose BOM `2026.06.01`，这是能用 AGP 8.13 和 compileSdk 36 构建的最新版本。更新的版本需要 AGP 9.1 和 compileSdk 37，请先升级这两项。

## Windows {#windows}

**"rtc_shim.dll or libwebrtc.dll is missing"。** 针对你要构建的架构运行 `native/RtcShim/scripts/build-shim.ps1`（`-Arch x64` 或 `-Arch arm64`），然后重新构建应用。

**"…cannot be loaded because running scripts is disabled on this system"。** 先在当前 PowerShell 窗口中运行 `Set-ExecutionPolicy -Scope Process Bypass`，或者直接用 `powershell -ExecutionPolicy Bypass -File native/RtcShim/scripts/build-shim.ps1`。

**`NETSDK1233` 警告。** 说明解决方案是用 Visual Studio 2022 打开的，请改用 Visual Studio 2026。

**`rtc_shim ABI … does not match`。** DLL 比绑定代码旧，重新构建 shim 即可。

**没有摄像头或麦克风。** 打开"设置 → 隐私和安全性 → 相机 / 麦克风"，开启 *允许桌面应用访问…*。没有权限时通话仍能以只接收的方式进行，通话界面会提示缺少什么。

**摄像头在别处能用，这里却提示 "No camera found"。** Windows 同一时间只允许一个应用使用摄像头。关闭占用它的浏览器标签页或应用，然后按 `V`。

**自己的画面一直是黑的。** 检查"相机"应用能否显示画面，以及是否有其他程序在占用摄像头。虚拟摄像头（OBS 之类）在其应用没有运行时会输出黑屏，红外摄像头则根本没有画面，这时请在摄像头按钮旁的菜单中换一个。有些驱动在 720p 下不输出任何画面，比如 Boot Camp 下的 FaceTime HD 摄像头。打开这类摄像头时，应用会依次尝试更小的格式，可能要等几秒钟。

**别人听不到你的声音。** 用 `build-shim.ps1` 重新构建 shim。旧版本会把回声消除交给 Windows 的语音采集 DMO，而它在通话中采集不到声音。另外，在声音控制面板中打开麦克风的 **Levels**（级别）选项卡，检查输入音量和静音状态。

**通话中拔掉了麦克风或扬声器。** 大约 2 秒内，通话会切换到 Windows 默认的通信设备，并提示 "Switched to …"。

**"Couldn't load that effect"。** 图片、视频或模型无法打开，应用会恢复到之前的选择。视频背景只支持该电脑上 Media Foundation 能解码的格式。

更多 Windows 相关说明见 [`windows/README.md`](gh:windows/README.md#troubleshooting)。
