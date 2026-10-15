# Reference — deeper context, read only the section you need

## Data model (planned schema, Phase 2–3)

Tables and columns per original plan (PDF §Data model):
`ScanRoots` (ScanRootId PK, Path, RootType LocalFolder|UsbDrive|NetworkShare, DriveId FK,
Enabled, IncludeSubfolders, ExcludePatternsJson, LastScanUtc, ScanState),
`Drives` (DriveId PK, VolumeSignature, Label, TotalBytes, IsAvailable, LastSeenUtc),
`VideoFiles` (FileId PK, ScanRootId FK, DriveId FK nullable, StableIdentity,
RelativePath, FullPath, FileName, Extension, SizeBytes, ModifiedUtc, DurationSeconds,
Width, Height, VideoCodec, AudioCodec, HasAudio, Sha256, FingerprintVersion, ScanState,
ErrorMessage, RetryCount),
`VisualFingerprints` (FileId FK, TimestampSeconds, Phash, QualityScore),
`AudioFingerprints` (FileId FK, StartSeconds, EndSeconds, FingerprintBlob, FingerprintHash),
`Matches` (MatchId PK, FileIdA FK, FileIdB FK, MatchType, VisualScore, AudioScore,
OverallScore, ClipStartSeconds, ClipEndSeconds, Status, CreatedUtc),
`Jobs` (JobId PK, FileId FK nullable, ScanRootId FK nullable, JobType, Priority, State,
Attempts, NextRunUtc, LockedBy, LockedUtc, PayloadJson, Error).

Queue discipline: `BEGIN IMMEDIATE` for claims, `busy_timeout >= 5000`, WAL mode,
single-writer for mutations, no duplicate active (FileId, JobType).

## Workers (Phase 2/3)

DriveMonitor, Discovery, Probe, Hash, VisualFingerprint, AudioFingerprint, Matching,
Maintenance — each a `BackgroundService` consuming the durable SQLite job queue.
Per-drive concurrency limits; cancellation-safe; resume after crash.

## Matching policy (Phase 7)

`IDuplicateMatchPolicy.Evaluate(MatchCandidate) -> MatchDecision`. Tiers:
exact (SHA-256), near duplicate (visual + audio score above threshold), partial clip
(audio window alignment + visual confirmation at offset), hard transformation
(deferred/AI), needs-review (ambiguous). Matcher never deletes — it only creates
`Matches` rows with Status Candidate|Confirmed|Rejected|NeedsReview.

## Edge-case inventory

See the original plan's "Quality and corner cases" lists — those bullet lists are the
tracking source for unit/BDD coverage. Every listed case needs at least one test or
scenario before the phase it belongs to closes.
