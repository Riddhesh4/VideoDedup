# VideoDedup Phase Tracker

Source prompts: [`PHASE-PROMPTS.md`](PHASE-PROMPTS.md). Project rules: [`ARCHITECTURE.md`](ARCHITECTURE.md), [`DECISIONS.md`](DECISIONS.md), and the project skill at [`.cursor/skills/vibecoding-context/SKILL.md`](.cursor/skills/vibecoding-context/SKILL.md).

## State

- [x] Phase 0 — Solution foundation, CI, smoke tests, and project docs. Done and CI green at commit `291140a`.
- [x] Phase 1 — Domain model and matching policy. Evidence: Release build 0 warnings/0 errors; full solution 39/39 tests pass (33 Domain, 2 ArchUnitNET, 4 other unit, 1 BDD); Domain line coverage 96.53%, branch coverage 89.49%; 90% minimum enforced in CI. See phase exit record below.
- [x] Phase 2 — Scan roots and file discovery. Evidence and remaining adapter/persistence boundary are recorded below.
- [x] Phase 3 — Durable SQLite job queue. Evidence and exit record below.
- [x] Phase 4 — FFprobe metadata and exact SHA-256 duplicates (locally verified; not committed).
- [x] Phase 5 — Visual fingerprints (locally verified; uncommitted). See Phase 5 exit evidence below.
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

### Phase 4 local exit evidence

- Implemented typed media ports, allowlisted ArgumentList process runner (30s probe deadline, combined 1 MiB output cap, process-tree cleanup), defensive FFprobe metadata parser, revision checks and 64 KiB-buffer streaming SHA-256 (5-minute deadline).
- Probe/Hash queue worker iterations persist metadata/hash/failure in SQLite media_results, preserve results only within the same revision, and retry transient failures; cancellation leaves the lease for recovery. Host integration remains explicit via RunOnceAsync off the UI thread; no Desktop scan UI or discovery-to-processing auto-enqueue is added.
- Release build: zero warnings/errors; 114 tests passed, no failures/skips, including real tools, worker roundtrip, failure paths, output bounds, timeout/cancellation, malformed metadata, file locks/staleness and exact hashes. Domain line coverage 96.55%, >=90% gate passed.
- Pinned BtbN LGPLv3-or-later tools and generated FFV1/PCM fixture prepared by scripts/prepare-media.py; CI runs the same preparation. No media executables or generated media are committed.
- Limitations: revision checks use size/mtime, not adversarial same-size/time replacement detection; no OS sandbox beyond protocols/allowlist/resource bounds; static media redistribution obligations need packaging review. Known no-video/unknown-codec inputs are terminal Unsupported; other nonzero probes are InvalidData. No remote CI run or commit/push performed for Phase 4.

### Phase 5 local exit evidence

- Final verification after the last code change: Release solution build 0 warnings / 0 errors; full suite 152 passed, 0 failed, 0 skipped (unit, architecture, infrastructure, worker, and Reqnroll BDD); Domain coverage 96.87% line and the 90% gate passed.
- Generated-media script verifies pinned LGPL FFmpeg/FFprobe archive/configuration and creates deterministic synthetic original, resized/re-encoded, letterboxed, unrelated, black/static, short, VFR, rotated and 10-bit clips; no media/binaries are committed.
- Real FFmpeg integration verifies deterministic/versioned hashes, resized/re-encoded/letterboxed and rotated candidate behavior, unrelated separation, single-frame rejection, black/static filtering, frame/timestamp bounds, VFR/10-bit/short handling, hostile Unicode paths, output caps and software fallback. SQLite tests cover existing database initialization, reopen roundtrip including unsigned hashes, separate visual failure, cancellation, concurrent open/write, transaction rollback, corrupt values and caps. Hosted processing verifies visual persistence, revision/settings-version enqueue and unchanged rescan suppression.
- Published Release WPF UI Automation smoke passed twice using the same isolated data directory; scan, visual completion status, shutdown and reopen succeeded. Package output is verified to omit the `media` directory and FFmpeg binaries. The Reqnroll-generated feature class is regenerated from the feature source.
- No packages added; FFmpeg remains user-prepared and excluded from packaging; licensing certification remains separate. Visual evidence creates candidates only; final sequence matching/classification is Phase 7. Decode work has pixel/allocation/output/frame/thread limits, a 2-minute per-video wall-time budget, 10-second per-frame wall-time budget and 8-second Windows Job Object process CPU-time limit, plus a 512 MiB Job Object memory cap. Administrator/kernel attackers remain outside the threat model.
- No commit, push, or remote CI run.

### Phase 4 integrated checkpoint follow-up

- Approved expansion implemented: Generic Host ProcessingService, discovery-to-Probe/Hash enqueue, unchanged-completed rescan suppression, cached video decoder inventory, volume/file ID plus Windows change-time revision evidence, SQLite identity migration, and minimal WPF dashboard through Application facade with separate Launcher composition root.
- New tests cover hosted end-to-end discovery, real decoder inventory and timestamp-preserving same-size rewrites/replacement. 117 tests total. Build and published WPF smoke exercised folder entry, Scan invocation, completed result and clean shutdown.
- Packaging excludes static media tools and blocks accidental inclusion; legal/static-source compliance certification remains intentionally unclaimed. CI publishes the no-media desktop checkpoint.
- Windows profile ACL inheritance bug repaired in repository code; existing development LocalAppData files created under the old broken ACL remain inaccessible. Windows refused non-elevated repair; no bypass attempted. Smoke uses isolated --data directory. Existing affected profiles require explicit ACL recovery; no source media affected.
- File ID/change time is not protection against administrator/kernel tampering. FAT/exotic filesystem change-time precision may differ. UI is a processing checkpoint, not Phase 8 review/quarantine UI.

- Published Release WPF smoke passed twice with the same isolated data directory, proving shutdown/reopen after ACL-inheritance repair. Strong revision regression and hosted unchanged-rescan regression pass. Desktop package is an unobfuscated development checkpoint, not a production installer; existing Domain obfuscation CI gate remains in place.
