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

## Pending decisions

- Exact FFmpeg and fpcalc artifact versions/hashes must be pinned when introduced in their implementation phases and recorded in `THIRD-PARTY.md`.
- Confirm commercial distribution/licensing with counsel before publishing bundled LGPL media binaries; this log is not legal advice.
- Windows code signing certificate and signing pipeline are not selected; unsigned releases must be labeled as such.
