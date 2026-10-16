# Phase 4 media-tool compliance boundary

## Shipping policy

The desktop checkpoint **does not bundle FFmpeg or FFprobe**. `scripts/package-desktop.ps1` publishes the app without a media directory and fails if one is present. Supply locally prepared tools using `VideoDedup.Launcher.exe --tools <absolute-directory>`; `scripts/prepare-media.py` verifies the pinned upstream archive before use. CI downloads tools solely for testing; no tool executable is checked into Git or uploaded as a release artifact.

This removes static FFmpeg redistribution from this checkpoint rather than asserting that a license checklist is legal certification. No public binary release or upload is authorized by a source-code push.

## Audit evidence

- Archive and executable digests: `scripts/media-tools.json` and `THIRD-PARTY.md`.
- Both executables report GNU LGPL version 3-or-later using `-L`.
- Both configurations omit `--enable-gpl` and `--enable-nonfree`; `--enable-version3` is present.
- The archive supplies `LICENSE.txt`; the preparation script retains it and configuration output.
- Tools are separate subprocesses, never linked into managed application assemblies.
- Microsoft.Extensions.Hosting 10.0.13 is MIT, from NuGet/dotnet runtime.

## Gate before any future static-tool bundle

A release owner must supply and verify: exact corresponding source for **all linked libraries**, patches and build scripts/toolchain requirements, LGPLv3 plus incorporated GPLv3 license texts, copyright notices for linked components, applicable installation/relink information and object code or another compliant mechanism, and a distribution method that fulfills the license's source/access obligations. A moving GitHub release URL or a license file alone is not sufficient.

No checked-in manifest currently asserts these requirements are satisfied. The package therefore excludes the static binaries. Counsel/release-owner approval cannot be manufactured by automated tests. Switching to shared libraries is not automatically a compliance exemption and would require another reviewed artifact pin.

## Security limits

Volume serial/file ID and Windows file change time reject ordinary same-size/mtime replacement and in-place timestamp restoration on NTFS. Older jobs lacking strong evidence are not accepted by the integrated path. An administrator or kernel-level attacker controlling the filesystem can forge evidence; no local desktop program can establish trust against its machine owner. Source media is never modified by processing.
