<#
.SYNOPSIS
  Downloads the pinned webrtc-sdk/libwebrtc release, verifies its SHA-256 and extracts it to
  third_party/libwebrtc/<arch>/.
.EXAMPLE
  ./fetch-libwebrtc.ps1               # x64 and arm64
  ./fetch-libwebrtc.ps1 -Arch x64
#>
param(
  [ValidateSet('x64', 'arm64')]
  [string[]] $Arch = @('x64', 'arm64')
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'  # Invoke-WebRequest is very slow with the progress bar

$root = Split-Path -Parent $PSScriptRoot
$lock = Get-Content -Raw (Join-Path $root 'libwebrtc.lock.json') | ConvertFrom-Json
$cache = Join-Path $root 'third_party/downloads'
New-Item -ItemType Directory -Force $cache | Out-Null

foreach ($a in $Arch) {
  $asset = $lock.assets.$a
  $destination = Join-Path $root "third_party/libwebrtc/$a"
  $stamp = Join-Path $destination '.sha256'
  if ((Test-Path $stamp) -and ((Get-Content -Raw $stamp).Trim() -eq $asset.sha256)) {
    Write-Host "libwebrtc $($lock.tag) $a is up to date"
    continue
  }

  $zip = Join-Path $cache $asset.file
  if (-not (Test-Path $zip) -or (Get-FileHash -Algorithm SHA256 $zip).Hash -ne $asset.sha256) {
    Write-Host "Downloading $($asset.file) ($($lock.tag))"
    Invoke-WebRequest -Uri ($lock.url + $asset.file) -OutFile "$zip.part"
    Move-Item -Force "$zip.part" $zip
  }

  $hash = (Get-FileHash -Algorithm SHA256 $zip).Hash.ToLowerInvariant()
  if ($hash -ne $asset.sha256) {
    Remove-Item -Force $zip
    throw "SHA-256 mismatch for $($asset.file): expected $($asset.sha256), got $hash"
  }

  if (Test-Path $destination) { Remove-Item -Recurse -Force $destination }
  Expand-Archive -Path $zip -DestinationPath $destination
  if (-not (Test-Path (Join-Path $destination "$($asset.folder)/lib/libwebrtc.dll"))) {
    throw "Unexpected archive layout in $($asset.file)"
  }
  Set-Content -NoNewline -Path $stamp -Value $asset.sha256
  Write-Host "libwebrtc $($lock.tag) $a -> $destination"
}
