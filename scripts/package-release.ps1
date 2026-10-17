param([string]$Version = '0.9.0-preview')
# Builds the tester deliverable: the published WPF app (no bundled media tools), the compliance and privacy docs,
# and a README with the tester checklist, zipped with a SHA-256 next to it.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

& (Join-Path $PSScriptRoot 'package-desktop.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Desktop packaging failed' }

$release = Join-Path $root 'release'
$staging = Join-Path $release "VideoDedup-$Version-win-x64"
$zip = Join-Path $release "VideoDedup-$Version-win-x64.zip"
Remove-Item $staging, $zip, "$zip.sha256" -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $staging, (Join-Path $staging 'docs') | Out-Null

Copy-Item (Join-Path $root 'publish/desktop/*') $staging -Recurse -Force
Copy-Item (Join-Path $root 'THIRD-PARTY.md') $staging -Force
foreach ($document in @('PRIVACY.md', 'THREAT-MODEL.md', 'RELEASE.md', 'MEDIA-COMPLIANCE.md', 'BENCHMARKS.md')) {
    Copy-Item (Join-Path $root "docs/$document") (Join-Path $staging 'docs') -Force
}

$readme = @'
VideoDedup tester build
=======================

This is a portable build: unzip it anywhere and run VideoDedup.Launcher.exe. There is no installer and no registry
entry. The build contains NO media tools; prepare them once from the source checkout:

    python scripts/prepare-media.py
    .\VideoDedup.Launcher.exe --tools <checkout>\tools\media --audio-tools <checkout>\tools\audio

What to test (please report pass/fail for each, with the exact wording of any message):

1.  Start the app. Printing, Review and Privacy-and-data tabs are present; no crash.
2.  Processing: enter a folder with at least two copies of one short video, click "Scan and process".
    Rows reach Complete and the worker grid shows Probe/Hash/Visual/Audio/Match.
3.  Wait a moment, click Refresh. Duplicate groups appear on Review (matching finishes after the fingerprints).
4.  Review: select a group, load aligned previews, use Keep and Reject.
5.  Quarantine a group with more than one copy and confirm. Undo last restores it. Quarantining the last copy is
    refused with a reason.
6.  Privacy and data: check the storage table, the data folder path and the retention notes.
7.  Export the index twice: with and without "Include file names and paths". The second file must contain no file
    names or paths ("path": null, "pathRedacted": true).
8.  Delete index: decline once ("canceled; nothing was removed"), then confirm. Quarantined copies stay on disk.
9.  Your source videos are untouched and still play.
10. Report file count, total size, scan duration and Task Manager peak memory.

Full details: docs/RELEASE.md, docs/PRIVACY.md, docs/THREAT-MODEL.md. No code signature is applied, so a SmartScreen
warning on first run is expected; verify the .sha256 next to the zip instead.
'@
Set-Content -Path (Join-Path $staging 'README-TESTER.txt') -Value $readme -Encoding utf8

if (-not (Test-Path (Join-Path $staging 'VideoDedup.Launcher.exe'))) { throw 'The release staging folder has no launcher executable.' }
Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zip -CompressionLevel Optimal
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $(Split-Path -Leaf $zip)" | Set-Content -Path "$zip.sha256" -Encoding ascii
Remove-Item $staging -Recurse -Force

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($zip)
try {
    $entries = $archive.Entries.Count
    $hasLauncher = @($archive.Entries | Where-Object { $_.Name -eq 'VideoDedup.Launcher.exe' }).Count -eq 1
} finally { $archive.Dispose() }
if (-not $hasLauncher) { throw 'The release zip does not contain VideoDedup.Launcher.exe.' }
$sizeMiB = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "Release built: $zip ($sizeMiB MiB, $entries entries)"
Write-Host "SHA-256: $hash"
