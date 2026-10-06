---
title: "Picture-in-picture cho cuộc gọi video trên mọi nền tảng"
description: "Cách mỗi app giữ cuộc gọi trong cửa sổ nổi: Document Picture-in-Picture trên web, chế độ PiP của Android, AVKit trên iOS, cửa sổ nổi trên macOS và Windows."
---

# Picture-in-picture

App nào cũng có thể giữ cuộc gọi trong một cửa sổ nhỏ nằm trên các app khác. Mỗi nền tảng có API riêng cho việc này, và những gì chúng cho phép khác nhau rất nhiều.

<DemoMedia src="/media/pip-web.png" :width="720">
Chrome trên máy tính, phía trước là một tab hoặc app khác, và cửa sổ picture-in-picture nổi của cuộc gọi nằm trên cùng: video của người kia, tile nhỏ của bạn và các nút micro, camera, cúp máy.
</DemoMedia>

| Nền tảng | API | Cửa sổ hiện gì |
| --- | --- | --- |
| Web (Chrome, Edge) | Document Picture-in-Picture | Một cuộc gọi thu nhỏ: video bên kia, tile của bạn, micro, camera và cúp máy |
| Web (trình duyệt khác) | Video picture-in-picture | Chỉ video bên kia |
| Android | Activity picture-in-picture | Video bên kia |
| iOS | `AVPictureInPictureController`, PiP cho cuộc gọi video | Video bên kia (chỉ cuộc gọi 1:1) |
| macOS | Một `NSPanel` luôn nằm trên cùng | Video bên kia, tile của bạn, nút điều khiển hiện khi di chuột vào |
| Windows | Cửa sổ `CompactOverlay` | Video bên kia kèm nút tắt tiếng, camera, quay lại và rời phòng |

Trong cuộc gọi nhóm, cửa sổ hiện người đang nói, nếu không có thì hiện người đầu tiên đang bật camera.

## Web {#web}

[`usePictureInPicture.ts`](gh:web/src/composables/usePictureInPicture.ts) dùng Document Picture-in-Picture API nếu trình duyệt có (Chromium trên máy tính), nếu không thì quay về video picture-in-picture.

- `documentPictureInPicture.requestWindow()` mở một cửa sổ luôn nằm trên cùng. Stylesheet của trang được copy sang đó, và [`PipView.vue`](gh:web/src/components/call/PipView.vue) được render vào cửa sổ đó qua `<Teleport>`. Animation trong đó chỉ dùng CSS, vì tab chứa app Vue lúc này bị ẩn và `requestAnimationFrame` của nó không chạy.
- Khi đã có peer kết nối, trang đăng ký action `enterpictureinpicture` của Media Session. Từ Chrome 134, khi bạn chuyển sang tab khác, trình duyệt tự mở cửa sổ này, giống Google Meet. Chuyển sang app khác thì không tự mở, phải bấm nút (hoặc `P`).
- Các trình duyệt khác đưa chính phần tử `<video>` của bên kia vào picture-in-picture (`requestPictureInPicture()`, hoặc `webkitSetPresentationMode('picture-in-picture')` trên Safari iOS).

## Android {#android}

[`CallActivity`](gh:android/app/src/main/java/com/example/myapplication/CallActivity.kt) khai báo `supportsPictureInPicture` và tự xử lý việc đổi kích thước, nên vào hay ra cửa sổ cũng không làm activity bị tạo lại.

- Khi đã có peer kết nối, `setAutoEnterEnabled(true)` trên Android 12+ đưa cuộc gọi vào cửa sổ nhỏ khi bạn vuốt về màn hình chính. Bản Android cũ hơn làm việc tương tự từ `onUserLeaveHint`.
- Tỉ lệ khung hình của cửa sổ theo frame của bên kia, giới hạn trong khoảng 1:2.39 đến 2.39:1 mà hệ thống chấp nhận.
- Trong cửa sổ chỉ hiện video bên kia. Tile của bạn bị ẩn chứ không bị xóa, nên nó vẫn giữ đúng góc cũ.
- Đóng cửa sổ là kết thúc cuộc gọi, vì không có gì giữ camera và micro chạy nền.

## iOS {#ios}

Cuộc gọi dùng PiP dành cho cuộc gọi video: một `AVPictureInPictureController` với content source `activeVideoCallSourceView` và một `AVPictureInPictureVideoCallViewController` ([`PictureInPicture.swift`](gh:ios/WebRTCDemo/PictureInPicture.swift)).

- Cửa sổ hệ thống không hiện được video view Metal, nên một sink thứ hai trên remote track cấp frame cho một `AVSampleBufferDisplayLayer`. Frame decode bằng phần cứng được đưa vào nguyên như vậy. Frame I420 decode bằng phần mềm, chính là loại VP8 tạo ra, thì được copy sang các pixel buffer NV12 lấy từ pool.
- `canStartPictureInPictureAutomaticallyFromInline` được bật khi đã có peer kết nối, nên rời app là cửa sổ tự mở.
- Ở nơi hỗ trợ, camera session bật `isMultitaskingCameraAccessEnabled`, để người kia vẫn thấy bạn khi cuộc gọi đang ở PiP.

## macOS {#macos}

Mac không có PiP hệ thống cho cuộc gọi, nên [`FloatingCallWindow.swift`](gh:ios/WebRTCDemoMac/FloatingCallWindow.swift) là một `NSPanel` ở mức floating, có mặt trên mọi Space. Nó hiện video bên kia, một ô nhỏ hình của bạn và các nút điều khiển khi di chuột vào, và tự mở khi bạn thu nhỏ cửa sổ cuộc gọi.

## Windows {#windows}

Cửa sổ chuyển sang presenter `CompactOverlay`: một cửa sổ nhỏ luôn nằm trên cùng, có video bên kia và các nút tắt tiếng, camera, quay lại và rời phòng. Trong cuộc gọi nhóm, cửa sổ tự đóng khi người cuối cùng rời đi.
