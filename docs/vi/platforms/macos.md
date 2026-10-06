# macOS

App SwiftUI native, dùng thêm AppKit ở những chỗ SwiftUI không có API. Đây không phải Catalyst. App nằm chung project Xcode với app iOS và compile phần lớn code của nó, nên signaling, E2EE, effect và cuộc gọi nhóm chạy y hệt trên [iOS](/vi/platforms/ios). Cần macOS 26 trở lên.

<DemoMedia src="/media/share-macos.png" :width="720">
App Mac đang mở hộp chọn chia sẻ màn hình, hiện thumbnail trực tiếp của các màn hình và cửa sổ có thể chia sẻ.
</DemoMedia>

Mở [`ios/WebRTCDemo.xcodeproj`](gh:ios/WebRTCDemo.xcodeproj), chọn scheme `WebRTCDemoMac` và destination **My Mac**. Địa chỉ server nằm trong **WebRTC Demo › Settings…** (⌘,), mặc định là `localhost`, nên server chạy trên cùng máy Mac dùng được ngay mà không cần sửa gì. Cuộc gọi đầu tiên sẽ xin quyền dùng camera và micro, lần chia sẻ màn hình đầu tiên sẽ xin quyền Screen Recording.

## Dùng chung với iOS {#shared-with-ios}

`CallViewModel`, `LocalMedia`, `WebRTCClient`, `GroupCallClient`, `SignalingSocket`, `FrameEncryption`, `EffectsProcessor` và các view dùng chung được compile vào cả hai app. Code riêng của từng nền tảng được tách ra bằng `#if os(macOS)` / `#if os(iOS)`.

## Chỉ có trên Mac {#mac-only}

Các file trong [`ios/WebRTCDemoMac`](gh:ios/WebRTCDemoMac):

| File | Làm gì |
| --- | --- |
| [`WebRTCDemoMacApp.swift`](gh:ios/WebRTCDemoMac/WebRTCDemoMacApp.swift) | Một cửa sổ, scene Settings, menu Call |
| [`MacCallView.swift`](gh:ios/WebRTCDemoMac/MacCallView.swift) | Cửa sổ cuộc gọi: khung chính, view tự xem hình kéo được, nút điều khiển tự ẩn |
| [`ScreenSharePicker.swift`](gh:ios/WebRTCDemoMac/ScreenSharePicker.swift) | Hộp chọn màn hình và cửa sổ, có thumbnail |
| [`ScreenShareCapturer.swift`](gh:ios/WebRTCDemoMac/ScreenShareCapturer.swift) | `SCStream` đẩy vào `RTCVideoSource` |
| [`FileVideoCapturer.swift`](gh:ios/WebRTCDemoMac/FileVideoCapturer.swift) | `AVAssetReader` đẩy vào `RTCVideoSource`, phát lặp |
| [`FloatingCallWindow.swift`](gh:ios/WebRTCDemoMac/FloatingCallWindow.swift) | Cửa sổ cuộc gọi thu nhỏ luôn nằm trên cùng |
| [`MacGroupStage.swift`](gh:ios/WebRTCDemoMac/MacGroupStage.swift) | Lưới cuộc gọi nhóm, tile, danh sách người tham gia |
| [`CallCommands.swift`](gh:ios/WebRTCDemoMac/CallCommands.swift) | Menu Call và các phím tắt một phím |

## Khác gì so với iOS {#what-is-different-from-ios}

- **Chia sẻ màn hình.** Dùng ScreenCaptureKit: `SCShareableContent` để lấy danh sách, `SCScreenshotManager` để chụp thumbnail, `SCStream` ở định dạng NV12, cạnh dài tối đa 1920 px, 30 fps. Khi màn hình đứng yên, frame cuối được lặp lại mỗi 500 ms để encoder vẫn tiếp tục ra frame. App không dùng `RTCDesktopCapturer` của webrtc-sdk vì nó dựa vào những API mà từ macOS 15 trở đi không còn hỗ trợ.
- **File video.** Bản webrtc-sdk cho macOS không có `RTCFileVideoCapturer`, nên `FileVideoCapturer` đọc file bằng `AVAssetReader`, nhả frame theo presentation time.
- **View video.** `RTCMTLVideoView` trên Mac không có content mode. View được layout theo đúng tỉ lệ của frame, vừa đủ lớn để phủ kín vùng chứa, còn fit và lật gương là một layer transform quanh tâm.
- **Thiết bị.** Camera lấy từ `RTCCameraVideoCapturer`, micro và loa lấy từ audio device module của factory. Lựa chọn của bạn được ghi nhớ.
- **Cửa sổ nổi.** Một `NSPanel` ở mức floating, có mặt trên mọi Space. Nó tự mở khi bạn thu nhỏ cửa sổ cuộc gọi. [Picture-in-picture](/vi/how-it-works/picture-in-picture#macos)
- **Lật gương.** Capture connection được ghim ở chế độ không lật gương, nên bên kia luôn nhận frame không bị lật và sticker đúng chiều. Chỉ có view local là lật gương.
- **Nhịp chạy effect.** Vision chạy cứ 2 frame một lần, và giảm xuống 3 hoặc 4 frame một lần khi một lượt xử lý bị chậm, vì Mac Intel không có Neural Engine.
- **Sandbox.** App Sandbox có quyền camera, micro, kết nối mạng ra và vào (các ICE check tới mà không cần yêu cầu trước) và file do người dùng chọn. Trên Mac không có audio session nào phải cấu hình.
