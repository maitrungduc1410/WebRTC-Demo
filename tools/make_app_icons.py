#!/usr/bin/env python3
"""Draws the app icon for every native client from one design.

The design is an indigo-to-violet plate with a frosted glass disc and a white video camera.
Run from anywhere (needs Pillow and numpy: `python3 -m pip install pillow numpy`):

    python3 tools/make_app_icons.py

It overwrites:
- iOS:     ios/WebRTCDemo/Assets.xcassets/AppIcon.appiconset (light, dark and tinted, 1024 px)
- macOS:   ios/WebRTCDemoMac/Assets.xcassets/MacAppIcon.appiconset (16-1024 px)
- Android: adaptive icon vectors in res/drawable, legacy WebP launchers in res/mipmap-*,
           and the 512 px Play Store image
- Windows: windows/src/WebRtcDemo.App/Assets/AppIcon.ico (16-256 px) and AppIcon.png (256 px)

Sizes of 32 px and below drop the glass disc and enlarge the camera so it stays legible.
"""

import json
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFilter

ROOT = Path(__file__).resolve().parent.parent
IOS_ICONSET = ROOT / "ios/WebRTCDemo/Assets.xcassets/AppIcon.appiconset"
MAC_ICONSET = ROOT / "ios/WebRTCDemoMac/Assets.xcassets/MacAppIcon.appiconset"
ANDROID_RES = ROOT / "android/app/src/main/res"
ANDROID_PLAY_ICON = ROOT / "android/app/src/main/ic_launcher-playstore.png"
WINDOWS_ASSETS = ROOT / "windows/src/WebRtcDemo.App/Assets"

# Plate gradient, top-left to bottom-right.
GRADIENT = [
    (0.00, (124, 126, 242)),
    (0.42, (84, 76, 222)),
    (0.72, (70, 52, 204)),
    (1.00, (114, 42, 218)),
]
DARK_DISC = [(0.00, (104, 100, 236)), (1.00, (88, 44, 206))]

# Geometry in units of the plate side (0..1).
DISC_RADIUS = 0.305
DISC_STROKE = 0.007
CAMERA_SHIFT_X = -0.012
CAMERA_BODY = (0.300, 0.391, 0.604, 0.610)  # left, top, right, bottom
CAMERA_BODY_RADIUS = 0.056
CAMERA_LENS = [(0.626, 0.462), (0.738, 0.400), (0.738, 0.600), (0.626, 0.538)]
SMALL_CAMERA_SCALE = 1.45

SUPERSAMPLE = 4


def _gradient(size, stops, angle_from=(0.0, 0.0), angle_to=(1.0, 1.0)):
    yy, xx = np.mgrid[0:size, 0:size].astype(np.float32) / max(size - 1, 1)
    dx, dy = angle_to[0] - angle_from[0], angle_to[1] - angle_from[1]
    t = ((xx - angle_from[0]) * dx + (yy - angle_from[1]) * dy) / (dx * dx + dy * dy)
    t = np.clip(t, 0.0, 1.0)
    positions = [p for p, _ in stops]
    channels = [np.interp(t, positions, [c[i] for _, c in stops]) for i in range(3)]
    rgb = np.stack(channels, axis=-1).astype(np.uint8)
    return Image.fromarray(rgb, "RGB").convert("RGBA")


def _camera_points(scale=1.0):
    """Camera body box, corner radius and lens polygon, scaled about the plate center."""

    def tx(x, y):
        return (0.5 + (x + CAMERA_SHIFT_X - 0.5) * scale, 0.5 + (y - 0.5) * scale)

    left, top = tx(CAMERA_BODY[0], CAMERA_BODY[1])
    right, bottom = tx(CAMERA_BODY[2], CAMERA_BODY[3])
    lens = [tx(x, y) for x, y in CAMERA_LENS]
    return (left, top, right, bottom), CAMERA_BODY_RADIUS * scale, lens


def _draw_camera(layer, size, color=(255, 255, 255, 255), scale=1.0):
    body, radius, lens = _camera_points(scale)
    draw = ImageDraw.Draw(layer)
    draw.rounded_rectangle([v * size for v in body], radius=radius * size, fill=color)
    draw.polygon([(x * size, y * size) for x, y in lens], fill=color)


