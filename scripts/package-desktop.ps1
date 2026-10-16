$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
dotnet publish src/VideoDedup.Launcher/VideoDedup.Launcher.csproj -c Release -o publish/desktop
if ($LASTEXITCODE -ne 0) { throw 'Desktop publish failed' }
# This checkpoint intentionally distributes no static FFmpeg executable. Users prepare
# their own pinned local tools; bundling requires the independent compliance gate below.
Copy-Item THIRD-PARTY.md publish/desktop/THIRD-PARTY.md -Force
Copy-Item docs/MEDIA-COMPLIANCE.md publish/desktop/MEDIA-COMPLIANCE.md -Force
if (Test-Path publish/desktop/media) { throw 'Unexpected bundled media directory; stop packaging for compliance review' }
Write-Host 'Desktop published without bundled FFmpeg/FFprobe; launch with --tools <prepared-media-directory>.'
