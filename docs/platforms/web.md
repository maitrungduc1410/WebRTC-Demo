# Web

A Vue 3 single-page app. It runs in desktop and phone browsers and uses nothing but the browser's own WebRTC API.

<DemoMedia src="/media/web-call.png" :width="720">
The web client in a desktop browser during a 1:1 call: the other person's video fills the window, your own tile sits in a corner, the toolbar is visible with its tooltip on one button, and the chat side panel is open.
</DemoMedia>

**Stack:** Vue 3, TypeScript, Vite, Tailwind CSS v4, shadcn-vue (reka-ui), Lucide, motion-v, MediaPipe Tasks Vision.

```sh
cd web
npm install
npm run dev   # http://localhost:5173
```

## Where things are {#where-things-are}

| File | What it does |
| --- | --- |
| [`call/useCall.ts`](gh:web/src/call/useCall.ts) | The 1:1 engine: signaling WebSocket, `RTCPeerConnection`, chat |
| [`call/useGroupCall.ts`](gh:web/src/call/useGroupCall.ts) | The group engine: SFU WebSocket, publish and subscribe connections, active speaker |
| [`call/media.ts`](gh:web/src/call/media.ts) | Local sources (mic, camera, screen, file), effects, preview, the 300 ms rule. Shared by both engines. |
| [`call/frameCrypto.ts`](gh:web/src/call/frameCrypto.ts) | Holds the frame key and attaches the E2EE transforms. Shared by both engines. |
| [`e2ee.ts`](gh:web/src/e2ee.ts) | The FrameCryptor-compatible frame format |
| [`encryptionWorker.ts`](gh:web/src/encryptionWorker.ts) | The worker that runs the E2EE transforms |
| [`effects/`](gh:web/src/effects) | Effects catalog, sticker placement, MediaPipe processing |
| [`composables/usePictureInPicture.ts`](gh:web/src/composables/usePictureInPicture.ts) | Document PiP with video PiP fallback |
| [`components/`](gh:web/src/components) | Lobby, call screen, toolbar, chat, PiP view |

The engines are singleton composables. They own the socket, the peer connections, the local media and the chat, and expose state as refs. The components only render that state and call its actions.

## How it does each part {#how-it-does-each-part}

- **Signaling.** A plain browser `WebSocket` to `/ws`. The lobby polls `GET /` every 5 s for its status dot, and the socket only opens when you join. [Signaling](/how-it-works/signaling)
- **Switching sources.** One track per source, and `replaceTrack()` on the same sender. [Switching video sources](/how-it-works/media-sources)
- **E2EE.** `createEncodedStreams()` on Chrome, `RTCRtpScriptTransform` on Safari and Firefox, both in a worker so encryption never blocks rendering. [End-to-end encryption](/how-it-works/e2ee)
- **Effects.** MediaPipe and a 2D canvas, loaded only when an effect is first turned on. [Backgrounds and effects](/how-it-works/effects#web)
- **Picture-in-picture.** A full mini call window in Chrome and Edge, which opens by itself on tab switch since Chrome 134. [Picture-in-picture](/how-it-works/picture-in-picture#web)
- **Hang-up.** The data channel closing on a live connection counts as the other side hanging up. [Signaling](/how-it-works/signaling#hanging-up)

## Notes {#notes}

- The remote audio plays from one hidden `<audio>` element and every `<video>` element is muted, so the preview, the stage and the PiP window never play it twice. In a group call each participant has their own hidden `<audio>`.
- The local tile is driven by motion values. Dragging projects the release velocity to choose a corner, and the tile snaps there with a spring.
- Chat is a side panel at 1024 px and wider, a bottom drawer below.
- A web client served over HTTPS can't open `ws://` sockets. Put a TLS proxy in front of the servers in that case.