def _draw_disc(layer, size, fill_top, fill_bottom, stroke_alpha):
    """Frosted disc: a vertical white gradient clipped to a circle, with a thin light rim."""
    r = DISC_RADIUS * size
    c = size / 2
    box = [c - r, c - r, c + r, c + r]
    mask = Image.new("L", (size, size), 0)
    ImageDraw.Draw(mask).ellipse(box, fill=255)
    alpha = np.linspace(fill_top, fill_bottom, size, dtype=np.float32)[:, None]
    alpha = np.repeat(alpha, size, axis=1) * (np.asarray(mask, np.float32) / 255.0)
    white = Image.new("RGBA", (size, size), (255, 255, 255, 0))
    white.putalpha(Image.fromarray((alpha * 255).astype(np.uint8), "L"))
    layer.alpha_composite(white)
    ImageDraw.Draw(layer).ellipse(
        box, outline=(255, 255, 255, int(stroke_alpha * 255)), width=max(1, round(DISC_STROKE * size))
    )


def _gloss(size):
    """Soft highlight across the top of the plate."""
    layer = Image.new("RGBA", (size, size), (255, 255, 255, 0))
    mask = Image.new("L", (size, size), 0)
    ImageDraw.Draw(mask).ellipse([-0.45 * size, -1.05 * size, 1.45 * size, 0.36 * size], fill=34)
    layer.putalpha(mask.filter(ImageFilter.GaussianBlur(size * 0.035)))
    return layer


def plate(size, small=False, variant="light"):
    """Full-bleed square artwork, `size` px, before any outline mask."""
    s = size * SUPERSAMPLE
    if variant == "light":
        img = _gradient(s, GRADIENT)
        img.alpha_composite(_gloss(s))
        if small:
            _draw_camera(img, s, scale=SMALL_CAMERA_SCALE)
        else:
            _draw_disc(img, s, 0.24, 0.10, 0.38)
            _draw_camera(img, s)
    elif variant == "dark":
        # iOS draws its own dark backdrop behind a transparent dark icon.
        img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
        disc = _gradient(s, DARK_DISC)
        mask = Image.new("L", (s, s), 0)
        r = DISC_RADIUS * s
        ImageDraw.Draw(mask).ellipse([s / 2 - r, s / 2 - r, s / 2 + r, s / 2 + r], fill=255)
        img.paste(disc, (0, 0), mask)
        _draw_disc(img, s, 0.14, 0.0, 0.30)
        _draw_camera(img, s)
    elif variant == "tinted":
        # Grayscale on black; iOS applies the user's tint.
        img = Image.new("RGBA", (s, s), (0, 0, 0, 255))
        _draw_disc(img, s, 0.30, 0.16, 0.45)
        _draw_camera(img, s)
    else:
        raise ValueError(variant)
    return img.resize((size, size), Image.LANCZOS)


def _rounded_mask(size, inset, radius):
    s = size * SUPERSAMPLE
    mask = Image.new("L", (s, s), 0)
    i = inset * SUPERSAMPLE
    ImageDraw.Draw(mask).rounded_rectangle([i, i, s - 1 - i, s - 1 - i], radius=radius * SUPERSAMPLE, fill=255)
    return mask.resize((size, size), Image.LANCZOS)


def _circle_mask(size, inset):
    s = size * SUPERSAMPLE
    mask = Image.new("L", (s, s), 0)
    i = inset * SUPERSAMPLE
    ImageDraw.Draw(mask).ellipse([i, i, s - 1 - i, s - 1 - i], fill=255)
    return mask.resize((size, size), Image.LANCZOS)


def _masked(art, mask):
    out = art.copy()
    out.putalpha(Image.fromarray(
        (np.asarray(art.getchannel("A"), np.float32) * np.asarray(mask, np.float32) / 255).astype(np.uint8), "L"))
    return out


def mac_icon(size):
    """Apple's macOS grid: an 824/1024 rounded-square plate with a soft drop shadow."""
    canvas = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    inset = round(size * 100 / 1024)
    plate_size = size - 2 * inset
    art = _masked(plate(plate_size, small=size <= 32), _rounded_mask(plate_size, 0, plate_size * 0.2237))
    if size >= 64:
        shadow = Image.new("RGBA", (size, size), (0, 0, 0, 0))
        shadow_alpha = Image.new("L", (size, size), 0)
        shadow_alpha.paste(art.getchannel("A").point(lambda a: a * 0.32), (inset, inset + round(size * 0.012)))
        shadow.putalpha(shadow_alpha.filter(ImageFilter.GaussianBlur(size * 0.022)))
        canvas.alpha_composite(shadow)
    canvas.alpha_composite(art, (inset, inset))
    return canvas


