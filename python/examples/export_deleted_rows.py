#!/usr/bin/env python3
"""Export recovered deleted rows to CSV — one file per table.

The desktop app and CLI can inspect deleted rows, but neither currently exports them to
CSV/JSON directly; this is a small script filling that gap via the Python bindings.

Usage:
    python3 export_deleted_rows.py evidence.db                       # every table, ./deleted_rows/
    python3 export_deleted_rows.py evidence.db --table users         # just one table
    python3 export_deleted_rows.py evidence.db --output-dir out/     # custom output directory

Each CSV has columns: rowid, page, offset, then one column per table field. BLOB fields are
hex-encoded (e.g. "de:ad:be:ef") since raw bytes can't round-trip through CSV text cleanly.
"""
from __future__ import annotations

import argparse
import csv
import sys
from pathlib import Path
from typing import Any

from shard_native import ShardDatabase, ShardError


def _csv_value(value: Any) -> Any:
    if isinstance(value, (bytes, bytearray)):
        return ":".join(f"{b:02x}" for b in value)
    return value


def export_table(db: ShardDatabase, table_name: str, output_path: Path) -> int:
    rows = db.deleted_rows(table_name)
    if not rows:
        return 0

    field_names: list[str] = []
    for row in rows:
        for key in row["fields"]:
            if key not in field_names:
                field_names.append(key)

    output_path.parent.mkdir(parents=True, exist_ok=True)
    with output_path.open("w", newline="", encoding="utf-8") as f:
        writer = csv.writer(f)
        writer.writerow(["rowid", "page", "offset", *field_names])
        for row in rows:
            fields = row["fields"]
            writer.writerow([
                row["rowId"], row["pageNumber"], row["cellOffset"],
                *(_csv_value(fields.get(name)) for name in field_names),
            ])

    return len(rows)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("db_path", help="Path to the SQLite evidence file")
    parser.add_argument("--table", help="Export only this table (default: every live table)")
    parser.add_argument("--output-dir", default="deleted_rows", help="Output directory (default: ./deleted_rows)")
    args = parser.parse_args()

    output_dir = Path(args.output_dir)

    try:
        with ShardDatabase(args.db_path) as db:
            if args.table:
                table_names = [args.table]
            else:
                table_names = [
                    entry["name"] for entry in db.schema
                    if entry["type"] == "table" and entry.get("rootPage")
                ]

            total = 0
            for name in table_names:
                try:
                    count = export_table(db, name, output_dir / f"{name}.csv")
                except ShardError as exc:
                    print(f"  {name}: error — {exc}", file=sys.stderr)
                    continue
                if count:
                    print(f"  {name}: {count} deleted row(s) -> {output_dir / f'{name}.csv'}")
                total += count

            print(f"\n{total} deleted row(s) exported to {output_dir}/")

    except ShardError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 1

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
