# VideoDedup Phase Tracker

Source prompts: [`PHASE-PROMPTS.md`](PHASE-PROMPTS.md). Project rules: [`ARCHITECTURE.md`](ARCHITECTURE.md), [`DECISIONS.md`](DECISIONS.md), and the project skill at [`.cursor/skills/vibecoding-context/SKILL.md`](.cursor/skills/vibecoding-context/SKILL.md).

## State

- [x] Phase 0 — Solution foundation, CI, smoke tests, and project docs. Done and CI green at commit `291140a`.
- [x] Phase 1 — Domain model and matching policy. Evidence: Release build 0 warnings/0 errors; full solution 39/39 tests pass (33 Domain, 2 ArchUnitNET, 4 other unit, 1 BDD); Domain line coverage 96.53%, branch coverage 89.49%; 90% minimum enforced in CI. See phase exit record below.
- [x] Phase 2 — Scan roots and file discovery. Evidence and remaining adapter/persistence boundary are recorded below.
- [x] Phase 3 — Durable SQLite job queue. Evidence and exit record below.
- [ ] Phase 4 — FFprobe metadata and exact SHA-256 duplicates.
- [ ] Phase 5 — Visual fingerprints.
- [ ] Phase 6 — Chromaprint audio fingerprints.
- [ ] Phase 7 — Match engine (near/partial/cross-root).
- [ ] Phase 8 — WPF review UI and safe quarantine/undo actions.
- [ ] Phase 9 — Resilience, performance, privacy, and release hardening.

## Phase exit record

### Phase 1

- Commit SHA: pending commit (local checks are green; remote CI runs on push).
- Build/test results: `dotnet build VideoDedup.slnx -c Release --no-restore` succeeded, 0 warnings/0 errors. `dotnet test VideoDedup.slnx -c Release --no-build -p:TestingPlatformShowTestsFailure=true -p:TestingPlatformCaptureOutput=false`: 39 passed, 0 failed, 0 skipped. Domain-specific: 33 passed. Architecture: 2 passed.
- Coverage: `dotnet-coverage` Cobertura result in ignored `TestResults/Domain-Coverage.cobertura.xml`: Domain line 96.53%, branch 89.49%. CI enforces a >=90% Domain line coverage minimum via `scripts/check-domain-coverage.ps1`.
- Security/privacy checks: Domain stays BCL-only; stable identity rejects rooted/traversal/control-character relative paths and treats signature changes/uncertain rename as NeedsReview; score policy is deterministic and has no destructive behavior; ArchUnitNET confirms Domain has no references to other solution assemblies and Desktop has no Infrastructure dependency. Test dependency licensing checked: FluentAssertions pinned to Apache-2.0 7.2.0; ArchUnitNET Apache-2.0; local coverage tool license noted in THIRD-PARTY.md. Release Domain obfuscation passed the CI marker check.
- BDD/edge cases added: Domain unit tests cover drive detail and root validation, all `VideoFile` scan-state transitions, unknown probe metadata, identity revision/signature/rename cases, input bounds, audio payload defensive-copy, exact/near/borderline/conflicting/no-evidence match decisions, and scoring invariants. Existing BDD smoke scenario continues to pass.
- Known limitations/deferred items: identity comparison intentionally uses volume signature + relative path + size + modified time and cannot prove content sameness (SHA-256 is required). No partial-clip decision is implemented. Local checks pass; remote CI will run after the requested push.

### Phase 2

- Commit SHA: `3d67e0a` (CI `ci` #6 success on `main`).
- Build/test results: Release build 0 warnings/0 errors. Full solution suite: 59 passed, 0 failed, 0 skipped, including Application, Infrastructure, 2 architecture tests, and 3 BDD scenarios. Domain line coverage 96.55% (>=90% gate passes).
- Implemented: add/update/enable/disable/remove/rescan root use cases; duplicate-path guard; local/USB discovery; network roots explicitly deferred; supported video filtering, include/exclude extension/name/subtree patterns, hidden/system/reparse skipping; cancellable iterative traversal (100k-directory bound), accessible sibling continuation; volume signature + relative path + size/time identity comparisons; global volume-signature-change warning with matching paths forced to NeedsReview (never silently updated); uncertain rename/move to review; unchanged records not resaved; temp-file atomic persistence under LocalAppData for root configuration and indexed records.
- Hardening: Central Package Management pins System.IO.FileSystem.AccessControl 5.0.0 (System.Security.Principal.Windows is in-box, not referenced); discovery/manifest stores use in-process gate plus cross-process DeleteOnClose lock files, unique temp names, atomic replace, corrupt-snapshot reporting, and best-effort current-user ACLs; filesystem source uses StringBuilder P/Invoke, drive/path error wrapping to unavailable, and invalid-child skipping; IO/access failures retain records as RootUnavailable.
- Tests: orchestration tests cover first/unchanged/changed/signature-change (same and changed revision)/rename/offline/disabled/network/cancellation; filesystem temporary-dir integration tests cover relative paths, size, deep nesting, drive-signature stability, cancellation, unavailable roots, snapshot persistence, and corrupt-snapshot reporting; BDD scenarios exercise initial scan and offline-root retention.
- Known limitations: temporary per-user JSON snapshot storage is intentionally replaced by SQLite repository/schema in Phase 3; supported video extensions/pattern matching are conservative and intentionally small; USB detection is currently selected by configured root kind, so hardware enumeration/automatic drive mounting remains outside Phase 2; no packaged WPF scan UI yet.

### Phase 3

- Commit SHA: pending (locally verified, uncommitted).
- Build/test results: Release build 0 warnings/0 errors. Full solution suite: 91 passed, 0 failed, 0 skipped (59 prior + 17 Application queue-policy + 15 Infrastructure SQLite integration), including 2 architecture tests and 3 BDD scenarios. Domain line coverage 96.55% (>=90% gate passes).
- Implemented: Application queue contracts (`JobType`, `JobState`, `QueueJob`, `IJobQueue`, versioned payload validation with 64K limit, deterministic FNV-1a backoff/jitter retry policy, `IQueueClock`); Infrastructure `SqliteJobQueue` (WAL initialized outside transactions, per-connection foreign keys + busy timeout >= 5000, `BEGIN IMMEDIATE` claims, in-process single-writer gate, atomic lease acquisition with expired-lease recovery, poison/dead-letter at max attempts, idempotent enqueue with unique active `(FileId, JobType)` index plus race-tolerant re-read, priority ordering, per-drive/global concurrency caps, cancellation-safe transitions that never hold a transaction during processing, transactional v0-to-v1 migration).
- Tests: payload validation/retry determinism/bounds; roundtrip + stats; reopen durability; true-parallel claim uniqueness (Task.Run; Microsoft.Data.Sqlite async runs inline-sync so bare calls would serialize); batch atomicity; backoff invisibility then poison; stale-lease reclaim; corrupt-payload quarantine; empty + v0 migration; per-transition cancellation; per-drive/global caps; blocking-writer release (background init via Task.Run); lease-owner enforcement; enqueue validation. Corrupt payloads are dead-lettered on read, never returned to workers.
- Known limitations: Microsoft.Data.Sqlite executes async ADO inline-sync, so a contended queue call can block its caller up to the command timeout and mid-wait cancellation is entry-checked only; mid-flight cancellation still never corrupts state (claim/complete/fail commit atomically or roll back). Discovery JSON bridge is unchanged and still awaits SQLite migration in a later phase; no worker BackgroundServices yet (worker-facing interfaces only).

Never mark a phase complete merely because files were generated. Its prompt's Definition of Done must be demonstrated by the tests/build and, where relevant, the packaged app interface.