def windows_icon(size):
    """Fluent-style rounded square that fills most of the frame."""
    inset = max(1, round(size * 0.04)) if size > 16 else 0
    radius = size * 0.22
    return _masked(plate(size, small=size <= 32), _rounded_mask(size, inset, radius))


def write_ios():
    IOS_ICONSET.mkdir(parents=True, exist_ok=True)
    plate(1024).convert("RGB").save(IOS_ICONSET / "AppIcon.png")  # iOS rejects alpha here
    plate(1024, variant="dark").save(IOS_ICONSET / "AppIcon-Dark.png")
    plate(1024, variant="tinted").convert("RGB").save(IOS_ICONSET / "AppIcon-Tinted.png")
    entry = {"idiom": "universal", "platform": "ios", "size": "1024x1024"}
    contents = {
        "images": [
            {**entry, "filename": "AppIcon.png"},
            {"appearances": [{"appearance": "luminosity", "value": "dark"}], **entry, "filename": "AppIcon-Dark.png"},
            {"appearances": [{"appearance": "luminosity", "value": "tinted"}], **entry,
             "filename": "AppIcon-Tinted.png"},
        ],
        "info": {"author": "xcode", "version": 1},
    }
    (IOS_ICONSET / "Contents.json").write_text(json.dumps(contents, indent=2) + "\n")


def write_mac():
    MAC_ICONSET.mkdir(parents=True, exist_ok=True)
    images = []
    for points in (16, 32, 128, 256, 512):
        for scale in (1, 2):
            name = f"icon_{points}x{points}{'@2x' if scale == 2 else ''}.png"
            mac_icon(points * scale).save(MAC_ICONSET / name)
            images.append({"filename": name, "idiom": "mac", "scale": f"{scale}x", "size": f"{points}x{points}"})
    contents = {"images": images, "info": {"author": "xcode", "version": 1}}
    (MAC_ICONSET / "Contents.json").write_text(json.dumps(contents, indent=2) + "\n")


def _fmt(v):
    return f"{v:.2f}".rstrip("0").rstrip(".")


def _vector(paths, viewport=108):
    body = "\n".join(paths)
    return (
        '<?xml version="1.0" encoding="utf-8"?>\n'
        '<!-- Generated by tools/make_app_icons.py -->\n'
        '<vector xmlns:android="http://schemas.android.com/apk/res/android"\n'
        '    xmlns:aapt="http://schemas.android.com/aapt"\n'
        f'    android:width="{viewport}dp"\n'
        f'    android:height="{viewport}dp"\n'
        f'    android:viewportWidth="{viewport}"\n'
        f'    android:viewportHeight="{viewport}">\n'
        f"{body}\n"
        "</vector>\n"
    )


def _camera_path(origin, span, scale=1.0):
    """Camera glyph as vector path data, mapped from plate units to the 108 dp canvas."""

    def m(x, y):
        return origin + x * span, origin + y * span

    (l, t, r, b), radius, lens = _camera_points(scale)
    l, t = m(l, t)
    r, b = m(r, b)
    k = radius * span
    body = (
        f"M{_fmt(l + k)},{_fmt(t)}H{_fmt(r - k)}A{_fmt(k)},{_fmt(k)} 0,0 1,{_fmt(r)},{_fmt(t + k)}"
        f"V{_fmt(b - k)}A{_fmt(k)},{_fmt(k)} 0,0 1,{_fmt(r - k)},{_fmt(b)}"
        f"H{_fmt(l + k)}A{_fmt(k)},{_fmt(k)} 0,0 1,{_fmt(l)},{_fmt(b - k)}"
        f"V{_fmt(t + k)}A{_fmt(k)},{_fmt(k)} 0,0 1,{_fmt(l + k)},{_fmt(t)}Z"
    )
    pts = [m(x, y) for x, y in lens]
    lens_path = "M" + "L".join(f"{_fmt(x)},{_fmt(y)}" for x, y in pts) + "Z"
    return body + lens_path


