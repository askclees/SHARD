#!/usr/bin/env python3
"""Extract a table's rows — live only by default, or live + recoverable deleted rows combined
with --include-deleted. Uses ShardDatabase.table_rows(), which returns exactly the table's own
columns (not _shard_recovered_<table>'s extra bookkeeping columns).

Usage:
    python3 extract_table.py evidence.db users
    python3 extract_table.py evidence.db users --include-deleted
    python3 extract_table.py evidence.db users --include-deleted --carve-mode tight
"""
from __future__ import annotations

import argparse
import sys

from shard_native import ShardDatabase, ShardError


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("db_path", help="Path to the SQLite evidence file")
    parser.add_argument("table", help="Table name to extract")
    parser.add_argument("--include-deleted", action="store_true",
                         help="Also include in-tree recoverable deleted/freeblock rows")
    parser.add_argument("--carve-mode", choices=["loose", "tight"], default=None,
                         help="Also include carved orphan-page records (implies --include-deleted's recovered copy; "
                              "default: don't carve)")
    parser.add_argument("--no-wal", action="store_true",
                         help="Skip processing a sibling -wal file even if one exists")
    args = parser.parse_args()

    try:
        with ShardDatabase(args.db_path) as db:
            rows = db.table_rows(
                args.table,
                include_deleted=args.include_deleted,
                process_wal=not args.no_wal,
                carve_mode=args.carve_mode,
            )
    except ShardError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 1

    if not rows:
        print("(no rows)")
        return 0

    columns = list(rows[0].keys())
    print("  ".join(columns))
    for row in rows:
        print("  ".join(str(row[c]) for c in columns))
    print(f"\n{len(rows)} row(s)")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
