---
layout: home

hero:
  name: WebRTC Demo
  text: One call, five native apps
  tagline: A video call app built for Web, Android, iOS, macOS and Windows. Every client speaks the same small protocol, so any of them can call any other, with or without end-to-end encryption. Read how each part works, then build your own.
  image:
    src: /logo.png
    alt: WebRTC Demo
  actions:
    - theme: brand
      text: Quick start
      link: /guide/quick-start
    - theme: alt
      text: What is this?
      link: /guide/
    - theme: alt
      text: Port to a new platform
      link: /porting

features:
  - icon: 📞
    title: 1:1 calls, peer to peer
    details: A tiny WebSocket server pairs two peers. After that, audio, video and chat go straight between them.
    link: /how-it-works/signaling
    linkText: See a call step by step
  - icon: 👥
    title: Group calls through your own SFU
    details: An optional Go server built on Pion forwards everyone's video. The clients still use only standard WebRTC APIs.
    link: /how-it-works/group-calls
    linkText: Mesh or SFU?
  - icon: 🔒
    title: End-to-end encryption everywhere
    details: The browser encrypts frames byte for byte like the native FrameCryptor, so a Chrome tab and an iPhone can talk encrypted.
    link: /how-it-works/e2ee
    linkText: Encrypt a frame yourself
  - icon: 🖥️
    title: Screen and file sharing
    details: Switch between camera, screen and a video file without renegotiating, so the call and its encryption keep running.
    link: /how-it-works/media-sources
    linkText: Switching sources
  - icon: ✨
    title: Backgrounds and face stickers
    details: Blur, pictures, looping videos and stickers that follow your face, from one shared folder, on every platform.
    link: /how-it-works/effects
    linkText: Five pipelines
  - icon: 🧩
    title: Built to be read
    details: Each feature is explained once, then shown on every platform, with links to the exact files.
    link: /platforms/
    linkText: Compare platforms
---

## See it in action {#see-it-in-action}

<DemoMedia src="/media/demo.mp4" kind="video">
20 to 30 seconds, about 720p, no sound needed: two devices join the same room, the call connects, one side turns on a background, sends a chat message, then shrinks the call into picture-in-picture. Using two different platforms (for example iPhone and the web) shows that they call each other.
</DemoMedia>

## Pick a platform {#pick-a-platform}

Every app does the same things with its own platform's tools. Pick one to see how.

<PlatformPicker />

## Run it in five minutes {#run-it-in-five-minutes}

```sh
cd signaling-server && npm install && npm run dev   # prints the address to use
cd web && npm install && npm run dev                # then open http://localhost:5173 twice
```

Join the same room ID in both windows and you are in a call. The [quick start](/guide/quick-start) covers the native apps and the address to give them.

## A demo, not a product {#a-demo-not-a-product}

This code shows common WebRTC use cases across platforms and gives you working code to start from. Some things a real product needs, like a TURN server, reconnecting and authentication, are left out on purpose. [Versions and limitations](/reference/versions#limitations) lists them.
