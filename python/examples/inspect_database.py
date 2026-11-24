#!/usr/bin/env python3
"""Quick triage of a SQLite evidence file: header, schema, and live row counts per table.

Usage:
    python3 inspect_database.py evidence.db

Requires the shard_native package to be installed (`pip install -e ../`) and the native
library to be discoverable — see ../README.md.
"""
from __future__ import annotations

import argparse
import json
import sys

from shard_native import ShardDatabase, ShardError


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("db_path", help="Path to the SQLite evidence file")
    args = parser.parse_args()

    try:
        with ShardDatabase(args.db_path) as db:
            header = db.header
            print(f"SQLite {header['sqliteVersion']}  page size={header['pageSize']}  "
                  f"encoding={header['textEncoding']}")
            print(f"database size: {header['databaseSizeInPages']} pages  "
                  f"freelist: {header['totalFreelistPages']} pages")
            print()

            tables = [entry for entry in db.schema if entry["type"] == "table"]
            print(f"{len(tables)} table(s):")
            for entry in tables:
                name = entry["name"]
                if entry.get("rootPage") in (None, 0):
                    print(f"  {name:<30}  (virtual table — no B-tree, skipped)")
                    continue
                try:
                    live = len(db.rows(name))
                    deleted = len(db.deleted_rows(name))
                except ShardError as exc:
                    print(f"  {name:<30}  error: {exc}")
                    continue
                print(f"  {name:<30}  {live:>6} live   {deleted:>6} recoverable deleted")

    except ShardError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 1

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
