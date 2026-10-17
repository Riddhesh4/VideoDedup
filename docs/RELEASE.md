# Release and tester builds

## What ships

`scripts/package-desktop.ps1` publishes the WPF launcher for `win-x64` into `publish/desktop`.
`scripts/package-release.ps1` turns that into `release/VideoDedup-<version>-win-x64.zip` plus a `.sha256` file.

The package deliberately contains **no** FFmpeg, FFprobe or fpcalc binaries: those are separate LGPL downloads with
their own redistribution obligations (`docs/MEDIA-COMPLIANCE.md`). What the zip does contain:

| Item | Purpose |
| --- | --- |
| `VideoDedup.Launcher.exe` + application assemblies | The application |
| `THIRD-PARTY.md` | Every external binary and package with version, license and source |
| `docs/MEDIA-COMPLIANCE.md`, `docs/PRIVACY.md`, `docs/THREAT-MODEL.md` | Compliance and privacy documentation |
| `README-TESTER.txt` | The steps below, in the package |

## Preparing the media tools (tester and CI)

```powershell
python scripts/prepare-media.py     # downloads the pinned LGPL tools, verifies SHA-256, generates fixtures
```

Then either copy `tools\media` and `tools\audio` next to `VideoDedup.Launcher.exe` in the unzipped package, or launch
with explicit tool paths:

```powershell
.\VideoDedup.Launcher.exe --tools tools\media --audio-tools tools\audio
```

The copy is enough because the launcher defaults to `<application folder>\media` and `<application folder>\audio`;
an `audio` folder keeps its inner `chromaprint-fpcalc-1.6.2-windows-x86_64\fpcalc.exe` path. This keeps the distributed
zip tool-free for the LGPL gate while letting a tester set it up with a file copy. `scripts/desktop-smoke.ps1` accepts
`-ToolsDirectory ''` to verify exactly that argument-free start.

Without the tools the application still starts and the privacy panel works, but probe/hash/visual/audio stages fail
with a typed tool failure and matching has nothing to compare. `--data <folder>` is supported for an isolated test
profile; the default is `%LOCALAPPDATA%\VideoDedup`.

## Install and removal

There is no installer and no registry entry: unzip and run. To remove everything, use **Privacy and data → Delete
index** (which keeps quarantined copies and scan-root settings and says so), restore or delete the quarantined copies
in the review tab, then delete the application folder and the data folder.

## Obfuscation: what is and is not applied

- The shipped application assemblies are **not obfuscated**. Obfuscation is friction, not a security boundary
  (`DECISIONS.md`), and renaming the UI-facing assemblies breaks WPF: `x:Class`, resource URLs inside compiled BAML
  and `{Binding}` paths are resolved by name at runtime and Obfuscar does not rewrite BAML. A full-app obfuscated
  build was measured and fails at startup in two distinct ways (see the Phase 9 decision record), so the package
  ships unobfuscated on purpose rather than shipping something that does not start.
- CI still runs the Domain obfuscation gate: it publishes `VideoDedup.Domain.dll`, obfuscates it, and fails if
  recognizable type names survive. That gate proves the tooling and the marker-stripping work and keeps an accidental
  name leak from going unnoticed.

## Code signing

The build is **not code-signed**. There is no publisher identity, so Windows SmartScreen will warn on first run of a
downloaded zip and the "unknown publisher" prompt is expected. Signing requires a certificate authority the project
does not have; no signature is fabricated. Verify the downloaded archive against the published `.sha256` instead.

## Tester checklist

Please report each item as pass or fail, with the exact wording of any message you see.

1. Unzip the archive, prepare the tools, and start the app. Expected: the window opens with Processing, Review and
   Privacy and data tabs; no crash.
2. On **Processing**, enter a folder that contains at least two copies of the same video file (a few seconds each is
   enough) and click **Scan and process**. Expected: rows appear and reach `Complete`; the worker grid shows
   Probe/Hash/Visual/Audio/Match.
3. Wait a moment and click **Refresh**. Expected: duplicate groups become available on the **Review** tab even though
   the fingerprints finished after the scan started. Please report how long this took.
4. On **Review**, select a group, load the aligned previews, then use **Keep** and **Reject**. Expected: the copy list
   shows your decisions.
5. Use **Quarantine** on a group with more than one copy and confirm the dialog. Expected: the copy moves to the
   quarantine folder, the history grid shows it, **Undo last** puts it back at its original path. Try to quarantine
   the last remaining copy: it must be refused with a reason.
6. On **Privacy and data**, check the storage table and the notice. Expected: sizes, the data folder path, and the
   note that quarantined copies and scan-root settings are kept.
7. Export the index twice: once with **Include file names and paths** checked, once unchecked. Expected: two JSON
   files; in the second, `path` is `null` with `pathRedacted: true` and no file name appears anywhere in the file.
8. Click **Delete index**, decline once (expected: "canceled; nothing was removed"), then confirm. Expected: a count
   of deleted files, a count of kept entries, the quarantined copies still on disk, and the Processing tab empty
   afterwards.
9. Confirm your source videos are untouched and still play.
10. Please also report: roughly how many files you scanned, total size, how long the scan took, and peak memory from
    Task Manager, so the numbers in `docs/BENCHMARKS.md` can be compared with real library sizes.
