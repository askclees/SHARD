#!/usr/bin/env python3
"""Compare loose vs. tight orphan-page carving for a database, without writing anything —
carve() is read-only; use full_recovery.py's --carve-mode to persist results.

"loose" matches a candidate table's declared column types only (more hits, more false
positives). "tight" additionally narrows each column to the byte-length range actually observed
in that table's own live/deleted data (fewer false positives, but misses rows outside that
range). Comparing both gives a sense of how ambiguous the recovered pages are for this file.

Usage:
    python3 carve_report.py evidence.db
    python3 carve_report.py evidence.db --table users --table notes
    python3 carve_report.py evidence.db --output carved.json
"""
from __future__ import annotations

import argparse
import json
import sys
from collections import Counter

from shard_native import ShardDatabase, ShardError


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("db_path", help="Path to the SQLite evidence file")
    parser.add_argument("--table", action="append", dest="tables",
                         help="Restrict carving to this table (repeatable). Default: every live table.")
    parser.add_argument("--output", help="Write the tight-mode carved records to this JSON file")
    args = parser.parse_args()

    try:
        with ShardDatabase(args.db_path) as db:
            loose = db.carve(mode="loose", tables=args.tables)
            tight = db.carve(mode="tight", tables=args.tables)
    except ShardError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 1

    loose_counts = Counter(r["tableName"] for r in loose)
    tight_counts = Counter(r["tableName"] for r in tight)

    table_names = sorted(set(loose_counts) | set(tight_counts))
    if not table_names:
        print("No orphan pages matched any candidate table's schema.")
        return 0

    print(f"{'Table':<30} {'Loose':>8} {'Tight':>8}")
    for name in table_names:
        print(f"{name:<30} {loose_counts[name]:>8} {tight_counts[name]:>8}")
    print(f"\n{'TOTAL':<30} {len(loose):>8} {len(tight):>8}")

    if args.output:
        with open(args.output, "w", encoding="utf-8") as f:
            json.dump(tight, f, indent=2, default=lambda v: v.hex() if isinstance(v, (bytes, bytearray)) else str(v))
        print(f"\nTight-mode carved records written to {args.output}")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
