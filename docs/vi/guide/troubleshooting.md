---
title: "Xử lý sự cố WebRTC: kết nối, âm thanh và build"
description: "Cách xử lý các lỗi hay gặp: không kết nối được server, iOS không có tiếng, Swift package không resolve được, thiếu DLL trên Windows và lỗi khi gọi nhóm."
---

# Xử lý sự cố

## Mọi nền tảng {#every-platform}

**"Can't reach the server"** (không kết nối được server). Thiết bị phải truy cập được server: cùng mạng, port 4000 (signaling) hoặc 4001 (SFU) được mở trên firewall của máy chạy server. Nếu server chạy trên máy khác, dùng IP LAN của máy đó chứ không dùng `localhost`. Lệnh `curl http://<address>:4000/` phải trả về `{"name":"signaling-server","ok":true}`.

**"Lost the connection to the signaling server"** (mất kết nối tới signaling server). Socket signaling bị đóng giữa cuộc gọi. App được thiết kế để không tự kết nối lại. Bạn vào lại phòng là được.

**"Room is full"** (phòng đã đầy). Phòng 1:1 chỉ chứa hai người. Dùng room ID khác, hoặc chuyển sang [cuộc gọi nhóm](/vi/guide/group-calls).

**Bật E2EE thì video đen và không có tiếng.** Cả hai bên phải bật E2EE. Khi key không khớp, app không báo lỗi mà chỉ không có video, vì frame nào không giải mã được đều bị bỏ.

**Ở một số mạng cuộc gọi không bao giờ kết nối được.** Không có TURN server, nên cuộc gọi có thể thất bại khi thiết bị nằm sau symmetric NAT hoặc firewall chặt. Trước tiên hãy thử cho cả hai thiết bị dùng chung một mạng Wi-Fi.

## Cuộc gọi nhóm {#group-calls}

| Hiện tượng | Nguyên nhân thường gặp |
| --- | --- |
| Lobby không kết nối được server cuộc gọi nhóm | Sai địa chỉ, server chưa chạy, hoặc TCP 4001 bị chặn. Thử `curl http://<address>:4001/` từ cùng mạng. Trên web, kiểm tra địa chỉ đã có `:4001` chưa. |
| Đã vào phòng, tile có tên nhưng không có video | UDP 4001 bị chặn, hoặc server nằm sau NAT mà không đặt `-public-ip`. |
| `Room is full` | Phòng đã đủ `-max-participants` người. |
| `E2EE setting does not match the room` | Có người vào phòng với thiết lập E2EE khác với người tạo phòng. |
| `bind: address already in use` | Một process khác đang dùng port 4001. Tắt process đó hoặc dùng `-port`. |
| `go run .` báo lỗi phiên bản Go | Bản Go của bạn cũ hơn 1.21 nên không tự tải được 1.25. Cài bản Go mới hơn. |

Server log mọi lần join, leave và mọi track được publish, kèm room ID ở đầu dòng.

## iOS và macOS {#ios-and-macos}

**Swift package không resolve được.** Trong Xcode chọn **File › Packages › Reset Package Caches**, rồi **Resolve Package Versions**. WebRTC lấy từ local package [`ios/Packages/WebRTC`](gh:ios/Packages/WebRTC), không lấy thẳng từ `webrtc-sdk/Specs`: manifest của Specs cho bản `150.7871.01` không resolve được ("'v26' is unavailable"). Local package tải đúng file binary đó với cùng checksum.

**iOS: video chạy nhưng cuộc gọi không có tiếng.** Người khác không nghe được iPhone, iPhone cũng không phát ra tiếng gì, và console của Xcode hiện `Failed to set category and mode ... OSStatus error -50`. Bản fork webrtc-sdk không cấu hình audio session theo cách WebRTC upstream làm, nên app tự đặt nó thành `playAndRecord` + `voiceChat` trước mỗi cuộc gọi trong `CallViewModel.configureCallAudio()`. Khi nâng cấp package, nhớ giữ đoạn này. Nếu iOS lại từ chối thiết lập, cuộc gọi sẽ hiện "Call audio didn't start: iOS refused the audio settings".

**macOS: chia sẻ màn hình không hiện gì.** Cấp quyền Screen Recording trong System Settings › Privacy & Security, rồi mở lại app.

## Android {#android}

**Một dependency Compose hoặc Material 3 đòi compileSdk hoặc AGP mới hơn.** Material 3 Expressive chỉ có trong các bản alpha `material3` 1.5. App ghim `1.5.0-alpha18` với Compose BOM `2026.06.01`, là các bản mới nhất còn build được với AGP 8.13 và compileSdk 36. Bản mới hơn cần AGP 9.1 và compileSdk 37, nên hãy nâng hai thứ đó trước.

## Windows {#windows}

**"rtc_shim.dll or libwebrtc.dll is missing"** (thiếu DLL). Chạy `native/RtcShim/scripts/build-shim.ps1` cho đúng kiến trúc bạn build (`-Arch x64` hoặc `-Arch arm64`), rồi build lại app.

**"…cannot be loaded because running scripts is disabled on this system"** (PowerShell chặn chạy script). Chạy `Set-ExecutionPolicy -Scope Process Bypass` trong cửa sổ PowerShell đó trước, hoặc dùng `powershell -ExecutionPolicy Bypass -File native/RtcShim/scripts/build-shim.ps1`.

**Cảnh báo `NETSDK1233`.** Solution đang được mở bằng Visual Studio 2022. Hãy dùng Visual Studio 2026.

**`rtc_shim ABI … does not match`.** DLL cũ hơn phần binding. Build lại shim.

**Không có camera hoặc micro.** Vào Settings → Privacy & security → Camera / Microphone → bật *Let desktop apps access…*. Cuộc gọi vẫn chạy ở chế độ chỉ nhận, và màn hình cuộc gọi sẽ ghi rõ đang thiếu gì.

**"No camera found" trong khi camera vẫn dùng được ở chỗ khác.** Windows chỉ cho một app dùng camera tại một thời điểm. Đóng tab trình duyệt hoặc app đang giữ camera, rồi bấm `V`.

**Video của chính bạn cứ đen.** Kiểm tra app Camera có hiện hình không và không có app nào khác đang dùng camera. Camera ảo (OBS và các app tương tự) gửi hình đen khi app của nó không chạy, còn camera hồng ngoại thì không có hình, nên hãy chọn camera khác trong menu cạnh nút camera. Một số driver không gửi gì ở 720p, ví dụ camera FaceTime HD khi chạy Boot Camp, nên bật những camera như vậy có thể mất vài giây vì app phải thử dần các định dạng nhỏ hơn.

**Người khác không nghe được bạn.** Build lại shim bằng `build-shim.ps1`. Bản shim cũ giao phần khử tiếng vọng cho voice-capture DMO của Windows, mà DMO này không thu âm trong cuộc gọi. Kiểm tra thêm mức âm lượng đầu vào và trạng thái mute ở tab **Levels** của micro trong Sound control panel.

**Rút micro hoặc loa ra giữa cuộc gọi.** Trong khoảng 2 giây, cuộc gọi chuyển sang thiết bị liên lạc mặc định của Windows và hiện "Switched to …".

**"Couldn't load that effect"** (không tải được effect). Không mở được ảnh, video hoặc model, nên app quay về lựa chọn trước đó. Phông nền video chỉ chạy với các định dạng mà Media Foundation trên máy đó decode được.

Ghi chú thêm cho Windows có trong [`windows/README.md`](gh:windows/README.md#troubleshooting).
