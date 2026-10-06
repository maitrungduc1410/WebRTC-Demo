---
layout: home

hero:
  name: WebRTC Demo
  text: 一次通话，五个原生应用
  tagline: 一个视频通话应用，覆盖 Web、Android、iOS、macOS 和 Windows。所有客户端都遵循同一套精简的协议，任意两端都能互相通话，端到端加密可开可关。先了解每个部分的原理，再动手实现你自己的客户端。
  image:
    src: /logo.png
    alt: WebRTC Demo
  actions:
    - theme: brand
      text: 快速开始
      link: /zh/guide/quick-start
    - theme: alt
      text: 这是什么？
      link: /zh/guide/
    - theme: alt
      text: 移植到新平台
      link: /zh/porting

features:
  - icon: 📞
    title: 1:1 通话，点对点直连
    details: 一个很小的 WebSocket 服务器负责让两端配对，之后音频、视频和聊天都在两端之间直接传输。
    link: /zh/how-it-works/signaling
    linkText: 逐步拆解一次通话
  - icon: 👥
    title: 通过自建 SFU 实现多人通话
    details: 可选的 Go 服务器基于 Pion 实现，负责转发所有人的视频。客户端依然只用标准 WebRTC API。
    link: /zh/how-it-works/group-calls
    linkText: Mesh 还是 SFU？
  - icon: 🔒
    title: 全平台端到端加密
    details: 浏览器加密帧的方式与原生 FrameCryptor 逐字节一致，所以 Chrome 标签页和 iPhone 之间也能加密通话。
    link: /zh/how-it-works/e2ee
    linkText: 亲手加密一帧
  - icon: 🖥️
    title: 屏幕共享与文件共享
    details: 在摄像头、屏幕和视频文件之间切换无需重新协商，通话和加密都不会中断。
    link: /zh/how-it-works/media-sources
    linkText: 切换视频源
  - icon: ✨
    title: 虚拟背景与人脸贴纸
    details: 模糊、图片、循环视频，以及跟随人脸移动的贴纸，素材来自同一个共享目录，所有平台都支持。
    link: /zh/how-it-works/effects
    linkText: 五套处理管线
  - icon: 🧩
    title: 为阅读而写
    details: 每个功能只讲一遍原理，再逐个平台展示实现，并直接链接到对应文件。
    link: /zh/platforms/
    linkText: 对比各平台
---

## 实际效果 {#see-it-in-action}

<DemoMedia src="/media/demo.mp4" kind="video">
20 到 30 秒，约 720p，无需声音：两台设备加入同一个房间，通话接通后，一方开启背景，发送一条聊天消息，然后把通话缩小成画中画。用两个不同的平台（比如 iPhone 和 Web）录制，可以展示它们之间能互相通话。
</DemoMedia>

## 选择平台 {#pick-a-platform}

每个应用实现的功能相同，只是用的是各自平台的工具。选一个看看具体怎么做。

<PlatformPicker />

## 五分钟跑起来 {#run-it-in-five-minutes}

```sh
cd signaling-server && npm install && npm run dev   # 会打印出客户端要用的地址
cd web && npm install && npm run dev                # 然后打开两个 http://localhost:5173 窗口
```

在两个窗口中输入同一个房间 ID 并加入，通话就建立了。原生应用的运行方法以及要填写的服务器地址，见[快速开始](/zh/guide/quick-start)。

## 这是 Demo，不是产品 {#a-demo-not-a-product}

这份代码用来展示各平台上常见的 WebRTC 用法，并提供一套能跑起来的代码作为起点。真实产品需要的一些东西，比如 TURN 服务器、断线重连和身份认证，都是有意省略的。完整列表见[版本与限制](/zh/reference/versions#limitations)。
