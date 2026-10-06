---
title: "WebRTC Demo là gì? App gọi video mã nguồn mở"
description: "App gọi video WebRTC mã nguồn mở được viết năm lần, cho trình duyệt, Android, iOS, macOS và Windows. App nào cũng gọi được cho nhau. Chạy thử và đọc code."
---

# Đây là gì?

WebRTC Demo là một app gọi video được viết năm lần: trên trình duyệt, Android, iOS, macOS và Windows. Mỗi bản là một app native dùng công cụ riêng của nền tảng đó, và tất cả cùng nói chuyện với vài server nhỏ giống nhau. Client nào cũng gọi được cho client nào.

Đây không phải là library. Bạn không cần cài gì vào project của mình. Nó là một bộ app chạy được, để bạn chạy thử, đọc và chép code.

## Dành cho ai {#who-it-is-for}

- Bạn muốn có tính năng gọi trong app của mình và cần xem các phần ghép với nhau thế nào trên nền tảng bạn dùng.
- Bạn đang đưa WebRTC lên một nền tảng chưa có ở đây (Flutter, React Native, Linux, TV) và cần biết chính xác phải gửi gì để các app hiện có gọi được cho app của bạn.
- Bạn muốn xem những thứ như E2EE giữa trình duyệt và điện thoại, chia sẻ màn hình trên iOS khi app chạy nền, hay một SFU nhỏ thực sự chạy thế nào.

## Bạn có gì {#what-you-get}

| Tính năng | Web | iOS | Android | macOS | Windows |
| --- | :-: | :-: | :-: | :-: | :-: |
| Gọi video 1:1, peer to peer | ✅ | ✅ | ✅ | ✅ | ✅ |
| Cuộc gọi nhóm qua SFU của riêng bạn (không bắt buộc) | ✅ | ✅ | ✅ | ✅ | ✅ |
| Chat qua data channel | ✅ | ✅ | ✅ | ✅ | ✅ |
| Chia sẻ màn hình hoặc file video | ✅ | ✅ | ✅ | ✅ | ✅ |
| Virtual background: làm mờ, ảnh và video | ✅ | ✅ | ✅ | ✅ | ✅ |
| Sticker bám theo khuôn mặt | ✅ | ✅ | ✅ | ✅ | ✅ |
| Mã hóa đầu cuối (E2EE), 1:1 và nhóm | ✅ | ✅ | ✅ | ✅ | ✅ |
| Mức âm lượng micro của chính bạn hiện trên video của bạn | ✅ | ✅ | ✅ | ✅ | ✅ |
| Picture-in-picture | ✅ | ✅¹ | ✅ | ✅² | ✅² |

¹ Trên iOS, picture-in-picture chỉ chạy trong cuộc gọi 1:1.
² Trên Mac là một cửa sổ nổi luôn nằm trên cùng, trên Windows là cửa sổ compact overlay.

## Trong repository có gì {#what-is-in-the-repository}

| Thư mục | Là gì |
| --- | --- |
| [`signaling-server/`](gh:signaling-server) | WebSocket relay viết bằng Node.js cho cuộc gọi 1:1. Khoảng 150 dòng. |
| [`sfu-server/`](gh:sfu-server) | Server Go cho cuộc gọi nhóm, viết trên Pion. Không bắt buộc. |
| [`web/`](gh:web) | Client Vue 3. |
| [`android/`](gh:android) | Kotlin và Jetpack Compose. |
| [`ios/`](gh:ios) | App iOS và macOS, chung một project Xcode. |
| [`windows/`](gh:windows) | WinUI 3 trên .NET, kèm một C shim nhỏ bọc libwebrtc. |
| [`effects/`](gh:effects) | Phông nền và sticker mà app nào cũng đóng gói theo. |
| [`tools/`](gh:tools) | Script chuẩn bị phông nền và vẽ icon app. |

## Điều gì giúp các app gọi được cho nhau {#what-keeps-them-working-together}

Năm app không dùng chung dòng code nào giữa các ngôn ngữ. Thứ giúp chúng gọi được cho nhau là một bản "hợp đồng" nhỏ mà client nào cũng tuân theo:

- các message JSON trên WebSocket, và thứ tự của chúng,
- ai tạo offer (luôn là peer vào phòng trước),
- định dạng frame E2EE và cách tạo ra key,
- message `media state` và quy tắc 300 ms của camera,
- với cuộc gọi nhóm, giao thức của SFU với hai peer connection cho mỗi client.

Làm đúng theo đó là client của bạn vào được cuộc gọi với bất kỳ app nào trong năm app. Trang [Port sang nền tảng mới](/vi/porting) biến nó thành một checklist.

## Cách đọc tài liệu này {#how-to-read-these-docs}

- [Bắt đầu nhanh](/vi/guide/quick-start): đưa hai client vào một cuộc gọi.
- [Cách hoạt động](/vi/how-it-works/): mỗi tính năng một trang. Mỗi trang giải thích ý tưởng một lần, rồi chỉ ra cách từng nền tảng làm.
- [Nền tảng](/vi/platforms/): chỉ chỗ từng thứ nằm trong code của mỗi app.
- [Port sang nền tảng mới](/vi/porting): checklist cho một client mới.
- [Tham khảo](/vi/reference/messages): mọi message, các phiên bản đang dùng và các giới hạn đã biết.

Toàn bộ nội dung này cũng có trong một file dài, đọc offline được, là [`ARCHITECTURE.md`](gh:ARCHITECTURE.md).

## Đây là demo, không phải sản phẩm {#a-demo-not-a-product}

Code này nhằm minh họa các trường hợp dùng WebRTC phổ biến và gợi ý cho bạn. Nó có thể còn bug. Một số thứ app production cần có được cố ý bỏ ra: không có TURN server, không tự kết nối lại, không có xác thực, và key E2EE đi qua server ở dạng chưa mã hóa. Xem [các giới hạn](/vi/reference/versions#limitations).
