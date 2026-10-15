# VideoDedup Phase Tracker

Source prompts: [`PHASE-PROMPTS.md`](PHASE-PROMPTS.md). Project rules: [`ARCHITECTURE.md`](ARCHITECTURE.md), [`DECISIONS.md`](DECISIONS.md), and the project skill at [`.cursor/skills/vibecoding-context/SKILL.md`](.cursor/skills/vibecoding-context/SKILL.md).

## State

- [x] Phase 0 — Solution foundation, CI, smoke tests, and project docs. Done and CI green at commit `291140a`.
- [x] Phase 1 — Domain model and matching policy. Evidence: Release build 0 warnings/0 errors; full solution 39/39 tests pass (33 Domain, 2 ArchUnitNET, 4 other unit, 1 BDD); Domain line coverage 96.53%, branch coverage 89.49%; 90% minimum enforced in CI. See phase exit record below.
- [ ] Phase 2 — Scan roots and file discovery.
- [ ] Phase 3 — Durable SQLite job queue.
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

Never mark a phase complete merely because files were generated. Its prompt's Definition of Done must be demonstrated by the tests/build and, where relevant, the packaged app interface.
