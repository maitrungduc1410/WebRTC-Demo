---
title: "WebRTC trên Web, Android, iOS, macOS và Windows"
description: "Các app Web, Android, iOS, macOS và Windows được viết thế nào, mỗi app dùng WebRTC SDK nào, và nên xem chỗ nào trong code của từng app."
---

# Nền tảng

Năm app, mỗi app viết bằng công cụ của nền tảng mình. Giữa các ngôn ngữ, chúng không dùng chung dòng code nào, chỉ chung [bản "hợp đồng"](/vi/guide/#what-keeps-them-working-together), thư mục [`effects`](gh:effects) và icon app. Ngoại lệ là iOS và macOS: app Mac compile phần lớn code của iOS.

<PlatformPicker />

## Nên đọc app nào trước {#which-one-to-read-first}

Bắt đầu với **web client**, kể cả khi bạn nhắm tới nền tảng khác. API WebRTC của trình duyệt là chuẩn mà các SDK native làm theo, code ngắn, và [`useCall.ts`](gh:web/src/call/useCall.ts) chứa trọn cuộc gọi 1:1 trong một file. Sau đó đọc app gần với nền tảng của bạn nhất:

| Bạn đang làm cho | Đọc |
| --- | --- |
| Flutter, React Native, hoặc bất cứ thứ gì chạy trên webrtc-sdk | [Android](/vi/platforms/android) và [iOS](/vi/platforms/ios): cùng họ SDK |
| App desktop dùng thẳng libwebrtc | [Windows](/vi/platforms/windows): C shim cho thấy toàn bộ phần API native mà một cuộc gọi cần |
| Một app khác chạy trên trình duyệt | [Web](/vi/platforms/web) |
| Các nền tảng của Apple | [iOS](/vi/platforms/ios) và [macOS](/vi/platforms/macos) |

## Mỗi app lưu thiết lập ở đâu {#where-each-app-keeps-its-settings}

| App | Nơi lưu | Lưu những gì |
| --- | --- | --- |
| Web | `localStorage` | Địa chỉ server, effect đã chọn |
| Android | `SharedPreferences` | Địa chỉ server, effect đã chọn |
| iOS, macOS | `UserDefaults` | Địa chỉ server, effect đã chọn, thiết bị (trên Mac) |
| Windows | `%LOCALAPPDATA%\WebRtcDemo\settings.json` | Địa chỉ server, công tắc E2EE, thiết bị, effect đã chọn |

## Library {#libraries}

| App | WebRTC |
| --- | --- |
| Web | Của trình duyệt |
| Android | [`io.github.webrtc-sdk:android`](https://github.com/webrtc-sdk) `150.7871.01` |
| iOS, macOS | [`webrtc-sdk/Specs`](https://github.com/webrtc-sdk/Specs) `150.7871.01`, qua local Swift package [`ios/Packages/WebRTC`](gh:ios/Packages/WebRTC) |
| Windows | [`webrtc-sdk/libwebrtc`](https://github.com/webrtc-sdk/libwebrtc) `m150.7871.03`, qua một C shim |

Cả bốn bản build native đều lấy từ cùng nhánh m150 của bản fork webrtc-sdk, nhờ vậy `FrameCryptor` của chúng giống hệt nhau. Phiên bản đầy đủ có trong [Phiên bản và giới hạn](/vi/reference/versions).
