# Architecture Plan Review — Video Dedup Scanner

Review date: 2026-10-15. Source: "Create a architecture plan for the solution select the best language and backend and protocol…" (PDF).

**Verdict: Approve with amendments.** The plan's fundamentals are right — local-first .NET desktop app, SQLite as the single store of truth, FFmpeg for media access, worker pipeline, contract-first testing. Nothing needs a rewrite. But it was written against stale assumptions: three tooling facts have changed, two library choices risk dead-ends, and one licensing decision it treats as an afterthought can sink distribution. Corrections below are checked against current sources.

---

## 1. Tooling corrections (keep the plan current)

| Plan says | Reality (Oct 2026) | Amend to |
|---|---|---|
| ".NET 9 or .NET 10 LTS" | .NET 9 (STS) is already out of support; .NET 10 is the current LTS. .NET 8 EOL is Nov 10, 2026. | ".NET 10 LTS" — remove .NET 9 as an option. |
| "xUnit v3" | xUnit v3 is stable and recommended; note the v3 *generation* now ships 4.x package versions (released Oct 9, 2026). Pin `xunit.v3` packages; VSTest integration needs `xunit.runner.visualstudio` 3.x+. FluentAssertions 8+ also requires a paid commercial-use license; 7.2.0 remains Apache-2.0. | Keep xUnit v3 generation; pin versions in `Directory.Packages.props`; use FluentAssertions 7.2.0 unless commercial licensing is approved. |
| Reqnroll + xUnit | Reqnroll is at 3.x; xUnit v3 support ships as the separate `Reqnroll.xunit.v3` package since 3.1. | Use `Reqnroll.xunit.v3` explicitly; plan's generic "Reqnroll + xUnit" will resolve to the wrong runner otherwise. |
| "NetArchTest or ArchUnitNET" | NetArchTest.Rules is stagnant (last 1.3.2, 2021-era API); ArchUnitNET is actively maintained (0.13.x, Aug 2026) with an xUnit v3 integration package (`TngTech.ArchUnitNET.xUnitV3`). | Pick **ArchUnitNET + its xUnitV3 package**. Don't list NetArchTest as an option; it's a dead end for a new project. |

## 2. Media stack corrections

### 2.1 Chromaprint — simplify Phase 6

- The official Windows `fpcalc` ZIP exists on GitHub (`chromaprint-fpcalc-1.6.2-windows-x86_64.zip`, v1.6.2, 2026-10-15). The AcoustID download *page* lags behind GitHub releases — build any auto-fetch against the **GitHub releases API**, not the marketing page.
- The plan's Phase 6 says "Extract mono PCM audio using FFmpeg, then segment fingerprints." That's a design leftover from raw Chromaprint use. **`fpcalc` accepts the video file directly** (it uses its bundled FFmpeg internally) and emits JSON. Drop the hand-rolled PCM-extraction step for v1: invoke `fpcalc -json` on the file, parse, store. Segment-based partial-clip matching still needs a following phase, but get the simple whole-file fingerprint path working first.
- Shipping note: keep the extracted runtime files together — extracting only `fpcalc.exe` is known to be insufficient.
- `fpcalc` fingerprints **audio**, nothing else. It is not a substitute for the visual pipeline; keep the two workers separate as planned.

### 2.2 Visual fingerprints — don't commit to OpenCvSharp4

- The plan lists "OpenCVSharp + pHash". OpenCvSharp4 is now officially in **maintenance mode**; **OpenCvSharp5** is the actively developed line (and targets .NET 8+). If OpenCV is used at all, use OpenCvSharp5 and verify the `img_hash` module bindings exist in the wrapper you ship — the native OpenCV module existing does not guarantee the C# binding does.
- Better option for this project's actual need: the hash algorithms (aHash/dHash/pHash) are small and pure. Options in order:
  1. **In-house pHash in Domain/Infrastructure** fed by FFmpeg-decoded grayscale frames — ~100 lines, fully testable, zero extra native dependency, no license questions.
  2. CoenM.ImageHash (`CoenM.ImageSharp.ImageHash`) implements the three hashes but the NuGet package is from 2022 — usable, but if it's the pick, plan to vendor/fork it eventually.
  3. OpenCvSharp5 — only if other OpenCV features (frame quality scoring, motion detection) get added later.
