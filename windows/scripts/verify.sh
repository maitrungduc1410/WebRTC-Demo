#!/usr/bin/env bash
# Everything that can be checked without Windows: the shim against the Linux libwebrtc build
# (loopback test), the .NET tests (with the shim, and against a running signaling server when
# one is up), and a compile of the WinUI app against the Windows App SDK reference assemblies.
#
#   windows/scripts/verify.sh            # all of it
#   SKIP_NATIVE=1 windows/scripts/verify.sh
#
# The native part needs cmake, ninja, a C++17 compiler and an audio server (PulseAudio with a
# null sink is enough): libwebrtc aborts when it finds no audio device.
set -euo pipefail

here="$(cd "$(dirname "$0")/.." && pwd)"
# global.json (SDK pin and the Microsoft.Testing.Platform runner) lives in windows/.
cd "$here"
dotnet="${DOTNET:-dotnet}"
shim_build="$here/native/RtcShim/out/build/linux-test"

if [[ "${SKIP_NATIVE:-0}" != 1 ]]; then
  "$here/native/RtcShim/scripts/fetch-libwebrtc.sh" linux-x64
  cmake --preset linux-test -S "$here/native/RtcShim"
  cmake --build "$shim_build"
  "$shim_build/rtc_shim_loopback"
  native_args=(-p:RtcShimNativeDir="$shim_build")
else
  native_args=()
fi

# The committed effects models are the ones convert.sh produces (re-run it to rebuild them).
(cd "$here/models" && sha256sum --check --quiet SHA256SUMS)

# Includes real ONNX inference with the committed models (CPU ONNX Runtime).
"$dotnet" test --project "$here/tests/WebRtcDemo.Core.Tests" "${native_args[@]}"

# mt.exe and makepri.exe only exist on Windows, so stop after the C# compile.
"$dotnet" msbuild "$here/src/WebRtcDemo.App/WebRtcDemo.App.csproj" -restore -t:Compile \
  -p:WindowsAppSDKSelfContained=false -p:Platform=x64 -v:m
"$dotnet" msbuild "$here/src/WebRtcDemo.App/WebRtcDemo.App.csproj" -restore -t:Compile \
  -p:WindowsAppSDKSelfContained=false -p:Platform=ARM64 -v:m
echo "verify: OK"
