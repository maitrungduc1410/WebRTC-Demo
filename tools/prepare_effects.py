#!/usr/bin/env python3
"""Turns raw background downloads into the files the apps bundle.

Drop images (.jpg .jpeg .png .webp) and videos (.mp4 .mov .webm .mkv) into effects-source/,
named in kebab-case (cozy-living-room.jpg, beach-sunset.mp4), then run:

    python3 tools/prepare_effects.py

Each file becomes effects/backgrounds/<id>.jpg or <id>.mp4 plus effects/thumbnails/<id>.jpg,
and effects/backgrounds.json is rewritten from what is in effects/backgrounds/. Names already in
the manifest are kept, so they can be edited by hand. Needs ffmpeg on the PATH.
"""

import argparse
import json
import re
import shutil
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
EFFECTS = ROOT / "effects"
BACKGROUNDS = EFFECTS / "backgrounds"
THUMBNAILS = EFFECTS / "thumbnails"
MANIFEST = EFFECTS / "backgrounds.json"

IMAGE_EXTENSIONS = {".jpg", ".jpeg", ".png", ".webp"}
VIDEO_EXTENSIONS = {".mp4", ".mov", ".webm", ".mkv"}

IMAGE_LONG_SIDE = 1920
VIDEO_LONG_SIDE = 1280
VIDEO_MAX_SECONDS = 15
THUMB_SIZE = (320, 180)
# Built into every app; a file with one of these names would be ignored.
BUILT_IN_IDS = {"none", "blur-light", "blur-strong"}


def slug(name: str) -> str:
    return re.sub(r"[^a-z0-9]+", "-", name.lower()).strip("-")


def title(slug_id: str) -> str:
    return slug_id.replace("-", " ").capitalize()


def fit_long_side(limit: int) -> str:
    # Never upscales; -2 keeps the other side even, which H.264 requires.
    return (
        f"scale='if(gte(iw,ih),min({limit},iw),-2)':'if(gte(iw,ih),-2,min({limit},ih))'"
        ":flags=lanczos"
    )


def thumb_filter() -> str:
    w, h = THUMB_SIZE
    return f"scale={w}:{h}:force_original_aspect_ratio=increase:flags=lanczos,crop={w}:{h}"


def ffmpeg(*args: str) -> None:
    subprocess.run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-y", *args], check=True)


def duration(path: Path) -> float:
    out = subprocess.run(
        ["ffprobe", "-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", str(path)],
        check=True, capture_output=True, text=True,
    ).stdout.strip()
    try:
        return float(out)
    except ValueError:
        return 0.0


def process_image(src: Path, out: Path, thumb: Path) -> None:
    ffmpeg("-i", str(src), "-vf", fit_long_side(IMAGE_LONG_SIDE), "-q:v", "3", str(out))
    ffmpeg("-i", str(out), "-vf", thumb_filter(), "-q:v", "4", str(thumb))


def process_video(src: Path, out: Path, thumb: Path) -> None:
    ffmpeg(
        "-i", str(src), "-t", str(VIDEO_MAX_SECONDS), "-an",
        "-vf", fit_long_side(VIDEO_LONG_SIDE), "-fpsmax", "30",
        "-c:v", "libx264", "-profile:v", "main", "-preset", "slow", "-crf", "26",
        "-pix_fmt", "yuv420p", "-movflags", "+faststart", str(out),
    )
    at = min(1.0, duration(out) / 2)
    ffmpeg("-ss", f"{at:.2f}", "-i", str(out), "-frames:v", "1", "-vf", thumb_filter(), "-q:v", "4", str(thumb))


def build_manifest() -> list[dict]:
    names = {}
    if MANIFEST.exists():
        names = {b["id"]: b["name"] for b in json.loads(MANIFEST.read_text())["backgrounds"]}
    entries = []
    for path in sorted(BACKGROUNDS.iterdir()) if BACKGROUNDS.exists() else []:
        kind = "image" if path.suffix == ".jpg" else "video" if path.suffix == ".mp4" else None
        if kind is None:
            continue
        thumb = THUMBNAILS / f"{path.stem}.jpg"
        if not thumb.exists():
            print(f"warning: {path.name} has no thumbnail, skipped", file=sys.stderr)
            continue
        entries.append({
            "id": path.stem,
            "name": names.get(path.stem, title(path.stem)),
            "type": kind,
            "file": f"backgrounds/{path.name}",
            "thumbnail": f"thumbnails/{thumb.name}",
        })
    # Images first, like the pickers show them.
    entries.sort(key=lambda e: (e["type"] != "image", e["id"]))
    return entries


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--source", type=Path, default=ROOT / "effects-source", help="folder with the raw downloads")
    parser.add_argument("--force", action="store_true", help="re-encode files that are already up to date")
    args = parser.parse_args()

    if shutil.which("ffmpeg") is None or shutil.which("ffprobe") is None:
        print("ffmpeg and ffprobe are required", file=sys.stderr)
        return 1
    BACKGROUNDS.mkdir(parents=True, exist_ok=True)
    THUMBNAILS.mkdir(parents=True, exist_ok=True)

    sources = sorted(p for p in args.source.iterdir() if p.is_file()) if args.source.exists() else []
    seen: dict[str, Path] = {}
    for src in sources:
        ext = src.suffix.lower()
        if ext not in IMAGE_EXTENSIONS | VIDEO_EXTENSIONS:
            continue
        bg_id = slug(src.stem)
        if not bg_id:
            print(f"skipping {src.name}: no usable name", file=sys.stderr)
            continue
        if bg_id in BUILT_IN_IDS:
            print(f"skipping {src.name}: '{bg_id}' is a built-in background, rename the file", file=sys.stderr)
            continue
        if bg_id in seen:
            print(f"skipping {src.name}: id '{bg_id}' already used by {seen[bg_id].name}", file=sys.stderr)
            continue
        seen[bg_id] = src
        is_video = ext in VIDEO_EXTENSIONS
        out = BACKGROUNDS / f"{bg_id}.{'mp4' if is_video else 'jpg'}"
        other = BACKGROUNDS / f"{bg_id}.{'jpg' if is_video else 'mp4'}"
        thumb = THUMBNAILS / f"{bg_id}.jpg"
        if not args.force and out.exists() and thumb.exists() and out.stat().st_mtime >= src.stat().st_mtime:
            continue
        print(f"{src.name} -> {out.relative_to(ROOT)}")
        other.unlink(missing_ok=True)
        (process_video if is_video else process_image)(src, out, thumb)

    entries = build_manifest()
    MANIFEST.write_text(json.dumps({"backgrounds": entries}, indent=2) + "\n")
    print(f"{MANIFEST.relative_to(ROOT)}: {len(entries)} backgrounds")
    return 0


if __name__ == "__main__":
    sys.exit(main())
