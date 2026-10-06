---
title: "添加背景和贴纸，更换应用图标"
description: "一次性为所有应用添加自己的背景图片、视频和人脸贴纸，并重新生成共用的应用图标。"
---

# 自定义

## 添加背景 {#add-backgrounds}

所有应用都会打包仓库根目录下的 [`effects`](gh:effects) 目录，所以在这里添加的背景会出现在每个平台上。

1. 把原始图片（`.jpg` `.jpeg` `.png` `.webp`）和视频（`.mp4` `.mov` `.webm` `.mkv`）放进仓库根目录的 `effects-source/`，Git 会忽略这个目录。文件名用 kebab-case 描述画面内容，比如 `cozy-living-room.jpg` 或 `beach-sunset.mp4`。文件名会成为 ID，也会作为应用里显示的标题（"Cozy living room"）。
2. 运行 `python3 tools/prepare_effects.py`，需要先装好 `ffmpeg` 和 `ffprobe`。图片会缩放到长边 1920 px；视频会缩到 1280 px，最长 15 s，30 fps，H.264 编码且不带音轨。每个文件都会生成一张 320×180 的缩略图，同时重写 `effects/backgrounds.json`。加上 `--force` 可以全部重新编码。
3. 可以按需修改 `effects/backgrounds.json` 里的 `name` 字段，下次运行脚本时会保留你的修改。

模糊和"无"是各应用内置的，不需要文件。

## 添加贴纸 {#add-stickers}

贴纸列在 [`effects/stickers.json`](gh:effects/stickers.json) 中，素材图放在 [`effects/stickers`](gh:effects/stickers)。

- 尺寸和偏移量以两眼间距为单位，从 `anchor` 开始计算，`anchor` 可以是 `eyes`、`nose` 或 `mouth`。
- `offsetY` 为正时，贴纸沿脸部向上移动。
- `height` 可选，设置后会拉伸素材图。

所有应用都用同一套代码摆放贴纸，详见[贴纸定位](/zh/how-it-works/effects#sticker-placement)。如果在一个平台上改了这部分代码，其他平台也要同步修改。

## 修改应用图标 {#change-the-app-icon}

iOS、macOS、Android 和 Windows 应用共用一个图标，由 [`tools/make_app_icons.py`](gh:tools/make_app_icons.py) 绘制：一块从靛蓝到紫罗兰渐变的底板，一个磨砂玻璃圆盘，加上一台白色摄像机。修改颜色或形状后运行：

```sh
pip install pillow numpy
python3 tools/make_app_icons.py
```

脚本会重新生成：

- iOS：`AppIcon`，包含浅色、深色和着色三种变体。
- macOS：`MacAppIcon`，16 到 1024 px，遵循 Apple 的图标网格并带投影。
- Android：自适应图标（带用于主题图标的单色图层）、供 Android 7 使用的 WebP 启动图标，以及 512 px 的 Play 商店图片。
- Windows：`Assets/AppIcon.ico`（16 到 256 px）和标题栏用的 `AppIcon.png`。

在 32 px 及以下的尺寸中，玻璃圆盘会被去掉，摄像机画得更大，保证小尺寸下依然清晰可辨。

## 致谢 {#credits}

- 贴纸基于 [Noto Emoji](https://github.com/googlefonts/noto-emoji) 制作（Apache License 2.0，见 [`effects/stickers/LICENSE`](gh:effects/stickers/LICENSE)），其中耳机重新调整了造型和配色。
- 背景图片和视频来自 [Pexels](https://www.pexels.com) 和 [Pixabay](https://pixabay.com/)，分别遵循 [Pexels 许可](https://www.pexels.com/license/)和 [Pixabay 内容许可](https://pixabay.com/service/license-summary/)。
- MediaPipe 模型（`selfie_segmenter`、`face_landmarker`）随 Android 应用一起打包，为 Windows 应用转换成了 ONNX 格式（Apache License 2.0，见 [`windows/models/NOTICE.txt`](gh:windows/models/NOTICE.txt)），Web 端则从 MediaPipe 的模型存储中加载。