def write_android():
    drawable = ANDROID_RES / "drawable"
    # Background: the plate gradient across the whole 108 dp layer.
    stops = "\n".join(
        f'                <item android:offset="{_fmt(p)}" android:color="#FF{c[0]:02X}{c[1]:02X}{c[2]:02X}" />'
        for p, c in GRADIENT
    )
    background = _vector([
        '    <path android:pathData="M0,0h108v108h-108z">\n'
        '        <aapt:attr name="android:fillColor">\n'
        '            <gradient android:type="linear" android:startX="0" android:startY="0"\n'
        '                android:endX="108" android:endY="108">\n'
        f"{stops}\n"
        "            </gradient>\n"
        "        </aapt:attr>\n"
        "    </path>"
    ])
    # Foreground: the plate artwork mapped onto the 72 dp a launcher shows, so the disc keeps the
    # iOS/macOS proportions and sits well inside the 66 dp safe zone.
    span = 72
    origin = (108 - span) / 2
    r = DISC_RADIUS * span
    disc = (f"M54,{_fmt(54 - r)}A{_fmt(r)},{_fmt(r)} 0,1 1,54,{_fmt(54 + r)}"
            f"A{_fmt(r)},{_fmt(r)} 0,1 1,54,{_fmt(54 - r)}Z")
    foreground = _vector([
        f'    <path android:pathData="{disc}"\n'
        '        android:fillColor="#2EFFFFFF"\n'
        '        android:strokeColor="#61FFFFFF"\n'
        f'        android:strokeWidth="{_fmt(DISC_STROKE * span)}" />',
        f'    <path android:pathData="{_camera_path(origin, span)}"\n'
        '        android:fillColor="#FFFFFFFF" />',
    ])
    # Themed (monochrome) icon: the camera alone, larger.
    monochrome = _vector([
        f'    <path android:pathData="{_camera_path(origin, span, scale=1.4)}"\n'
        '        android:fillColor="#FFFFFFFF" />',
    ])
    (drawable / "ic_launcher_background.xml").write_text(background)
    (drawable / "ic_launcher_foreground.xml").write_text(foreground)
    (drawable / "ic_launcher_monochrome.xml").write_text(monochrome)
    adaptive = (
        '<?xml version="1.0" encoding="utf-8"?>\n'
        '<adaptive-icon xmlns:android="http://schemas.android.com/apk/res/android">\n'
        '    <background android:drawable="@drawable/ic_launcher_background" />\n'
        '    <foreground android:drawable="@drawable/ic_launcher_foreground" />\n'
        '    <monochrome android:drawable="@drawable/ic_launcher_monochrome" />\n'
        "</adaptive-icon>\n"
    )
    for name in ("ic_launcher.xml", "ic_launcher_round.xml"):
        (ANDROID_RES / "mipmap-anydpi-v26" / name).write_text(adaptive)

    # Legacy launchers for API 24-25.
    for density, px in (("mdpi", 48), ("hdpi", 72), ("xhdpi", 96), ("xxhdpi", 144), ("xxxhdpi", 192)):
        folder = ANDROID_RES / f"mipmap-{density}"
        inset = round(px / 48)
        art = plate(px, small=px <= 32)
        _masked(art, _rounded_mask(px, inset, px * 0.18)).save(folder / "ic_launcher.webp", lossless=True)
        _masked(art, _circle_mask(px, inset)).save(folder / "ic_launcher_round.webp", lossless=True)
    plate(512).save(ANDROID_PLAY_ICON)  # Play wants 32-bit PNG; the plate is fully opaque


def write_windows():
    WINDOWS_ASSETS.mkdir(parents=True, exist_ok=True)
    sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]
    frames = [windows_icon(s) for s in sizes]
    frames[-1].save(WINDOWS_ASSETS / "AppIcon.ico", sizes=[(s, s) for s in sizes], append_images=frames[:-1])
    frames[-1].save(WINDOWS_ASSETS / "AppIcon.png")


def main():
    write_ios()
    write_mac()
    write_android()
    write_windows()
    print("App icons written for iOS, macOS, Android and Windows.")


if __name__ == "__main__":
    main()
