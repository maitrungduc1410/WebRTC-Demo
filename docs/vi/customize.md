# Tùy biến

## Thêm phông nền {#add-backgrounds}

Mọi app đều đóng gói thư mục [`effects`](gh:effects) ở gốc repository, nên phông nền thêm vào đó sẽ có mặt ở mọi app.

1. Đặt ảnh gốc (`.jpg` `.jpeg` `.png` `.webp`) và video gốc (`.mp4` `.mov` `.webm` `.mkv`) vào `effects-source/` ở gốc repository. Git bỏ qua thư mục này. Đặt tên file theo kiểu kebab-case, mô tả nội dung, ví dụ `cozy-living-room.jpg` hay `beach-sunset.mp4`. Tên file sẽ thành ID và tiêu đề hiện trong app ("Cozy living room").
2. Chạy `python3 tools/prepare_effects.py`. Script cần `ffmpeg` và `ffprobe`. Ảnh được thu nhỏ để cạnh dài là 1920 px. Video được cắt về 1280 px, tối đa 15 s, 30 fps, H.264 không có audio. Mỗi file có thêm một thumbnail 320×180, và `effects/backgrounds.json` được ghi lại từ đầu. Thêm `--force` nếu muốn encode lại toàn bộ.
3. Nếu muốn, bạn sửa các trường `name` trong `effects/backgrounds.json`. Lần chạy script sau vẫn giữ nguyên những tên này.

Làm mờ và "none" có sẵn trong từng app, không cần file.

## Thêm sticker {#add-stickers}

Danh sách sticker nằm trong [`effects/stickers.json`](gh:effects/stickers.json), còn hình vẽ nằm trong [`effects/stickers`](gh:effects/stickers).

- Kích thước và độ lệch được đo bằng khoảng cách giữa hai mắt, tính từ `anchor`: `eyes`, `nose` hoặc `mouth`.
- `offsetY` dương thì sticker dịch lên phía trên khuôn mặt.
- `height` không bắt buộc, dùng để kéo giãn hình vẽ.

Mọi app đặt sticker bằng cùng một đoạn code, mô tả ở [Đặt sticker](/vi/how-it-works/effects#sticker-placement). Nếu sửa trên một nền tảng thì phải sửa trên tất cả.

## Đổi icon app {#change-the-app-icon}

App iOS, macOS, Android và Windows dùng chung một icon, được vẽ bằng [`tools/make_app_icons.py`](gh:tools/make_app_icons.py): một tấm nền chuyển từ chàm sang tím, một đĩa kính mờ và một chiếc camera màu trắng. Sau khi đổi màu hoặc hình, chạy:

```sh
pip install pillow numpy
python3 tools/make_app_icons.py
```

Script sẽ ghi lại:

- iOS: `AppIcon` với các biến thể sáng, tối và tinted.
- macOS: `MacAppIcon`, từ 16 đến 1024 px theo lưới của Apple, có đổ bóng.
- Android: adaptive icon có lớp monochrome cho themed icon, launcher WebP cho Android 7, và ảnh 512 px cho Play Store.
- Windows: `Assets/AppIcon.ico` (16 đến 256 px) và `AppIcon.png` cho thanh tiêu đề.

Từ 32 px trở xuống, đĩa kính bị bỏ đi và camera được vẽ to hơn để vẫn dễ nhìn.

## Ghi công {#credits}

- Sticker dựa trên [Noto Emoji](https://github.com/googlefonts/noto-emoji) (Apache License 2.0, xem [`effects/stickers/LICENSE`](gh:effects/stickers/LICENSE)). Chiếc tai nghe đã được sửa lại hình và đổi màu.
- Ảnh và video phông nền lấy từ [Pexels](https://www.pexels.com) và [Pixabay](https://pixabay.com/), theo [giấy phép Pexels](https://www.pexels.com/license/) và [Pixabay Content License](https://pixabay.com/service/license-summary/).
- Model MediaPipe (`selfie_segmenter`, `face_landmarker`) được đóng gói sẵn trong app Android, chuyển sang ONNX cho app Windows (Apache License 2.0, xem [`windows/models/NOTICE.txt`](gh:windows/models/NOTICE.txt)), còn trên web thì tải từ kho model của MediaPipe.
