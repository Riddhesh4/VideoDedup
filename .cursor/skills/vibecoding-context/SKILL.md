---
name: vibecoding-context
description: Orient in the VideoDedup repo without re-reading the codebase. Before any non-trivial task in this repository, read this skill instead of scanning source files; it maps architecture, conventions, commands, and where the current work sits (phase-wise). Use whenever starting a session in this repo, after a model/agent change, or when unsure where a change belongs.
---

# VideoDedup — AI Orientation Skill

**Purpose: never re-scan the whole repo.** After a model/agent change, start here — a
session consumes this file (~1 read) instead of walking the tree.

## One-paragraph summary

Windows video-duplicate scanner. .NET 10 LTS, WPF + MVVM UI, BackgroundService workers,
SQLite (per-user, WAL, single-writer), FFmpeg/FFprobe (BtbN **LGPL** build), Chromaprint
via `fpcalc -json`, in-house pHash. Quality gates: xUnit v3 (MTP), Reqnroll BDD,
ArchUnitNET, zero warnings, Obfuscar in Release.

## Map (what lives where)

| Path | Contents |
|---|---|
| `ARCHITECTURE.md` | Concise charter, dependency boundaries, threat model, and invariants |
| `DECISIONS.md` | Accepted choices and pending decisions |
| `TASKS.md` | Current phase status and exit-record template |
| `PHASE-PROMPTS.md` | Canonical phase-by-phase prompts adapted from the source PDF |
| `PLAN-REVIEW.md` | Original-plan review and amendments |
| `THIRD-PARTY.md` | Licensing ledger for external binaries |
| `src/VideoDedup.Domain` | Entities, matching policies; pure C# — no infra deps allowed |
| `src/VideoDedup.Application` | Use cases, orchestrators, interfaces |
| `src/VideoDedup.Infrastructure` | FFmpeg/Chromaprint/SQLite/file adapters |
| `src/VideoDedup.Workers` | `BackgroundService` pipeline workers |
| `src/VideoDedup.Desktop` | WPF app (never touches FFmpeg or DB directly) |
| `tests/VideoDedup.*.Tests` | Unit tests per layer |
| `tests/VideoDedup.Bdd.Tests` | Reqnroll `Features/*.feature` + `Steps/` |
| `tests/VideoDedup.Architecture.Tests` | ArchUnitNET layer-boundary rules |
| `scripts/obfuscate.ps1` | Release-time obfuscation; generates `obfuscar.generated.xml` with absolute paths |

## Non-negotiables

1. `dotnet build` = **0 warnings**; all tests pass before a commit.
2. Domain references nothing infra; Desktop references Application only.
3. No GPL artifacts — check `THIRD-PARTY.md` before adding binaries.
4. Never auto-delete user files (quarantine + undo are invariants).
5. No client-side secrets; obfuscation only raises RE cost and is not a security boundary.
6. Treat media/paths/tool output as untrusted; subprocesses use `ArgumentList`, never a shell.
7. Test project names end `.Tests`; BDD steps use a consistent feature-specific convention.
8. New code needs tests; test-first for domain/matching logic.

## Commands

```bash
dotnet build VideoDedup.slnx
dotnet test VideoDedup.slnx            # MTP mode via global.json — do not switch to VSTest
dotnet format VideoDedup.slnx           # before committing
dotnet tool restore                     # Obfuscar, then scripts/obfuscate.ps1 (Release only)
```

## Progress

- **Phase 0 — DONE**: bootstrap; CI on GitHub (`Riddhesh4/VideoDedup`), last verified green at `291140a`.
- **Phase 1 — DONE**: domain model/value objects (including drive details), deterministic matching policy, scan-state transitions, 33 Domain tests, 2 ArchUnitNET rules. CI green at commit `9023a6d`; Domain coverage: 96.53% lines / 89.49% branches.
- **Phase 2 — DONE (committed as 3d67e0a, CI ci #6 green on main)**: root management and local/USB file discovery; cancellable bounded traversal, global volume-change review, cross-process locked JSON bridge pending Phase 3 SQLite replacement. Release build 0 warnings/errors; solution 59/59 tests; Domain coverage 96.55%. Hardening: global volume-change review, cross-process JSON locking, ACL package pin.
- **Phase 3 — NEXT**: durable SQLite job queue. See `TASKS.md` for Phase 2 limitations and `PHASE-PROMPTS.md` for the next prompt.

When finishing a phase, **update this `## Progress` section in the same commit** — this is
the mechanism that keeps sessions cheap.

## Reference

- `reference.md` — data-model draft and planned policies; read only if actually needed.
