# Web

一个 Vue 3 单页应用，可以在桌面和手机浏览器中运行，只使用浏览器自带的 WebRTC API。

<DemoMedia src="/media/web-call.png" :width="720">
桌面浏览器中的 Web 客户端，正在进行 1:1 通话：对方的视频铺满窗口，你自己的小窗位于一角，工具栏可见且其中一个按钮显示着提示，聊天侧边栏处于打开状态。
</DemoMedia>

**技术栈：** Vue 3、TypeScript、Vite、Tailwind CSS v4、shadcn-vue（reka-ui）、Lucide、motion-v、MediaPipe Tasks Vision。

```sh
cd web
npm install
npm run dev   # http://localhost:5173
```

## 代码结构 {#where-things-are}

| 文件 | 作用 |
| --- | --- |
| [`call/useCall.ts`](gh:web/src/call/useCall.ts) | 1:1 通话引擎：信令 WebSocket、`RTCPeerConnection`、聊天 |
| [`call/useGroupCall.ts`](gh:web/src/call/useGroupCall.ts) | 多人通话引擎：SFU WebSocket、publish 和 subscribe 连接、当前说话人 |
| [`call/media.ts`](gh:web/src/call/media.ts) | 本地媒体源（麦克风、摄像头、屏幕、文件）、特效、预览、300 ms 规则。两个引擎共用。 |
| [`call/frameCrypto.ts`](gh:web/src/call/frameCrypto.ts) | 保存帧密钥并挂载 E2EE transform。两个引擎共用。 |
| [`e2ee.ts`](gh:web/src/e2ee.ts) | 与 FrameCryptor 兼容的帧格式 |
| [`encryptionWorker.ts`](gh:web/src/encryptionWorker.ts) | 运行 E2EE transform 的 worker |
| [`effects/`](gh:web/src/effects) | 特效目录、贴纸定位、MediaPipe 处理 |
| [`composables/usePictureInPicture.ts`](gh:web/src/composables/usePictureInPicture.ts) | Document PiP，不支持时回退到视频 PiP |
| [`components/`](gh:web/src/components) | 大厅、通话界面、工具栏、聊天、画中画视图 |

两个引擎都是单例 composable。它们持有 socket、peer connection、本地媒体和聊天，并以 ref 的形式暴露状态。组件只负责渲染这些状态并调用引擎提供的操作。

## 各部分的实现 {#how-it-does-each-part}

- **信令。** 用浏览器原生的 `WebSocket` 连接 `/ws`。大厅每 5 s 轮询一次 `GET /` 来更新状态指示点，socket 只在加入时才打开。[信令](/zh/how-it-works/signaling)
- **切换视频源。** 每个来源一个 track，在同一个 sender 上调用 `replaceTrack()`。[切换视频源](/zh/how-it-works/media-sources)
- **E2EE。** Chrome 上用 `createEncodedStreams()`，Safari 和 Firefox 上用 `RTCRtpScriptTransform`，都运行在 worker 中，所以加密永远不会阻塞渲染。[端到端加密](/zh/how-it-works/e2ee)
- **特效。** MediaPipe 加 2D canvas，第一次开启特效时才加载。[背景与特效](/zh/how-it-works/effects#web)
- **画中画。** 在 Chrome 和 Edge 中是一个完整的迷你通话窗口，从 Chrome 134 开始，切换标签页时会自动打开。[画中画](/zh/how-it-works/picture-in-picture#web)
- **挂断。** 连接仍然有效时 data channel 关闭，就视为对方已挂断。[信令](/zh/how-it-works/signaling#hanging-up)

## 备注 {#notes}

- 远端音频由一个隐藏的 `<audio>` 元素播放，所有 `<video>` 元素都设为静音，所以预览、主画面和画中画窗口永远不会重复播放声音。在多人通话中，每个参与者都有自己的隐藏 `<audio>`。
- 本地小窗由 motion value 驱动。拖动松手时，会根据松手速度推算落点来选择角落，再以弹簧动画吸附过去。
- 宽度在 1024 px 及以上时，聊天显示为侧边栏，更窄时显示为底部抽屉。
- 通过 HTTPS 提供的 Web 客户端无法打开 `ws://` socket，这种情况下需要在服务器前面加一层 TLS 代理。
