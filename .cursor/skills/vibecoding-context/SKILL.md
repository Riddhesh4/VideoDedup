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
| `PLAN-REVIEW.md` | Approved amended plan; §5.0 = RE-hardening rules |
| `THIRD-PARTY.md` | Licensing ledger for external binaries |
| `src/VideoDedup.Domain` | Entities, matching policies; pure C# — no infra deps allowed |
| `src/VideoDedup.Application` | Use cases, orchestrators, interfaces |
| `src/VideoDedup.Infrastructure` | FFmpeg/Chromaprint/SQLite/file adapters |
| `src/VideoDedup.Workers` | `BackgroundService` pipeline workers |
| `src/VideoDedup.Desktop` | WPF app (never touches FFmpeg or DB directly) |
| `tests/VideoDedup.*.Tests` | Unit tests per layer |
| `tests/VideoDedup.Bdd.Tests` | Reqnroll `Features/*.feature` + `Steps/` |
| `testing/` | Planned ArchUnitNET layer-rules project |
| `scripts/obfuscate.ps1` | Release-time obfuscation; generates `obfuscar.generated.xml` with absolute paths |

## Non-negotiables

1. `dotnet build` = **0 warnings**; all tests pass before a commit.
2. Domain references nothing infra; Desktop references Application only.
3. No GPL artifacts — check `THIRD-PARTY.md` before adding binaries.
4. Never auto-delete user files (quarantine + undo are invariants).
5. No secrets in code; per-user SQLite under `%APPDATA%` is the only store.
6. Test project names end `.Tests`; BDD steps are `FeatureXyz_Steps`.
7. New code needs tests; test-first for domain/matching logic.

## Commands

```bash
dotnet build VideoDedup.slnx
dotnet test VideoDedup.slnx            # MTP mode via global.json — do not switch to VSTest
dotnet format VideoDedup.slnx           # before committing
dotnet tool restore                     # Obfuscar, then scripts/obfuscate.ps1 (Release only)
```

## Progress

- **Phase 0 — DONE**: bootstrap; CI on GitHub (`Riddhesh4/VideoDedup`).
- **Phase 1 — NEXT**: domain model + `IDuplicateMatchPolicy` tests-first; ArchUnit layer rules.
- Phases 2–9 pending (see `PLAN-REVIEW.md` for pipeline and gate details).

When finishing a phase, **update this `## Progress` section in the same commit** — this is
the mechanism that keeps sessions cheap.

## Reference

- `reference.md` — data-model draft and planned policies; read only if actually needed.
