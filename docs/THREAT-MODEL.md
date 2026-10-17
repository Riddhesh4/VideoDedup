# Threat model and residual risk

Scope: a single-user Windows desktop application that reads local video files, extracts metadata and fingerprints,
compares them, and moves files only when the user confirms a reversible quarantine. There is no server component, no
network traffic, and no multi-user or remote-input surface.

## Assets

1. **The user's media files** — the highest-value asset, and the one this application must never damage or lose.
2. **The local index** — file names, paths, sizes, timestamps, hashes, fingerprints, decisions. Private, and useful
   to an attacker who wants to know what is on the machine.
3. **The quarantine store** — copies of the user's media inside the data folder, plus the undo log that can put them
   back.
4. **Availability of the user's own work** — a scan that crashes, stalls or loses completed work wastes hours.

## Trust boundaries and mitigations

| Boundary | Untrusted input | Mitigations |
| --- | --- | --- |
| Media files and their contents | File names, container metadata, codec data, corrupt or hostile files | Revision check before and after reading (`FileRevision` + change-time witness); pixel/frame/duration/output caps; per-process Job Object memory and CPU limits; timeouts; typed failures; decode failures never create an exact match |
| File names and paths | Unicode, emoji, control characters, direction overrides, very long paths, reparse points | Displayed text passes `DisplayText.Sanitize` and renders as plain text; traversal and control characters are rejected by Domain validation; reparse points are skipped in discovery and never followed or deleted |
| Subprocesses (FFprobe, FFmpeg, fpcalc) | Their output, exit codes, and writes | Absolute allowlisted executable paths, `ProcessStartInfo.ArgumentList` only (no shell), output-size caps, timeouts, process-tree kill, explicit exit-code checks, bounded and defensive parsers |
| The SQLite index | Contents that may be corrupt or hand-edited | Values are revalidated on read; corrupt rows surface typed `InvalidData`; the database is opened with WAL, foreign keys and a busy timeout; a corrupt database is moved aside intact and a fresh index is created rather than failing to launch |
| Interactive input | Clipboard-style pastes into path fields, extremely long values | Length caps, control-character rejection, no markup rendering anywhere in the UI |
| The application's own data | Anything that could cause the application to delete the wrong thing | `AppDataRetentionPolicy` deletes only inside the data folder, never a reparse point, never inside a scan root, never quarantined media, never scan-root settings; deletion is refused while work is queued or leased and requires explicit confirmation |
| The application's own logs | Failure text derived from paths | Failures are a closed vocabulary of `Type: Reason`; the durable log has no message field; `PrivacyText.RedactPaths` is a second layer before writing |
| Hardware decoding | Driver and hardware behaviour | Hardware is a detected capability and a hint; every hardware attempt falls back to the deterministic software path for the same frame, and high-depth inputs never use hardware |

## Deliberately out of scope

- **Local administrator or kernel-level attackers.** They can read the index, alter the log, replace the binaries or
  call the filesystem directly. No user-mode design defends against them.
- **An attacker who can already write to the data folder.** Such an attacker can rewrite the index, the quarantine
  log or the processing log; the log is not tamper-evident.
- **Physical access, memory scraping and side channels.**
- **Malicious media that exploits a decoder.** The mitigation is privilege and resource bounding plus process
  isolation, not content sanitisation; a decoder 0-day is a decoder 0-day.
- **Anti-analysis.** Obfuscation is friction only, never a security boundary (see `DECISIONS.md`).

## Residual risks accepted for v1

1. Revision checks use size, timestamp and the NTFS change-time witness, not adversarial same-size/same-time
   replacement detection. A determined attacker who can write to the media can substitute content that the revision
   check may accept; Windows publishes change-time asynchronously, which is why the check requires an observed drift
   or witness rather than asserting a settled value.
2. `PrivacyText.RedactPaths` stops at the first space, so a path with spaces inside a hypothetical free-form message
   would leak its tail. The durable log has no free-form message field, so this can only matter for a future caller
   that starts writing one.
3. The index is not encrypted. It is protected by the Windows profile ACL only, so anything running as that user can
   read it. DPAPI would be added only if a real local secret (not merely private-ish data) were introduced.
4. Quarantine keeps a copy inside the data folder. If the media volume fills up or the folder is deleted by the user,
   the quarantined copy is lost with it; the original path is recorded so the loss is explainable.
5. The processing log is bounded and rotating, so it cannot serve as a forensic record of a long history.
6. The released binaries are **not code-signed**. Windows will show a SmartScreen warning for a downloaded build, and
   there is no publisher identity to verify. Signing is a separate, documented choice (see `RELEASE.md`); no
   signature is fabricated.
7. Obtained artifacts (FFmpeg/FFprobe/fpcalc) are hashed and pinned but not sandboxed beyond the Job Object limits
   documented in `THIRD-PARTY.md`.
