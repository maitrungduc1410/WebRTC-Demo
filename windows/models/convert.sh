#!/usr/bin/env bash
# Rebuilds the ONNX models in this folder from the MediaPipe models the Android app ships
# (android/app/src/main/assets), with pinned tools, and checks the result against SHA256SUMS.
#
#   bash windows/models/convert.sh           # convert into a temp dir and compare with SHA256SUMS
#   bash windows/models/convert.sh --update  # also overwrite the committed .onnx files and SHA256SUMS
#
# Needs python3.12 with venv and network access to PyPI. Linux x86-64 was used for the committed files.
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
assets="$here/../../android/app/src/main/assets"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

check() { # file sha256
  local actual
  actual="$(sha256sum "$1" | cut -d' ' -f1)"
  [[ "$actual" == "$2" ]] || { echo "SHA-256 mismatch for $1: $actual (expected $2)" >&2; exit 1; }
}
check "$assets/selfie_segmenter.tflite" 191ac9529ae506ee0beefa6b2c945a172dab9d07d1e802a290a4e4038226658b
check "$assets/face_landmarker.task" 64184e229b263107bc2b804c6625db1341ff2bb731874b0bcc2fe6544e0bc9ff

# MODELS_VENV reuses an existing venv that already has these exact versions.
venv="${MODELS_VENV:-$work/venv}"
if [[ -z "${MODELS_VENV:-}" ]]; then
  python3.12 -m venv "$venv"
  "$venv/bin/pip" install -q --disable-pip-version-check \
    tensorflow-cpu==2.19.0 tf2onnx==1.16.1 onnx==1.17.0 numpy==1.26.4 protobuf==3.20.3 flatbuffers==25.12.19
fi

cp "$assets/selfie_segmenter.tflite" "$work/"
# The .task bundle is a zip; the face landmarker is the face detector plus the mesh model.
"$venv/bin/python" -c "import zipfile,sys; zipfile.ZipFile(sys.argv[1]).extractall(sys.argv[2], ['face_detector.tflite','face_landmarks_detector.tflite'])" \
  "$assets/face_landmarker.task" "$work"

out="$work/out"
mkdir -p "$out"
# A fixed hash seed plus postprocess.py's renaming make the output byte-reproducible.
for name in selfie_segmenter face_detector face_landmarks_detector; do
  PYTHONHASHSEED=0 TF_CPP_MIN_LOG_LEVEL=3 "$venv/bin/python" -m tf2onnx.convert --opset 17 \
    --tflite "$work/$name.tflite" --output "$work/$name.raw.onnx" > "$work/$name.log" 2>&1 \
    || { cat "$work/$name.log" >&2; exit 1; }
  "$venv/bin/python" "$here/postprocess.py" "$work/$name.raw.onnx" "$out/$name.onnx"
done

(cd "$out" && sha256sum selfie_segmenter.onnx face_detector.onnx face_landmarks_detector.onnx) > "$work/SHA256SUMS"
if [[ "${1:-}" == "--update" ]]; then
  cp "$out"/*.onnx "$here/"
  cp "$work/SHA256SUMS" "$here/SHA256SUMS"
  echo "Updated $here"
elif diff -u "$here/SHA256SUMS" "$work/SHA256SUMS"; then
  echo "Models match SHA256SUMS"
else
  echo "Converted models differ from SHA256SUMS" >&2
  exit 1
fi
