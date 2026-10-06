---
title: "Gọi video WebRTC trên trình duyệt với Vue 3"
description: "Web client Vue 3 chỉ dùng API có sẵn của trình duyệt: getUserMedia, getDisplayMedia, RTCPeerConnection, Insertable Streams và Document Picture-in-Picture."
---

# Web

Một single-page app viết bằng Vue 3. App chạy trên trình duyệt của máy tính lẫn điện thoại, và chỉ dùng đúng API WebRTC có sẵn của trình duyệt.

<DemoMedia src="/media/web-call.png" :width="720">
Web client trên trình duyệt máy tính trong một cuộc gọi 1:1: video của người kia chiếm cả cửa sổ, tile của bạn nằm ở một góc, toolbar đang hiện và có tooltip trên một nút, panel chat bên cạnh đang mở.
</DemoMedia>

**Stack:** Vue 3, TypeScript, Vite, Tailwind CSS v4, shadcn-vue (reka-ui), Lucide, motion-v, MediaPipe Tasks Vision.

```sh
cd web
npm install
npm run dev   # http://localhost:5173
```

## Code nằm ở đâu {#where-things-are}

| File | Làm gì |
| --- | --- |
| [`call/useCall.ts`](gh:web/src/call/useCall.ts) | Engine 1:1: WebSocket signaling, `RTCPeerConnection`, chat |
| [`call/useGroupCall.ts`](gh:web/src/call/useGroupCall.ts) | Engine nhóm: WebSocket tới SFU, connection publish và subscribe, người đang nói |
| [`call/media.ts`](gh:web/src/call/media.ts) | Nguồn local (micro, camera, màn hình, file), effect, preview, quy tắc 300 ms. Dùng chung cho cả hai engine. |
| [`call/frameCrypto.ts`](gh:web/src/call/frameCrypto.ts) | Giữ key của frame và gắn các transform E2EE. Dùng chung cho cả hai engine. |
| [`e2ee.ts`](gh:web/src/e2ee.ts) | Định dạng frame tương thích với FrameCryptor |
| [`encryptionWorker.ts`](gh:web/src/encryptionWorker.ts) | Worker chạy các transform E2EE |
| [`effects/`](gh:web/src/effects) | Danh mục effect, đặt sticker, xử lý bằng MediaPipe |
| [`composables/usePictureInPicture.ts`](gh:web/src/composables/usePictureInPicture.ts) | Document PiP, dự phòng bằng video PiP |
| [`components/`](gh:web/src/components) | Lobby, màn hình cuộc gọi, toolbar, chat, view PiP |

Các engine là composable dạng singleton. Chúng nắm socket, các peer connection, media local và chat, rồi expose state ra dưới dạng ref. Component chỉ render state đó và gọi các action của nó.

## Cách web làm từng phần {#how-it-does-each-part}

- **Signaling.** Một `WebSocket` bình thường của trình duyệt tới `/ws`. Lobby gọi `GET /` mỗi 5 s để cập nhật chấm trạng thái, còn socket chỉ mở khi bạn vào phòng. [Signaling](/vi/how-it-works/signaling)
- **Chuyển nguồn.** Mỗi nguồn một track, và gọi `replaceTrack()` trên cùng một sender. [Chuyển nguồn video](/vi/how-it-works/media-sources)
- **E2EE.** `createEncodedStreams()` trên Chrome, `RTCRtpScriptTransform` trên Safari và Firefox, cả hai đều chạy trong worker để việc mã hóa không bao giờ chặn phần render. [Mã hóa đầu cuối](/vi/how-it-works/e2ee)
- **Effect.** MediaPipe và một canvas 2D, chỉ tải khi bạn bật effect lần đầu. [Phông nền và effect](/vi/how-it-works/effects#web)
- **Picture-in-picture.** Trên Chrome và Edge là cả một cửa sổ cuộc gọi thu nhỏ, từ Chrome 134 còn tự mở khi bạn chuyển tab. [Picture-in-picture](/vi/how-it-works/picture-in-picture#web)
- **Cúp máy.** Data channel bị đóng khi connection vẫn đang sống được tính là bên kia đã cúp máy. [Signaling](/vi/how-it-works/signaling#hanging-up)

## Ghi chú {#notes}

- Audio của bên kia phát từ một phần tử `<audio>` ẩn duy nhất, còn mọi phần tử `<video>` đều bị tắt tiếng, nên preview, khung chính và cửa sổ PiP không bao giờ phát tiếng hai lần. Trong cuộc gọi nhóm, mỗi người tham gia có một `<audio>` ẩn riêng.
- Tile local được điều khiển bằng motion value. Khi kéo, app dựa vào vận tốc lúc thả tay để chọn góc, rồi tile bật về góc đó theo hiệu ứng lò xo.
- Từ 1024 px trở lên, chat là panel bên cạnh, hẹp hơn thì là drawer ở dưới.
- Web client được phục vụ qua HTTPS thì không mở được socket `ws://`. Trong trường hợp đó, đặt một TLS proxy phía trước các server.
