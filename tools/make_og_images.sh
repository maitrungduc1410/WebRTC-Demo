#!/usr/bin/env bash
# Draws the social share images of the docs site (1200x630, one per language) into docs/public/.
# Needs ffmpeg with drawtext and fontconfig, DejaVu Sans and Noto Sans CJK.
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
out="$root/docs/public"
logo="$out/logo.png"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

# drawtext font options. Latin text uses DejaVu font files. Chinese uses fontconfig family names:
# Noto Sans CJK ships as .ttc collections whose first face is Japanese, drawtext can't pick a face
# from a file, and a fontconfig name picks the Simplified Chinese one. Weights are separate
# families there ("... Black"), because a ":style=" suffix doesn't survive filter escaping.
for family in 'DejaVu Sans' 'Noto Sans CJK SC Black'; do
  fc-list "$family" | grep -q . || { echo "$family is not installed" >&2; exit 1; }
done
dejavu_bold="fontfile=$(fc-match -f '%{file}' 'DejaVu Sans:style=Bold')"
dejavu="fontfile=$(fc-match -f '%{file}' 'DejaVu Sans:style=Book')"
cjk_bold="font=Noto Sans CJK SC Black"
cjk_medium="font=Noto Sans CJK SC Medium"
cjk="font=Noto Sans CJK SC"

# draw <file> <title font> <subtitle font> <text font> <subtitle, two lines> <features> [subtitle size]
draw() {
  local file="$1" title_font="$2" subtitle_font="$3" text_font="$4" size="${7:-40}"
  printf '%s' "$5" > "$tmp/subtitle.txt"
  printf '%s' "$6" > "$tmp/features.txt"
  printf '%s' 'maitrungduc1410.github.io/WebRTC-Demo' > "$tmp/url.txt"
  ffmpeg -v error -y \
    -f lavfi -i "gradients=s=1200x630:c0=0x1e1b4b:c1=0x312e81:c2=0x4c1d95:nb_colors=3:x0=0:y0=0:x1=1200:y1=630:seed=1:speed=0.00001" \
    -i "$logo" \
    -filter_complex "\
[1:v]scale=240:240[logo];\
[0:v][logo]overlay=80:195,\
drawtext=$title_font:text='WebRTC Demo':fontsize=88:fontcolor=white:x=372:y=130,\
drawtext=$subtitle_font:textfile='$tmp/subtitle.txt':fontsize=$size:line_spacing=16:fontcolor=0xe0e7ff:x=376:y=262,\
drawtext=$text_font:textfile='$tmp/features.txt':fontsize=27:fontcolor=0xc7d2fe:x=376:y=420,\
drawtext=$text_font:textfile='$tmp/url.txt':fontsize=24:fontcolor=0xa5b4fc:x=376:y=520" \
    -frames:v 1 -update 1 "$out/$file"
  echo "wrote docs/public/$file"
}

draw og-image.png "$dejavu_bold" "$dejavu_bold" "$dejavu" \
  $'Video call apps for Web, Android,\niOS, macOS and Windows' \
  '1:1 and group calls · Screen sharing · Chat · E2EE'
draw og-image-vi.png "$dejavu_bold" "$dejavu_bold" "$dejavu" \
  $'App gọi video cho Web, Android,\niOS, macOS và Windows' \
  'Gọi 1:1 và gọi nhóm · Chia sẻ màn hình · Chat · E2EE'
draw og-image-zh.png "$cjk_bold" "$cjk_medium" "$cjk" \
  $'视频通话应用，原生支持\nWeb、Android、iOS、macOS 和 Windows' \
  '一对一与多人通话 · 屏幕共享 · 聊天 · 端到端加密' 36
