---
title: "Bắt đầu nhanh: chạy cuộc gọi video WebRTC trong vài phút"
description: "Chạy signaling server Node.js, mở web client trên hai cửa sổ trình duyệt và thực hiện cuộc gọi WebRTC đầu tiên. Sau đó thêm Android, iOS, macOS hoặc Windows."
---

# Bắt đầu nhanh

Một cuộc gọi 1:1 cần signaling server và hai client ở cùng một phòng. Cuộc gọi nhóm cần thêm SFU server, xem [Cuộc gọi nhóm](/vi/guide/group-calls).

## Yêu cầu {#requirements}

| Để chạy | Bạn cần |
| --- | --- |
| Signaling server, web client | Node.js 20.19 trở lên (Vite 7 yêu cầu) |
| Android | Android Studio với JDK 17+, một thiết bị chạy Android 7.0 (API 24) trở lên |
| iOS, macOS | Xcode 26. App iOS cần iPhone chạy iOS 26, app Mac cần macOS 26 (Liquid Glass chỉ có từ bản 26). Dependency lấy từ Swift Package Manager. |
| Windows | Windows 10 1809 trở lên (Mica cần Windows 11), Visual Studio 2026 có cài workload *WinUI application development* và *Desktop development with C++* |
| Cuộc gọi nhóm | Go 1.25 trở lên |

## 1. Chạy signaling server {#_1-start-the-signaling-server}

```sh
cd signaling-server
npm install
npm run dev
```

Server in ra địa chỉ mà các client cần dùng:

```
Signaling server listening on port 4000
Network access via: 192.168.1.10:4000
```

Server chỉ là một WebSocket relay đơn giản ở `ws://<address>/ws`. Đặt biến `PORT` nếu muốn dùng port khác.

## 2. Chạy hai client {#_2-start-two-clients}

Hai client nào cũng được. Không cần cùng nền tảng.

::: code-group

```sh [Web]
cd web
npm install
npm run dev
# mở http://localhost:5173 trong hai cửa sổ trình duyệt
```

```sh [Android]
# Mở android/ bằng Android Studio và chạy configuration "app",
# hoặc cài từ terminal:
cd android
./gradlew :app:installDebug
```

```sh [iOS]
open ios/WebRTCDemo.xcodeproj
# Chọn scheme WebRTCDemo và chạy trên iPhone thật.
# Camera và chia sẻ màn hình cần phần cứng thật.
```

```sh [macOS]
open ios/WebRTCDemo.xcodeproj
# Chọn scheme WebRTCDemoMac và destination "My Mac".
```

```powershell [Windows]
cd windows
# Tải libwebrtc (có kiểm tra SHA-256) và build rtc_shim.dll
./native/RtcShim/scripts/build-shim.ps1
dotnet run --project src/WebRtcDemo.App -p:Platform=x64
```

:::

Trên iOS và macOS, lần đầu bạn mở project, Xcode sẽ tự resolve Swift package WebRTC. Không dùng CocoaPods, cũng không có workspace. Trên Windows bạn cũng có thể mở `windows/WebRtcDemo.slnx` bằng Visual Studio và chạy configuration x64 hoặc ARM64.

## 3. Trỏ client tới server {#_3-point-the-clients-at-the-server}

Đặt địa chỉ một lần là client nhớ luôn.

| Client | Đổi ở đâu | Mặc định |
| --- | --- | --- |
| Web | Bấm vào địa chỉ server bên dưới **Join room** | Port 4000 trên host đang phục vụ trang web |
| Android | Chạm vào địa chỉ server ở cuối màn hình lobby | `serverAddress` trong [`strings.xml`](gh:android/app/src/main/res/values/strings.xml) |
| iOS | Chạm vào địa chỉ server ở cuối màn hình lobby | `SignalingServer.defaultURL` trong [`SignalingServer.swift`](gh:ios/WebRTCDemo/SignalingServer.swift) |
| macOS | **WebRTC Demo › Settings…** (⌘,) | `http://localhost:4000` |
| Windows | **Signaling server** trong lobby | `http://localhost:4000` |

Điện thoại không truy cập được `localhost` trên máy tính của bạn. Hãy nhập địa chỉ LAN mà server đã in ra, và để cả hai thiết bị chung một mạng.

## 4. Vào cùng một phòng {#_4-join-the-same-room}

Nhập cùng một room ID trên cả hai client rồi join. Client vào trước sẽ chờ, client vào sau sẽ bắt đầu cuộc gọi.

Muốn thử mã hóa đầu cuối (E2EE), bật E2EE trong lobby ở **cả hai** bên trước khi join. Nếu chỉ một bên bật, bên kia sẽ thấy video đen và không nghe được gì.

::: tip Không tự kết nối lại
Client giữ socket signaling mở suốt cuộc gọi. Nếu socket bị rớt (server bị tắt, điện thoại chuyển từ Wi-Fi sang 4G), cuộc gọi sẽ kết thúc với thông báo "Lost the connection to the signaling server", kể cả khi audio và video vẫn đang chạy. Bạn chỉ cần vào lại phòng.
:::

## Tiếp theo {#next}

- [Dùng app](/vi/guide/using-the-app): các nút điều khiển, cử chỉ và phím tắt.
- [Cuộc gọi nhóm](/vi/guide/group-calls): gọi với nhiều hơn hai người.
- [Cách hoạt động](/vi/how-it-works/): bên dưới thực sự xảy ra những gì.
