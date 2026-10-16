# Architecture Decisions

Decisions are append-only. To change one, add a dated superseding decision with rationale and migration impact.

## Accepted decisions (2026-10-15)

1. **Runtime/framework:** .NET 10 LTS, C#, WPF + MVVM, .NET Generic Host. NativeAOT is not an approved WPF deployment strategy; ReadyToRun is not a protection mechanism.
2. **Process model:** Single local desktop app; no cloud backend or IPC in v1. Add local named pipes only if a later service split has a concrete requirement.
3. **Persistence/queue:** SQLite via Microsoft.Data.Sqlite. WAL, busy timeout >= 5 seconds, FK enforcement, `BEGIN IMMEDIATE` claims, serialized writes; durable, resumable queue.
4. **Media:** Pinned BtbN LGPL FFmpeg/FFprobe build, decode-only; Chromaprint via pinned fpcalc Windows release; in-house pHash. No GPL/nonfree distribution artifacts without a superseding licensing review.
5. **Matching:** Exact SHA-256, then explainable visual/audio near-match and later partial-clip matching. Ambiguous cases go to manual review. Matching never deletes files.
6. **Safety:** Quarantine-first, explicit confirmation, undo log, and last-copy protection. Never automate permanent deletion.
7. **Testing:** xUnit v3 + MTP, Reqnroll.xunit.v3, FluentAssertions 7.2.0 (Apache-2.0; avoid v8+ paid commercial terms), NSubstitute, ArchUnitNET. Release build has warnings-as-errors and zero-test runs are failures. Domain line coverage is measured with local `dotnet-coverage` (Microsoft .NET Library License, dev-only).
8. **Privacy/security:** Local-only processing and per-user storage; no telemetry in v1; no secrets embedded in desktop binaries. Treat files, paths, tool output, and metadata as untrusted. Use argument-list process invocation, resource limits, cancellation, and ACL-protected app data.
9. **Reverse-engineering posture:** Pin Obfuscar for Release obfuscation to raise the cost of casual inspection. It is friction, not security: assume an owner/admin can reverse engineer or patch the client. Keep no secret or decisive trust boundary in client code. Don't claim AOT, ReadyToRun, string hiding, or obfuscation makes code impossible to inspect. Preserve WPF XAML/reflection behavior and test packaged Release output.
10. **AI-assisted development:** `PHASE-PROMPTS.md` is the canonical prompt library. Every prompt is bounded, references `ARCHITECTURE.md`, requires tests and explicit Definition of Done, and does not ask the model to reread/regenerate the whole repository. `.cursor/skills/vibecoding-context/SKILL.md` is the short entry point; update its Progress section at each phase boundary.
11. **Phase 2 persistence bridge:** Discovery records use an atomic per-root JSON snapshot in the current user's LocalAppData until the Phase 3 SQLite schema/repository replaces it. This is a deliberately temporary store; writes use temp-file replacement and source media is never modified.

## Phase 8 decisions (2026-10-16)

12. **Quarantine is the only file-moving action, and it is intent-first.** Nothing moves without `confirmed: true` from the caller (in the app, an explicit Yes/No dialog). A durable `quarantine_log` row is written *before* the move, so an interruption is always explainable; `RecoverInterruptedAsync` reconciles leftover `Pending` rows at startup and flags quarantined rows whose copy vanished. Permanent deletion is not implemented at all.
13. **Quarantine destination.** Prefer a same-volume `VideoDedup Quarantine` folder, but only when that folder sits outside every configured scan root (otherwise the scanner would re-index the quarantined file); otherwise use `<LocalAppData>\VideoDedup\Quarantine\<volume>-<file>` under the per-user ACL-protected store and warn in the confirmation dialog that the move copies across drives. Destinations are collision-safe (numbered suffixes) and never overwrite.
14. **Last-copy protection is content-based and fail-safe.** A file is only movable when another indexed copy with the same SHA-256 is currently present on disk, excluding already-quarantined records. Unknown content state counts as "no other copy", and a confirmed batch re-evaluates each item sequentially, so quarantining both copies of a pair in one batch cannot empty the group.
15. **Match component scores are persisted.** `match_results` gained nullable `VisualScore`/`AudioScore` columns through an additive in-place migration, because the review UI must show visual, audio, and overall scores without recomputing evidence. Existing rows keep NULL.
16. **Manual decisions are keyed by (duplicate group, file).** A group is the single-linkage cluster of decided pairs; its key is the ordinally sorted, `|`-joined file IDs. A later re-cluster that changes group membership therefore starts from undecided again rather than inheriting a stale decision.
17. **No new UI dependency.** The desktop keeps MVVM hand-rolled (`ObservableObject`, `RelayCommand`, `AsyncRelayCommand`) instead of adding CommunityToolkit.Mvvm, and `IConfirmationPrompt` is a desktop-side port so "nothing moves without confirmation" stays testable. All display text passes through `DisplayText.Sanitize` and is rendered as plain text, never markup.
18. **Pause/resume semantics.** `WorkerStatusRegistry` is the single pause gate: pausing stops new claims but lets a leased job finish (or be recovered by lease expiry), so pausing never abandons half-processed work. Worker failures are reported to the dashboard as `ExceptionType: MediaFailure` only, so no message can leak a full local path.
19. **Preview frames are bounded and advisory.** `FfmpegFramePreviewSource` decodes one scaled MJPEG frame per request with the shared output/CPU/memory limits, verifies the file revision before and after, and returns "no preview" instead of throwing so a broken preview never blocks reviewing a group.

## Pending decisions

- Exact FFmpeg and fpcalc artifact versions/hashes must be pinned when introduced in their implementation phases and recorded in `THIRD-PARTY.md`.
- Confirm commercial distribution/licensing with counsel before publishing bundled LGPL media binaries; this log is not legal advice.
- Windows code signing certificate and signing pipeline are not selected; unsigned releases must be labeled as such.

## Phase 4 approved scope expansion

User approved integrated desktop checkpoint: hosted processing, automatic discovery enqueue, decoder inventory, Windows file-ID/change-time evidence and minimal WPF dashboard. VideoDedup.Launcher is the composition root; Desktop still depends only on Application. Infrastructure adapts the Application file-access port. Microsoft.Extensions.Hosting 10.0.13 (MIT) supplies hosted-service lifecycle. Static FFmpeg redistribution is excluded from the checkpoint and packaging fails on a bundled media directory; see docs/MEDIA-COMPLIANCE.md.
