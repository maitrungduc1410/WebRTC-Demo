---
title: "WebRTC Demo 是什么？开源视频通话应用"
description: "一个开源的 WebRTC 视频通话应用，为浏览器、Android、iOS、macOS 和 Windows 各原生实现了一遍，五个应用之间都能互相通话。"
---

# 这是什么？

WebRTC Demo 是同一个视频通话应用的五个实现：浏览器、Android、iOS、macOS 和 Windows。每一个都是用该平台自身工具编写的原生应用，它们连接同一组小型服务器，任意一个客户端都能呼叫其他任意客户端。

它不是一个库，没有什么需要装进你的项目。它是一组可以运行、阅读和直接借鉴的完整应用。

## 适合谁 {#who-it-is-for}

- 你想在自己的应用里加入通话功能，需要了解在你的平台上各个部分是怎么配合的。
- 你要把 WebRTC 带到一个这里还没有的平台（Flutter、React Native、Linux、电视），需要确切知道该发送什么，现有应用才能和你的应用通话。
- 你想看看浏览器和手机之间的 E2EE、iOS 后台屏幕共享，或者一个小型 SFU 实际是怎么实现的。

## 包含哪些功能 {#what-you-get}

| 功能 | Web | iOS | Android | macOS | Windows |
| --- | :-: | :-: | :-: | :-: | :-: |
| 1:1 视频通话，点对点 | ✅ | ✅ | ✅ | ✅ | ✅ |
| 通过自建 SFU 进行多人通话（可选） | ✅ | ✅ | ✅ | ✅ | ✅ |
| 基于 data channel 的聊天 | ✅ | ✅ | ✅ | ✅ | ✅ |
| 共享屏幕或视频文件 | ✅ | ✅ | ✅ | ✅ | ✅ |
| 虚拟背景：模糊、图片和视频 | ✅ | ✅ | ✅ | ✅ | ✅ |
| 跟随人脸的贴纸 | ✅ | ✅ | ✅ | ✅ | ✅ |
| 端到端加密，支持 1:1 和多人通话 | ✅ | ✅ | ✅ | ✅ | ✅ |
| 在自己的画面上显示麦克风音量 | ✅ | ✅ | ✅ | ✅ | ✅ |
| 画中画 | ✅ | ✅¹ | ✅ | ✅² | ✅² |

¹ iOS 上的画中画仅支持 1:1 通话。
² Mac 上是始终置顶的悬浮窗口，Windows 上是紧凑覆盖窗口（compact overlay）。

## 仓库结构 {#what-is-in-the-repository}

| 目录 | 内容 |
| --- | --- |
| [`signaling-server/`](gh:signaling-server) | 用于 1:1 通话的 Node.js WebSocket 中继，约 150 行。 |
| [`sfu-server/`](gh:sfu-server) | 可选的多人通话服务器，Go 编写，基于 Pion。 |
| [`web/`](gh:web) | Vue 3 客户端。 |
| [`android/`](gh:android) | Kotlin 和 Jetpack Compose。 |
| [`ios/`](gh:ios) | iOS 和 macOS 应用，同一个 Xcode 工程。 |
| [`windows/`](gh:windows) | 基于 .NET 的 WinUI 3，通过一层很薄的 C shim 调用 libwebrtc。 |
| [`effects/`](gh:effects) | 所有应用共同打包的背景和贴纸。 |
| [`tools/`](gh:tools) | 准备背景素材和绘制应用图标的脚本。 |

## 它们为什么能互通 {#what-keeps-them-working-together}

五个应用使用不同的语言，彼此不共享代码。让它们能互相通话的，是每个客户端都遵守的一份简短约定：

- WebSocket 上的 JSON 消息及其顺序；
- 由谁发起 offer（总是先进入房间的那一端）；
- E2EE 的帧格式和密钥派生方式；
- `media state` 消息和摄像头的 300 ms 规则；
- 多人通话时，SFU 的协议：每个客户端两个 peer connection。

只要遵守这份约定，你的客户端就能和这五个应用中的任意一个通话。[移植到新平台](/zh/porting)把它整理成了一份清单。

## 如何阅读本文档 {#how-to-read-these-docs}

- [快速开始](/zh/guide/quick-start)：让两个客户端连上通话。
- [工作原理](/zh/how-it-works/)：每个功能一页，先讲一遍原理，再展示各平台的实现。
- [平台](/zh/platforms/)：各应用代码中相关部分所在的位置。
- [移植到新平台](/zh/porting)：开发新客户端的检查清单。
- [参考](/zh/reference/messages)：所有消息、使用的版本和已知限制。

同样的内容也整理成了一个可离线阅读的长文件：[`ARCHITECTURE.md`](gh:ARCHITECTURE.md)。

## 这是 Demo，不是产品 {#a-demo-not-a-product}

这份代码的目的是展示常见的 WebRTC 用法，给你一些思路，其中可能有 bug。生产环境应用需要的一些东西被有意省略了：没有 TURN 服务器，没有断线重连，没有身份认证，E2EE 密钥以明文形式经过服务器。详见[限制](/zh/reference/versions#limitations)。
