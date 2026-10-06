---
layout: home

hero:
  name: WebRTC Demo
  text: Một cuộc gọi, năm app native
  tagline: App gọi video viết cho Web, Android, iOS, macOS và Windows. Mọi client nói chung một giao thức nhỏ, nên app nào cũng gọi được app nào, có hoặc không có mã hóa đầu cuối (E2EE). Đọc xem từng phần chạy thế nào, rồi tự làm bản của bạn.
  image:
    src: /logo.png
    alt: WebRTC Demo
  actions:
    - theme: brand
      text: Bắt đầu nhanh
      link: /vi/guide/quick-start
    - theme: alt
      text: Đây là gì?
      link: /vi/guide/
    - theme: alt
      text: Port sang nền tảng mới
      link: /vi/porting

features:
  - icon: 📞
    title: Gọi 1:1, peer to peer
    details: Một WebSocket server rất nhỏ ghép hai peer với nhau. Sau đó audio, video và chat đi thẳng giữa hai bên.
    link: /vi/how-it-works/signaling
    linkText: Xem từng bước của một cuộc gọi
  - icon: 👥
    title: Cuộc gọi nhóm qua SFU của riêng bạn
    details: Một server Go (không bắt buộc) viết trên Pion chuyển tiếp video của mọi người. Client vẫn chỉ dùng API WebRTC chuẩn.
    link: /vi/how-it-works/group-calls
    linkText: Mesh hay SFU?
  - icon: 🔒
    title: E2EE trên mọi nền tảng
    details: Trình duyệt mã hóa frame giống hệt từng byte với FrameCryptor native, nên một tab Chrome và một chiếc iPhone vẫn gọi mã hóa cho nhau được.
    link: /vi/how-it-works/e2ee
    linkText: Tự tay mã hóa một frame
  - icon: 🖥️
    title: Chia sẻ màn hình và file
    details: Chuyển qua lại giữa camera, màn hình và file video mà không cần renegotiate, nên cuộc gọi và phần mã hóa vẫn chạy bình thường.
    link: /vi/how-it-works/media-sources
    linkText: Chuyển nguồn video
  - icon: ✨
    title: Phông nền và sticker trên mặt
    details: Làm mờ, ảnh, video lặp và sticker bám theo khuôn mặt, lấy chung từ một thư mục, trên mọi nền tảng.
    link: /vi/how-it-works/effects
    linkText: Năm pipeline
  - icon: 🧩
    title: Viết ra để đọc
    details: Mỗi tính năng được giải thích một lần, rồi chỉ ra cách làm trên từng nền tảng, kèm link tới đúng file.
    link: /vi/platforms/
    linkText: So sánh các nền tảng
---

## Xem demo {#see-it-in-action}

<DemoMedia src="/media/demo.mp4" kind="video">
Dài 20 đến 30 giây, khoảng 720p, không cần tiếng: hai thiết bị vào cùng một phòng, cuộc gọi kết nối, một bên bật phông nền, gửi một tin nhắn chat, rồi thu cuộc gọi về picture-in-picture. Dùng hai nền tảng khác nhau (ví dụ iPhone và web) để thấy chúng gọi được cho nhau.
</DemoMedia>

## Chọn nền tảng {#pick-a-platform}

App nào cũng làm cùng những việc đó, mỗi app dùng công cụ của nền tảng mình. Chọn một nền tảng để xem cách làm.

<PlatformPicker />

## Chạy thử trong năm phút {#run-it-in-five-minutes}

```sh
cd signaling-server && npm install && npm run dev   # prints the address to use
cd web && npm install && npm run dev                # then open http://localhost:5173 twice
```

Nhập cùng một room ID ở cả hai cửa sổ là bạn đã ở trong cuộc gọi. Phần [bắt đầu nhanh](/vi/guide/quick-start) hướng dẫn chạy các app native và địa chỉ cần nhập cho chúng.

## Đây là demo, không phải sản phẩm {#a-demo-not-a-product}

Code này minh họa các trường hợp dùng WebRTC phổ biến trên nhiều nền tảng, và cho bạn code chạy được để làm điểm xuất phát. Một số thứ sản phẩm thật cần có, như TURN server, tự kết nối lại hay xác thực, được cố ý bỏ ra. Trang [Phiên bản và giới hạn](/vi/reference/versions#limitations) liệt kê đầy đủ.
