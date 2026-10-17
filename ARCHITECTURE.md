# VideoDedup Architecture Charter

## Product and threat model

A Windows-first, local-only video duplicate scanner for folders and USB drives. It indexes media metadata/fingerprints and recommends matches; it never deletes automatically. No cloud backend or remote service in v1.

Assume scanned media and filenames are untrusted. Protect user data from accidental loss, unauthorized access by other standard local users, command injection, path traversal, unsafe process invocation, and corrupted/interrupted work. The owner of the machine (local administrator) can inspect or modify a desktop application; **no obfuscator or packaging mode can guarantee secrecy or prevent reverse engineering**. Reduce the value of inspection: do not embed secrets, keep matching/security decisions client-side only when they are not secrets, and use obfuscation as friction—not as an authorization boundary.

## Stack

- .NET 10 LTS; C#; WPF + MVVM desktop.
- .NET Generic Host and `BackgroundService` workers with cancellation.
- SQLite via `Microsoft.Data.Sqlite`; WAL; `busy_timeout >= 5000`; foreign keys enabled; `BEGIN IMMEDIATE` for queue claims; serialize writes.
- FFmpeg/FFprobe from a pinned BtbN **LGPL** Windows build, decode-only; Chromaprint `fpcalc -json`; in-house pHash on decoded frame buffers.
- xUnit v3 on Microsoft Testing Platform; FluentAssertions; NSubstitute; Reqnroll.xunit.v3 BDD; ArchUnitNET.

## Dependency boundaries

- **Domain**: entities, value objects, matching policy. Pure C#; no WPF, SQLite, FFmpeg, filesystem, or process dependencies.
- **Application**: use cases and ports/interfaces; depends on Domain only.
- **Infrastructure**: SQLite, filesystem, drive, process, FFmpeg and Chromaprint adapters; implements Application ports.
- **Workers**: background orchestration; consumes Application interfaces.
- **Desktop**: WPF presentation and ViewModels; calls Application, never FFmpeg/SQLite/processes directly.
- **Tests**: unit tests by layer, integration tests for real adapters, separate BDD project.

## Security invariants

1. Never execute user input through a shell. Use `ProcessStartInfo.ArgumentList`, fixed executable paths, bounded timeouts, cancellation, output limits, and explicit exit-code checks.
2. Treat media, paths, metadata, filenames, and tool output as untrusted. Bound file sizes, duration, frame counts, and parser input; handle malformed files without crashing workers.
3. Restrict application data to the current user's profile with appropriate Windows ACLs. Do not log full paths or media metadata unless the user explicitly opts in. No telemetry in v1.
4. No credentials, API keys, signing keys, or secrets in source, binaries, logs, or test fixtures. If a future feature requires a local secret, use Windows DPAPI through a reviewed abstraction; never claim that obfuscation protects embedded secrets.
5. Destructive actions are forbidden by default. Quarantine is the default safe action, with explicit confirmation, last-copy protection, and an undo/audit record.
6. Pin external binary versions and hashes; maintain `THIRD-PARTY.md`; reject GPL/nonfree artifacts unless a documented licensing decision changes.
7. Release obfuscation with pinned Obfuscar is a reverse-engineering deterrent only. The shipped release artifact is unobfuscated (decision 24): a full-app obfuscated publish was measured not to start, and the Domain-only gate is the form this project verifies. CI enforces that gate (publish → obfuscate → fail if `ScanState`/`IScanOrchestrator` survive in the Domain assembly) and any future full-app attempt must prove a passing packaged smoke run with the assemblies renamed before it may ship. Do not use ReadyToRun or NativeAOT as security claims, and never claim obfuscation is an authorization boundary.
8. Report limitations honestly: client-side code can be inspected; the scanner is not a DRM/anti-tamper system.

## Quality gates

- `dotnet build VideoDedup.slnx -c Release`: zero warnings and errors.
- `dotnet test VideoDedup.slnx -c Release`: all unit, integration, and BDD tests pass; never accept zero discovered tests.
- Architecture tests enforce dependency boundaries.
- New public behavior has tests; matching logic is test-first and deterministic.
- Every phase prompt is in `PHASE-PROMPTS.md`; phase completion updates `TASKS.md` and the orientation skill progress in the same change.
- No phase is complete until its own Definition of Done and all applicable security invariants pass.
