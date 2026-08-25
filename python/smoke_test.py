#!/usr/bin/env python3
"""
Manual end-to-end smoke test for the shard_native Python bindings.

Not part of the automated test suite (it needs a real published native library — see
README.md) — run it by hand once you've built SHARD.Native and pointed the loader at it:

    dotnet publish ../SHARD.Native -r <your-rid> -c Release
    SHARD_NATIVE_LIB=../SHARD.Native/bin/Release/net10.0/<rid>/publish/shard_native.so \
        python3 python/smoke_test.py

Exercises the same fixture (and asserts the same known-correct counts) as
SHARD.Core.Tests/SqliteRecoveryFacadeTests.cs and SHARD.Native.Tests/RecoveryApiTests.cs, so a
mismatch here points specifically at the native/ctypes marshalling layer rather than the
underlying recovery logic (which those two managed test suites already cover).
"""
import json
import os
import sqlite3
import sys
import tempfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from shard_native import ShardDatabase  # noqa: E402

REPO_ROOT = Path(__file__).resolve().parent.parent
FIXTURE = REPO_ROOT / "TestData" / "SHARDCreated" / "Carving" / "carving_orphan_leaf.db"


def main() -> None:
    if not FIXTURE.exists():
        sys.exit(f"Fixture not found: {FIXTURE}")

    with ShardDatabase(str(FIXTURE)) as db:
        header = db.header
        assert header["pageSize"] == 4096, header
        print("header OK:", json.dumps(header, indent=2))

        schema = db.schema
        assert any(e["name"] == "moz_places" for e in schema), schema
        print(f"schema OK: {len(schema)} entries")

        rows = db.rows("moz_places")
        assert len(rows) == 299, len(rows)
        print(f"rows OK: {len(rows)} live rows")

        carved = db.carve(mode="loose")
        assert len(carved) == 156, len(carved)
        print(f"carve OK: {len(carved)} carved rows")

        with tempfile.TemporaryDirectory() as tmp:
            output_path = os.path.join(tmp, "recovered.db")
            result = db.recover_to_file(output_path, process_wal=False, carve_mode="loose")
            assert result["carvedRecords"] == 156, result
            print("recover_to_file OK:", json.dumps(result, indent=2))

            # The whole point: the output is a normal SQLite file, openable with plain stdlib.
            conn = sqlite3.connect(output_path)
            (count,) = conn.execute("SELECT COUNT(*) FROM moz_places").fetchone()
            assert count == 299, count
            conn.close()
            print(f"stdlib sqlite3 open OK: {count} rows visible via plain SELECT")

    print("\nAll smoke checks passed.")


if __name__ == "__main__":
    main()
