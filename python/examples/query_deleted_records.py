#!/usr/bin/env python3
"""Run a SQL query against a table's live rows, its recovered deleted rows, or both — via
ShardDatabase.query(), which runs real SQL through the stdlib sqlite3 module against a fully
recovered copy of the database (built once in a temp file and reused across queries).

The SQL argument is a template: {table} is replaced with the live table's own name, and
{recovered_table} with its _shard_recovered_<table> counterpart, so one query works for
whichever you point it at.

Usage:
    # Just live rows (plain SQL against the table also works with no placeholders at all)
    python3 query_deleted_records.py evidence.db users "SELECT id, name FROM {table}"

    # Just recovered deleted rows
    python3 query_deleted_records.py evidence.db users "SELECT id, name FROM {recovered_table} WHERE id > 100"

    # Both combined
    python3 query_deleted_records.py evidence.db users \\
        "SELECT id, name FROM {table} UNION ALL SELECT id, name FROM {recovered_table}"

    # Include carved orphan-page records in the recovered copy too
    python3 query_deleted_records.py evidence.db users "SELECT * FROM {recovered_table}" --carve-mode tight
"""
from __future__ import annotations

import argparse
import sys

from shard_native import ShardDatabase, ShardError


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("db_path", help="Path to the SQLite evidence file")
    parser.add_argument("table", help="Table name — fills in {table}/{recovered_table} in the SQL template")
    parser.add_argument("sql", help="SQL template; may use {table} and/or {recovered_table} placeholders")
    parser.add_argument("--carve-mode", choices=["loose", "tight"], default=None,
                         help="Also include carved orphan-page records in the recovered copy (default: don't carve)")
    parser.add_argument("--no-wal", action="store_true",
                         help="Skip processing a sibling -wal file even if one exists")
    args = parser.parse_args()

    sql = args.sql.format(table=args.table, recovered_table=f"_shard_recovered_{args.table}")

    try:
        with ShardDatabase(args.db_path) as db:
            rows = db.query(sql, process_wal=not args.no_wal, carve_mode=args.carve_mode)
    except ShardError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 1
    except Exception as exc:  # sqlite3.OperationalError etc. — surface as a plain CLI error
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
