<#
.SYNOPSIS
  Fetches libwebrtc and builds rtc_shim.dll with the CMake presets.
  Output: out/install/<arch>/{rtc_shim.dll, libwebrtc.dll}, which the app project picks up.
  Runs from any PowerShell (Windows PowerShell 5.1 or 7): vswhere finds the newest Visual Studio
  with the C++ workload (2026 or 2022), and that installation's CMake and generator are used.
.PARAMETER Arch
  x64 and/or arm64. Defaults to this PC's architecture; arm64 needs "MSVC ARM64 build tools".
.PARAMETER Configuration
  Release, or Debug for an unoptimized build with symbols. Both link the release C runtime that
  libwebrtc.dll uses (see CMakeLists.txt); there is no debug-CRT build.
#>
param(
  [ValidateSet('x64', 'arm64')]
  [string[]] $Arch = @($(if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { 'arm64' } else { 'x64' })),
  [ValidateSet('Release', 'Debug')]
  [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path $vswhere)) {
  throw 'Visual Studio was not found. Install Visual Studio 2026 with the "Desktop development with C++" workload.'
}
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -format json |
  ConvertFrom-Json | Select-Object -First 1
if (-not $vs) {
  throw 'No Visual Studio with the C++ compiler. In the Visual Studio Installer, add the "Desktop development with C++" workload.'
}
$generator = switch ([int]$vs.installationVersion.Split('.')[0]) {
  18 { 'Visual Studio 18 2026' }
  17 { 'Visual Studio 17 2022' }
  default { throw "Visual Studio $($vs.installationVersion) is not supported; use Visual Studio 2026 (or 2022)." }
}
if ($Arch -contains 'arm64') {
  $withArm64 = @(& $vswhere -products * -requires Microsoft.VisualStudio.Component.VC.Tools.ARM64 -property installationPath)
  if ($withArm64 -notcontains $vs.installationPath) {
    throw "$($vs.displayName) has no ARM64 compiler. Add ""MSVC ARM64 build tools"" in the Visual Studio Installer, or run with -Arch x64."
  }
}

# The CMake bundled with Visual Studio knows that version's generator; a CMake on the PATH may not.
$cmake = Join-Path $vs.installationPath 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
if (-not (Test-Path $cmake)) {
  $cmake = (Get-Command cmake -ErrorAction SilentlyContinue).Source
  if (-not $cmake) { throw 'CMake was not found. Add "C++ CMake tools for Windows" in the Visual Studio Installer.' }
}
Write-Host "Using $($vs.displayName) ($generator)"

& (Join-Path $PSScriptRoot 'fetch-libwebrtc.ps1') -Arch $Arch

# CMake looks for CMakePresets.json in the working directory.
Push-Location $root
try {
  foreach ($a in $Arch) {
    $preset = "win-$a"
    $build = Join-Path $root "out/build/$preset"
    # A build folder configured by another Visual Studio version can't be reused.
    $cache = Join-Path $build 'CMakeCache.txt'
    if ((Test-Path $cache) -and -not (Select-String -Quiet -SimpleMatch "CMAKE_GENERATOR:INTERNAL=$generator" $cache)) {
      Remove-Item -Recurse -Force $build
    }
    & $cmake --preset $preset -G $generator
    if ($LASTEXITCODE) { throw "cmake configure failed ($preset)" }
    & $cmake --build --preset $preset --config $Configuration
    if ($LASTEXITCODE) { throw "cmake build failed ($preset)" }
    & $cmake --install $build --config $Configuration --prefix (Join-Path $root "out/install/$a")
    if ($LASTEXITCODE) { throw "cmake install failed ($preset)" }
    Write-Host "rtc_shim $a -> $(Join-Path $root "out/install/$a")"
  }
}
finally {
  Pop-Location
}
