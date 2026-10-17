# Privacy and local data

VideoDedup is a local-only tool. It has no account, no telemetry, no update check and no network code path: nothing
is uploaded, and the only machine it reads is yours. The media itself is never modified by scanning; the only write
that can ever touch a media file is a **quarantine** you explicitly confirm, and that move is reversible from the
review tab.

## Where data lives

Everything the application owns sits in one folder, `%LOCALAPPDATA%\VideoDedup` unless `--data` points elsewhere:

| Path | Contents | Delete-index action |
| --- | --- | --- |
| `videodedup.db` (+ `-wal`, `-shm`) | SQLite index: scan roots, indexed files, probe metadata, SHA-256 hashes, visual and audio fingerprints, match results, review decisions, quarantine log, durable job queue | deleted (rebuilt on the next scan) |
| `Discovery\` | Discovery snapshots written outside the database | deleted |
| `logs\processing.jsonl` | Bounded privacy-safe processing log (see below) | deleted |
| `scan-roots.json` | Scan-root settings | **kept** so you can rescan; remove roots in the app if you want them gone |
| `Quarantine\` | Copies of media you chose to quarantine | **kept**: these are your files. Restore or remove them in the review tab first |

Source media is never application data, so no delete path in the application can reach it. The rules are pure and
tested in `AppDataRetentionPolicy` (`src/VideoDedup.Domain/AppDataRetentionPolicy.cs`), and the delete action is
confirmation-gated, refused while work is queued or leased, and reported item by item (deleted, kept with the reason,
failed with a redacted error).

## What the log contains — and what it cannot contain

`logs\processing.jsonl` holds one JSON object per worker iteration with exactly these fields:

```json
{"ts":"2026-10-16T09:12:03.1140000+00:00","worker":"Visual","outcome":"Processed","ms":812,"failure":null}
```

`worker`, `outcome`, `ms` and `failure` are the whole vocabulary. There is no free-form message field, so a path,
file name, folder name or media title has nowhere to go. A failure value is always a `Type: Reason` pair (for
example `MediaProcessingException: InvalidData`); `PrivacyText.RedactPaths` additionally replaces any drive-qualified
or UNC path with `<path>` before the line is written. Measured limits of that redaction:

- A path containing spaces is redacted up to its first space.
- A bare root such as `C:\` is left alone.
- Redaction is defence in depth, not the primary control: the primary control is that failure kinds are a closed
  vocabulary and never carry a path in the first place.

The log is size-bounded (4 MiB, one rotated file) and a failed write increments a counter instead of interrupting
processing.

## Export

The Privacy and data panel exports the index as JSON. **File names and paths are included by default** because the
index is your own data on your own machine; clearing *Include file names and paths* writes `"path": null` plus
`"pathRedacted": true` for every root, relative path and quarantine path, which makes the file safe to share for
diagnostics. Hashes, sizes, timestamps, codecs, scores, reasons and decisions stay in both forms. The document is
versioned (`schemaVersion`) and is written to the path you type, never into the application data folder.

## Not stored

- No media content, thumbnails or preview images: frame previews are decoded to memory and discarded.
- No fingerprint of anything except the video files you asked it to scan.
- No secrets: there is no credential, token or API key in this application, so no client secret exists to protect.
- No hardware or machine identifiers beyond the volume signature needed to recognise one of your drives again.

## Notes on the data that is stored

- SHA-256 hashes are content-derived. They cannot reconstruct a file, but if someone else learns a hash they can test
  whether a known file produces it. Treat an exported index as private unless you redact paths.
- Chromaprint audio fingerprints and perceptual visual hashes are one-way summaries used to compare clips.
- The processing log is not tamper-evident: anyone who can write to the data folder can rewrite it. It is a
  diagnostic record, not an audit trail.

## Removing everything

1. Open **Privacy and data**, choose **Delete index**. This removes the database, discovery snapshots and logs and
   reports anything it kept.
2. Restore or remove quarantined copies in the review tab; they are your media and are never deleted for you.
3. Delete the data folder to remove the remaining `scan-roots.json`. Removing the application folder itself
   uninstalls the tool; there is no installer and no registry entry.

See [THREAT-MODEL.md](THREAT-MODEL.md) for what this design does and does not defend against.
