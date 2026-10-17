# Third-Party Components

Versioned external binaries and packages this solution depends on. Each entry records
the exact source downloaded from, per the Phase 0 licensing gate in PLAN-REVIEW.md
("no new GPL build artifact introduced").

| Component | Version | License | Source | Notes |
|---|---|---|---|---|
| FFmpeg + FFprobe | n8.1.3-18-g46d899d7a0 | LGPL-3.0-or-later | Dated BtbN release; see pin below | Runtime -L and configuration audited; CI verifies archive hash before use. |
| fpcalc / Chromaprint | 1.6.2 | LGPL-2.1-or-later | Official acoustid/chromaprint GitHub release; see pin below | Static Windows x64 fpcalc archive contains only `fpcalc.exe`; CI verifies SHA-256 before extraction. |
| SQLite (via Microsoft.Data.Sqlite) | 10.0.13 | MIT | nuget.org | Managed provider; SQLite itself is public domain. |
| .NET 10 SDK / runtime | 10.0.402 | MIT | dotnet.microsoft.com | Build tooling only. |
| Obfuscar.GlobalTool | 3.0.0-beta.22 | MIT | NuGet (`Obfuscar.GlobalTool`) | Release-build RE deterrent only; do not treat as a security boundary. |
| TngTech.ArchUnitNET.xUnitV3 | 0.13.4 | Apache-2.0 | NuGet; TNG/ArchUnitNET | Test-only architecture rules; requires Windows target because it inspects WPF assembly. |
| FluentAssertions | 7.2.0 | Apache-2.0 | NuGet; fluentassertions.com | Test-only assertions; pinned to Apache-2.0 release (v8+ has paid commercial-use terms). |
| dotnet-coverage | 18.12.0 | Microsoft .NET Library License | Local dotnet tool manifest | Development-only local coverage measurement; not included in app distribution. |
| System.IO.FileSystem.AccessControl | 5.0.0 | MIT | NuGet (dotnet/runtime) | Phase 2 JSON-bridge ACL hardening only; best-effort per-user LocalAppData ACLs. |
| Microsoft.Extensions.Hosting | 10.0.13 | MIT | NuGet (dotnet/runtime) | Hosted worker lifetime in the launcher; no network or telemetry components. |
| xunit.v3 | 4.0.2 | Apache-2.0 | NuGet (xunit.net) | Test-only runner on Microsoft.Testing.Platform. |
| xunit.runner.visualstudio | 4.0.1 | Apache-2.0 | NuGet (xunit.net) | Test-only adapter. |
| Microsoft.NET.Test.Sdk | 17.14.1 | MIT | NuGet (Microsoft) | Test-only host. |
| coverlet.collector | 10.1.0 | MIT | NuGet (coverlet) | Test-only coverage collector. |
| NSubstitute | 6.2.0 | BSD-3-Clause | NuGet (nsubstitute) | Test-only doubles for boundary fakes. |
| Reqnroll | 3.3.4 | BSD-3-Clause | NuGet (reqnroll) | BDD feature parser/runner (test-only). |
| Reqnroll.xunit.v3 | 3.3.4 | BSD-3-Clause | NuGet (reqnroll) | BDD integration with the xUnit v3 runner (test-only). |

## Phase 6 fpcalc pin

Official Chromaprint v1.6.2 Windows x86_64 release asset, published 2026-10-15:
https://github.com/acoustid/chromaprint/releases/download/v1.6.2/chromaprint-fpcalc-1.6.2-windows-x86_64.zip
SHA-256 (GitHub asset digest): `b74369726676670af3ab0eadff45f960cf969fdf4a87a971ba37eaf8d530e874`.

The archive was inspected and contains only `chromaprint-fpcalc-1.6.2-windows-x86_64/fpcalc.exe` (4,148,224 bytes). Chromaprint's v1.6.2 LICENSE.md classifies the library as LGPL-2.1-or-later due to FFmpeg components; its binary notices/source obligations require separate distribution review. This pin does not certify redistribution compliance.

## Rules

1. **Only LGPL/MIT/public-domain artifacts may be committed or downloaded by CI.** Adding
   a GPL artifact (e.g. a gyan.dev FFmpeg build) requires a documented exception in DECISIONS.md.
2. Every new external binary gets a row here **in the same commit** that starts using it.
3. Pinned versions change only via PR with the reason recorded in DECISIONS.md.
4. `scripts/check-dependencies.ps1` fails the release pipeline when a package is declared without a row here
   (allow-list) or when a central version is not an exact pin. It runs offline and is part of CI.

## Phase 4 pinned media tools

BtbN `n8.1.3-18-g46d899d7a0`, Windows x64 static LGPL variant.
Archive: https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-10-15-13-03/ffmpeg-n8.1.3-18-g46d899d7a0-win64-lgpl-8.1.zip
SHA-256: `dc725e2f9b1c6d829c1d0a2ceffa28bffb3b54468e972f7467a38021ff6032aa` (GitHub asset digest; locally verify before extraction).
License: LGPL-3.0-or-later; retain archive LICENSE and notices. Runtime build configuration must contain no `--enable-gpl` or `--enable-nonfree`. Downloaded tools live in ignored `tools/media`; no executable is committed. Build configuration/license audit evidence is recorded after verification.

Verified executable SHA-256: ffmpeg.exe `3b57117b384df98f2b8d01876df0a596a0b6ebf45f756b30f15d6f65ab8251c5`; ffprobe.exe `5b9724ee7a115d5a8458d41bd2e0ce6206847e2b76105e822061a4421322cde9`. Both report LGPL version 3 or later via `-L`; configuration enables version3 and omits GPL/nonfree enabling flags. Static archive has no runtime DLLs; retain LICENSE.txt. Distribution must include required LGPL/GPLv3 incorporated license text and corresponding source/relink compliance; this development setup is not a packaging/legal compliance certification.

- Microsoft.Extensions.Hosting 10.0.13, MIT, NuGet/dotnet runtime: hosted worker lifecycle. Desktop packaging excludes FFmpeg/FFprobe; see docs/MEDIA-COMPLIANCE.md for the redistribution gate.
