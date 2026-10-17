#!/usr/bin/env python3
"""Wait until a VideoDedup index has no queued or leased job and report what the run did.

Reads the database read-only, so it can be used while the application is running (the index is in WAL mode).
JobType/JobState integers come from VideoDedup.Application.QueueContracts.

Usage: wait-for-queue-drain.py <database> [--timeout 900] [--interval 2] [--idle-exit 30]
Prints one JSON object on success; exits 2 on timeout with the last observed counts on stdout.
"""
from __future__ import annotations

import argparse
import json
import sqlite3
import sys
import time

JOB_TYPES = {
    0: "Discover",
    1: "Probe",
    2: "Hash",
    3: "VisualFingerprint",
    4: "AudioFingerprint",
    5: "Match",
    6: "Maintenance",
}

JOB_STATES = {0: "Queued", 1: "Leased", 2: "Completed", 3: "DeadLetter"}


def table_count(connection: sqlite3.Connection, table: str) -> int:
    """Tables are created lazily by the application, so an absent table simply means "nothing yet"."""
    try:
        return connection.execute(f"SELECT COUNT(*) FROM {table}").fetchone()[0]
    except sqlite3.OperationalError:
        return 0


def read_counts(database: str) -> dict:
    connection = sqlite3.connect(f"file:{database}?mode=ro", uri=True, timeout=5)
    try:
        connection.row_factory = sqlite3.Row
        by_state = {state: 0 for state in JOB_STATES}
        by_type: dict[str, int] = {}
        for row in connection.execute("SELECT JobType, State, COUNT(*) AS n FROM jobs GROUP BY JobType, State"):
            name = JOB_TYPES.get(row["JobType"], f"Unknown({row['JobType']})")
            state = JOB_STATES.get(row["State"], f"Unknown({row['State']})")
            by_state[row["State"]] = by_state.get(row["State"], 0) + row["n"]
            by_type[f"{name}.{state}"] = row["n"]
        files = table_count(connection, "media_results")
        matches = table_count(connection, "match_results")
        try:
            audio_failures = connection.execute(
                "SELECT COUNT(*) FROM audio_results WHERE Failure IS NOT NULL"
            ).fetchone()[0]
        except sqlite3.OperationalError:
            audio_failures = 0
        return {
            "queued": by_state.get(0, 0),
            "leased": by_state.get(1, 0),
            "completed": by_state.get(2, 0),
            "deadLetter": by_state.get(3, 0),
            "filesIndexOfRecordRows": files,
            "matchRows": matches,
            "audioFailures": audio_failures,
            "byTypeAndState": dict(sorted(by_type.items())),
        }
    finally:
        connection.close()


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("database")
    parser.add_argument("--timeout", type=float, default=900)
    parser.add_argument("--interval", type=float, default=2)
    parser.add_argument("--idle-exit", type=float, default=30,
                        help="how long the queue must stay empty before the run counts as finished")
    arguments = parser.parse_args()

    started = time.monotonic()
    deadline = started + arguments.timeout
    counts = read_counts(arguments.database)
    empty_since: float | None = None
    while time.monotonic() < deadline:
        counts = read_counts(arguments.database)
        if counts["queued"] == 0 and counts["leased"] == 0 and counts["completed"] > 0:
            empty_since = empty_since or time.monotonic()
            if time.monotonic() - empty_since >= arguments.idle_exit:
                counts["elapsedSeconds"] = round(time.monotonic() - started, 1)
                counts["idleDrainSeconds"] = round(arguments.idle_exit, 1)
                print(json.dumps(counts))
                return 0
        else:
            empty_since = None
        time.sleep(arguments.interval)

    counts["elapsedSeconds"] = round(time.monotonic() - started, 1)
    counts["timedOut"] = True
    print(json.dumps(counts))
    return 2


if __name__ == "__main__":
    sys.exit(main())
