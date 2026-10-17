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
if (Test-Path publish/desktop/audio) { throw 'Unexpected bundled fpcalc directory; stop packaging for compliance review' }
# One entry point only. A second executable in the package is a real failure mode, not cosmetic: a tester who picks
# the wrong one gets an invisible process with no window and reports that the application does not run.
$executables = @(Get-ChildItem publish/desktop -Filter *.exe -File | Select-Object -ExpandProperty Name)
if ($executables.Count -ne 1 -or $executables[0] -ne 'VideoDedup.Launcher.exe') {
    throw "The package must contain exactly one executable, VideoDedup.Launcher.exe; found: $($executables -join ', ')"
}
Write-Host 'Desktop published without bundled media binaries; launch with --tools <prepared-media-directory> --audio-tools <prepared-audio-directory>.'