- ImageSharp 4 (which CoenM's package depends on) now applies a **build-time license key for direct dependents** in commercial projects. If the app is closed-source/commercial, that's a real cost. An FFmpeg-fed in-house hash sidesteps the entire question, since frames come as raw buffers.

### 2.3 FFmpeg — the license decision is not an afterthought

- The plan never mentions licensing. Concretely:
  - **gyan.dev** "essentials/full" builds are **GPLv3** — redistributing them with your app makes the whole distribution GPL-obligated.
  - **BtbN** publishes **GPL and LGPL variants**; the LGPL variant omits GPL-only libs (libx264/x265) — which this scanner doesn't need anyway (it decodes; it never encodes).
- Separate-process invocation of `ffmpeg.exe` is **not** a licensing exemption — don't let anyone tell you it is.
- Amend: use **BtbN LGPL build**, decode-only, pinned version, sparse copy (you need ffmpeg + ffprobe only), with a `THIRD-PARTY.md` listing the pinned sources. Decide this in Phase 0, not Phase 4, because `THIRD-PARTY.md` and build download steps belong to CI from day one.

## 3. SQLite corrections — the queue claims are right but under-specified

The plan's durable queue design (atomic claim, retries, poison jobs, no-duplicate active job) is correct, but as written it will deadlock under concurrency unless amended:

- Use **`BEGIN IMMEDIATE`** for claim transactions. Plain deferred transactions on WAL can still serialize-conflict when two workers race to claim the same row.
- Set `busy_timeout` (≥ 5 s) on every connection, enable WAL mode once at startup, and use `PRAGMA foreign_keys = ON`.
- Consider a **single-writer discipline** for queue mutation (all writes funnel through one connection or a serialized lock) while workers read from separate connections. It's the cheapest way to make Phase 3's concurrency tests pass deterministically instead of flakily.
- Choose **Microsoft.Data.Sqlite** as the ADO provider (maintained by MS, pairs well with Dapper or plain ADO). The plan doesn't name a provider — name it.

## 4. Things the plan gets right (don't touch)

- Local-first single-process desktop app — right; named pipes/gRPC are correctly deferred as optional.
- Never-delete-matcher → human review with quarantine as default and undo log — right and under-invested; it's the product's core trust feature.
- The corner-case lists (file-system, media, matching) are unusually complete; keep them as the unit/BDD coverage tracking source.
- `Directory.Build.props` with `TreatWarningsAsErrors`, central package versions, deterministic small media fixtures generated by script — all good hygiene.
- Phase ordering and per-phase definitions of done are coherent.

## 5. Gaps to add

0. **Reverse-engineering hardening (added 2026-10-15, per user requirement)** — raise
   the cost of casual inspection, but do not promise that a client is impossible to
   reverse engineer:
   - **Obfuscar.GlobalTool 3.0.0-beta.22 (MIT, free)** is currently pinned in the local
     tool manifest and run by `scripts/obfuscate.ps1`. The script generates the Obfuscar
     project with absolute paths (required by this beta), then swaps obfuscated outputs
     into `publish/`. Keep unobfuscated intermediates out of release artifacts. WPF
     XAML, bindings, reflection, and serialized members need explicit preservation rules
     and a packaged Release smoke test; the current CI smoke checks only selected names,
     not all application behavior.
   - Treat obfuscation, string hiding, and `SuppressIldasm` as deterrents only. A local
     administrator/owner can inspect, patch, or replace the application. Do not rely on
     the client to enforce authorization or protect embedded secrets.
   - NativeAOT is **not an approved WPF path at .NET 10**; ReadyToRun retains IL and is
     not concealment. Neither is a security control here.
   - Strongest design rule: **ship no secrets in the client**. Current v1 has no cloud
     credentials or licensing secret; per-user local data is not a secret boundary.
     Protect local data with user-profile ACLs and minimize sensitive logs.
1. **Drive identity is the hard problem, not "DriveId"** — the plan stores `VolumeSignature`, but USB enclosures can change volume signatures across ports/hubs in practice. Amend `StableFileIdentity` to use (volume signature OR size+modifiedTime+path-hash tiebreaker) with a documented precedence, and add a BDD scenario for "same drive, signature mismatch, path matches" → NeedsReview instead of a new file record.
2. **Network shares are the v1 scope creep** — `\\NAS\Media` scanning (UNC enumeration over SMB) drags in auth, latency, and unavailability semantics that USB paths don't. Cut network shares from Phase 2's definition of done to "discoverable but not scanned by default"; ship local + USB in v1.
3. **Hashing with no FFmpeg coverage for exotic codecs** — some camera formats (10-bit H.265, RAW variants) decode slowly or fail. Add a codec-support probe to ProbeWorker that marks such files `Unsupported` early instead of letting them burn retry cycles in the two fingerprint workers.
4. **Kill/a11y checks for the workers UI** — the plan has no concept of "what does the user see when a scan is stalled?" Amend Phase 8 DoD: queue depth and per-worker state visible, and a Pause/Resume that works by canceling hosted services, not killing processes.
5. **Definition of done template should include the licensing check** — "no new GPL build artifact introduced" as an explicit gate.
6. **Reqnroll step-classes naming convention** — define upfront (e.g. `FeatureXyz_Steps` in `tests/VideoDedup.Bdd.Tests/Steps/`); Reqnroll generates binding classes and an unplanned convention creates friction later.

## 6. Amended decision list (what to record in DECISIONS.md)

| # | Decision | Status vs. plan |
|---|---|---|
| 1 | .NET 10 LTS (not 9, not 8 — 8 EOLs Nov 2026) | Amended |
| 2 | WPF + MVVM, .NET Generic Host + BackgroundService workers | Unchanged |
| 3 | SQLite via **Microsoft.Data.Sqlite**, WAL + `busy_timeout` + `BEGIN IMMEDIATE` claiming, single-writer discipline | Amended (provider + transaction discipline added) |
| 4 | FFmpeg/FFprobe from **BtbN LGPL build**, decode-only, pinned; `THIRD-PARTY.md` | Amended |
| 5 | Chromaprint via official **fpcalc 1.6.2** Windows ZIP fetched from GitHub releases, `fpcalc -json` direct on the file; drop separate PCM extraction for v1 | Amended |
| 6 | Visual fingerprints: in-house pHash on FFmpeg-decoded grayscale frames (Domain-tested); OpenCvSharp5/CoenM/ImageSharp deferred | Amended |
| 7 | Tests: xUnit v3, FluentAssertions, NSubstitute, **Reqnroll.xunit.v3**, **ArchUnitNET.xUnitV3** | Amended tooling |
| 8 | No cloud, no gRPC in v1; network shares discoverable-but-not-scanned by default | Amended |
| 9 | Quarantine-default + undo log + last-copy protection | Unchanged (raised to non-negotiable) |
| 10 | No client secrets; Obfuscar 3.0.0-beta.22 is only RE friction; WPF package smoke required; no AOT/ReadyToRun concealment claims | Added |

## 7. Sources checked

- Microsoft .NET support policy and downloads — dotnet.microsoft.com
- Reqnroll releases and NuGet (`Reqnroll.xunit.v3`) — github.com/reqnroll, nuget.org
- xUnit v3/4 releases and NuGet package guide — xunit.net
- NetArchTest.Rules NuGet; ArchUnitNET releases + `TngTech.ArchUnitNET.xUnitV3` NuGet
- Chromaprint v1.6.2 GitHub release assets; AcoustID download page; beets fpcalc setup docs
- OpenCvSharp repo status (4 maintenance / 5 active); CoenM.ImageHash NuGet + repo; ImageSharp 4 license-enforcement notice
- FFmpeg download page; gyan.dev builds (GPLv3); BtbN FFmpeg-Builds (GPL/LGPL variants); FFmpeg legal/compliance page

*This is an engineering-architecture review, not legal advice; confirm distribution licensing with counsel if commercial.*
