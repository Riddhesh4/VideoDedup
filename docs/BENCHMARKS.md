# Measured performance

Every number here was produced by `scripts/benchmark.ps1` on this machine and is stored as JSON next to the run
(`TestResults/benchmarks/`, not committed). No number is estimated, and none is a claim about anyone else's hardware.

## Method

The benchmark drives the **real product interface**: it launches the published Release launcher, sets the folder and
clicks Scan through UI Automation exactly as the smoke test does, then polls the index
(`scripts/wait-for-queue-drain.py`) until the durable queue has no queued or leased job for five consecutive seconds.
Elapsed time, job counts, CPU time and memory come from that run.

- Fixture: 12 byte-identical copies of the generated `sample.mkv` (8 s, FFV1 + PCM), one resized/re-encoded
  `visual-resized.mp4` and one unrelated `visual-unrelated.mkv` — 14 files, 9,028,790 bytes total. This is a
  duplicate-detection workload, not a throughput benchmark for large libraries.
- Tools: the pinned LGPL FFmpeg/FFprobe build and the pinned acoustid fpcalc, both prepared by
  `scripts/prepare-media.py`.
- Data folder: `TestResults/bench-data`, deleted before each run so the measurement is a cold first scan.

## Result — first scan, 14 files, 70 jobs (2026-10-17)

| Metric | Measured |
| --- | --- |
| Wall clock, process start to drained queue | 55.3 s |
| Scan click to drained queue | 50.3 s |
| Jobs completed | 70 (Probe 14, Hash 14, Visual 14, Audio 14, Match 14) |
| Dead-lettered jobs | 0 |
| Throughput | 76 jobs/minute |
| Match rows persisted | 66 (exactly C(12,2) exact-duplicate pairs) |
| Average CPU | 0.47 cores |
| Peak application working set | 179 MiB |
| Processing log size | 9,865 bytes for the whole run |

Raw record: `TestResults/benchmarks/benchmark-local-20261016-215225.json`.

## What these numbers showed

The first measurement of this run reported **0 match rows** for twelve identical copies. That was a real defect, not a
slow run: `Match` jobs are enqueued with the fingerprint jobs, so they were claimed before a file's fingerprints
existed, completed without comparing anything, and the work was gone. Two fixes came out of measuring:

1. A match job for a file whose evidence is still arriving is now a **retryable** failure
   (`MediaFailure.EvidenceNotReady`), so the queue brings it back instead of discarding it.
2. A file whose **audio** stage failed permanently no longer disappears from matching. Hash and visual evidence are
   enough to compare; only evidence that is still arriving excludes a file. Before this, twelve identical copies
   produced zero matches because their audio stage had failed.

## Limitations of this measurement

- Child-process memory reads as 0 MiB because the 2 s sampling interval misses FFmpeg/FFprobe/fpcalc processes that
  each live well under a second. Peak memory is therefore the application's own working set, not the whole tree's.
- The fixture is small and audio-light; it says nothing about throughput on multi-gigabyte files or on a real USB
  drive. A tester with a real library should report file count, total size and elapsed time from the packaged build so
  these numbers can be compared (see `docs/RELEASE.md`).
- Stages run one job at a time per drive by design (per-drive cap 1, global cap 4), so elapsed time is dominated by
  the slowest stage rather than by available cores: 0.47 average cores reflects that deliberate choice, not a limit.
- The 5 second idle confirmation window is included in the drain time.
- The first scan now spends at least one retry interval (30 s base backoff) on files whose Match job arrives before
  their fingerprints; matching completes on the retry. Reducing that latency is future work.
