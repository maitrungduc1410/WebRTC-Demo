# Customize

## Add backgrounds {#add-backgrounds}

All the apps bundle the [`effects`](gh:effects) folder at the repository root, so a background added there shows up everywhere.

1. Put the original pictures (`.jpg` `.jpeg` `.png` `.webp`) and videos (`.mp4` `.mov` `.webm` `.mkv`) in `effects-source/` at the repository root. Git ignores that folder. Name the files in kebab-case after what they show, like `cozy-living-room.jpg` or `beach-sunset.mp4`. The name becomes the ID and the title shown in the app ("Cozy living room").
2. Run `python3 tools/prepare_effects.py`. It needs `ffmpeg` and `ffprobe`. Pictures are resized to 1920 px on the long side. Videos are cut to 1280 px, at most 15 s, 30 fps, H.264 without audio. Each one gets a 320×180 thumbnail, and `effects/backgrounds.json` is rewritten. Use `--force` to encode everything again.
3. Optionally edit the `name` fields in `effects/backgrounds.json`. They are kept the next time the script runs.

Blur and "none" are built into each app and don't need a file.

## Add stickers {#add-stickers}

Stickers are listed in [`effects/stickers.json`](gh:effects/stickers.json), with their artwork in [`effects/stickers`](gh:effects/stickers).

- Sizes and offsets are measured in distances between the eyes, from the `anchor`: `eyes`, `nose` or `mouth`.
- A positive `offsetY` moves the sticker up the face.
- `height` is optional and stretches the artwork.

Every app places stickers with the same code, described in [Sticker placement](/how-it-works/effects#sticker-placement). If you change it on one platform, change it on all of them.

## Change the app icon {#change-the-app-icon}

The iOS, macOS, Android and Windows apps share one icon, drawn by [`tools/make_app_icons.py`](gh:tools/make_app_icons.py): an indigo to violet plate, a frosted glass disc and a white camera. After changing its colors or shapes, run:

```sh
pip install pillow numpy
python3 tools/make_app_icons.py
```

It rewrites:

- iOS: `AppIcon` with light, dark and tinted variants.
- macOS: `MacAppIcon`, 16 to 1024 px on Apple's grid with a drop shadow.
- Android: an adaptive icon with a monochrome layer for themed icons, WebP launchers for Android 7, and the 512 px Play Store image.
- Windows: `Assets/AppIcon.ico` (16 to 256 px) and `AppIcon.png` for the title bar.

At 32 px and below the glass disc is dropped and the camera is drawn larger, so it stays readable.

## Credits {#credits}

- Stickers are based on [Noto Emoji](https://github.com/googlefonts/noto-emoji) (Apache License 2.0, see [`effects/stickers/LICENSE`](gh:effects/stickers/LICENSE)). The headphones were reshaped and recolored.
- Background pictures and videos come from [Pexels](https://www.pexels.com) and [Pixabay](https://pixabay.com/), under the [Pexels license](https://www.pexels.com/license/) and the [Pixabay Content License](https://pixabay.com/service/license-summary/).
- The MediaPipe models (`selfie_segmenter`, `face_landmarker`) are bundled with the Android app, converted to ONNX for the Windows app (Apache License 2.0, see [`windows/models/NOTICE.txt`](gh:windows/models/NOTICE.txt)), and loaded from MediaPipe's model storage on the web.
