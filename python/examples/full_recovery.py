#!/usr/bin/env python3
"""Run a complete recovery pass — live rows, in-tree deleted/freeblock rows, WAL history (if a
sibling -wal file exists), and (optionally) orphan-page carving — into a fresh, ordinary SQLite
database, then verify the result with the stdlib sqlite3 module (no shard_native needed to read
the output afterwards).

Usage:
    python3 full_recovery.py evidence.db recovered.db
    python3 full_recovery.py evidence.db recovered.db --carve-mode tight
    python3 full_recovery.py evidence.db recovered.db --no-wal
"""
from __future__ import annotations

import argparse
import sqlite3
import sys

from shard_native import ShardDatabase, ShardError


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("db_path", help="Path to the SQLite evidence file")
    parser.add_argument("output_path", help="Path to write the recovered SQLite database to")
    parser.add_argument("--carve-mode", choices=["loose", "tight"], default=None,
                         help="Also carve orphan pages with no known owning table (default: don't carve)")
    parser.add_argument("--no-wal", action="store_true",
                         help="Skip processing a sibling -wal file even if one exists")
    args = parser.parse_args()

    try:
        with ShardDatabase(args.db_path) as db:
            result = db.recover_to_file(
                args.output_path,
                process_wal=not args.no_wal,
                carve_mode=args.carve_mode,
            )
    except ShardError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 1

    print(f"Recovered database written to {result['outputPath']}\n")

    print(f"{'Table':<30} {'Live':>8} {'Recovered':>10}")
    for table in result["tables"]:
        print(f"{table['tableName']:<30} {table['liveRowCount']:>8} {table['recoveredRowCount']:>10}")

    print(f"\nWAL records inserted: {result['walRecordsInserted']}")
    if args.carve_mode:
        print(f"Carved records:       {result['carvedRecords']}  "
              f"(ambiguous/skipped: {result['carveAmbiguousSkipped']})")

    if result["warnings"]:
        print("\nWarnings:")
        for warning in result["warnings"]:
            print(f"  - {warning}")

    # The output is a normal SQLite database — verify it with the stdlib, no shard_native needed.
    conn = sqlite3.connect(args.output_path)
    try:
        total = 0
        for (table_name,) in conn.execute("SELECT name FROM sqlite_master WHERE type='table'"):
            (count,) = conn.execute(f'SELECT COUNT(*) FROM "{table_name}"').fetchone()
            total += count
        print(f"\nVerified via stdlib sqlite3: {total} total row(s) readable in {args.output_path}")
    finally:
        conn.close()

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
