#!/usr/bin/env bash
# Downloads the pinned webrtc-sdk/libwebrtc release, verifies its SHA-256 and extracts it to
# third_party/libwebrtc/<arch>/. Arches: x64 arm64 linux-x64 (default: x64 arm64).
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
lock="$root/libwebrtc.lock.json"
cache="$root/third_party/downloads"
mkdir -p "$cache"

field() {  # field <json path...>
  python3 - "$lock" "$@" <<'PY'
import json, sys
value = json.load(open(sys.argv[1]))
for key in sys.argv[2:]:
    value = value[key]
print(value)
PY
}

tag="$(field tag)"
url="$(field url)"
arches=("$@")
[ ${#arches[@]} -eq 0 ] && arches=(x64 arm64)

for arch in "${arches[@]}"; do
  file="$(field assets "$arch" file)"
  folder="$(field assets "$arch" folder)"
  sha="$(field assets "$arch" sha256)"
  destination="$root/third_party/libwebrtc/$arch"
  if [ -f "$destination/.sha256" ] && [ "$(cat "$destination/.sha256")" = "$sha" ]; then
    echo "libwebrtc $tag $arch is up to date"
    continue
  fi

  zip="$cache/$file"
  if [ ! -f "$zip" ] || [ "$(sha256sum "$zip" | cut -d' ' -f1)" != "$sha" ]; then
    echo "Downloading $file ($tag)"
    curl -fL --retry 3 -o "$zip.part" "$url$file"
    mv -f "$zip.part" "$zip"
  fi

  actual="$(sha256sum "$zip" | cut -d' ' -f1)"
  if [ "$actual" != "$sha" ]; then
    rm -f "$zip"
    echo "SHA-256 mismatch for $file: expected $sha, got $actual" >&2
    exit 1
  fi

  rm -rf "$destination"
  mkdir -p "$destination"
  unzip -q "$zip" -d "$destination"
  if [ ! -f "$destination/$folder/include/libwebrtc.h" ]; then
    echo "Unexpected archive layout in $file" >&2
    exit 1
  fi
  printf '%s' "$sha" > "$destination/.sha256"
  echo "libwebrtc $tag $arch -> $destination"
done
