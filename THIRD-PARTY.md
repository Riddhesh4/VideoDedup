# Third-Party Components

Versioned external binaries and packages this solution depends on. Each entry records
the exact source downloaded from, per the Phase 0 licensing gate in PLAN-REVIEW.md
("no new GPL build artifact introduced").

| Component | Version | License | Source | Notes |
|---|---|---|---|---|
| FFmpeg + FFprobe (planned, Phase 4) | _pinned at adoption_ | LGPL-2.1-or-later | BtbN FFmpeg-Builds GitHub releases (**LGPL** variant, not gyan.dev) | Decode-only use; never enable GPL libs. Download step lives in CI from week one. |
| fpcalc / Chromaprint (planned, Phase 6) | 1.6.2 | LGPL-2.1-or-later | acoustid/chromaprint GitHub releases (`chromaprint-fpcalc-1.6.2-windows-x86_64.zip`) | Ship runtime files together; do not extract only the EXE. Auto-fetch via GitHub releases API. |
| SQLite (via Microsoft.Data.Sqlite) | 10.0.13 | MIT | nuget.org | Managed provider; SQLite itself is public domain. |
| .NET 10 SDK / runtime | 10.0.402 | MIT | dotnet.microsoft.com | Build tooling only. |
| Obfuscar.GlobalTool | 3.0.0-beta.22 | MIT | NuGet (`Obfuscar.GlobalTool`) | Release-build RE deterrent only; do not treat as a security boundary. |
| TngTech.ArchUnitNET.xUnitV3 | 0.13.4 | Apache-2.0 | NuGet; TNG/ArchUnitNET | Test-only architecture rules; requires Windows target because it inspects WPF assembly. |
| FluentAssertions | 7.2.0 | Apache-2.0 | NuGet; fluentassertions.com | Test-only assertions; pinned to Apache-2.0 release (v8+ has paid commercial-use terms). |
| dotnet-coverage | 18.12.0 | Microsoft .NET Library License | Local dotnet tool manifest | Development-only local coverage measurement; not included in app distribution. |
| System.IO.FileSystem.AccessControl | 5.0.0 | MIT | NuGet (dotnet/runtime) | Phase 2 JSON-bridge ACL hardening only; best-effort per-user LocalAppData ACLs. |

## Rules

1. **Only LGPL/MIT/public-domain artifacts may be committed or downloaded by CI.** Adding
   a GPL artifact (e.g. a gyan.dev FFmpeg build) requires a documented exception in DECISIONS.md.
2. Every new external binary gets a row here **in the same commit** that starts using it.
3. Pinned versions change only via PR with the reason recorded in DECISIONS.md.
