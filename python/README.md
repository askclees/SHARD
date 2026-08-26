# shard-native

Python bindings for SHARD's forensic SQLite recovery engine (`SHARD.Core`), via a Native
AOT-published C ABI (`SHARD.Native`) — pure `ctypes`, no third-party Python dependency, no .NET
runtime required on the Python side.

## Setup

1. Build the native library once (requires a C toolchain — `clang` on Linux/macOS, MSVC on
   Windows — this is a .NET Native AOT requirement, not a Python one):

   ```
   dotnet publish ../SHARD.Native -r <your-rid> -c Release
   ```

   `<your-rid>` is one of `linux-x64`, `win-x64`, `osx-x64`, `osx-arm64` (match your machine).
   The published file lands under `../SHARD.Native/bin/Release/net10.0/<rid>/publish/`, named
   `shard_native.so` / `shard_native.dll` / `shard_native.dylib`.

2. Point Python at it — either:
   - copy it into `shard_native/native/` in this directory, or
   - set `SHARD_NATIVE_LIB=/full/path/to/shard_native.(so|dll|dylib)`, or
   - set `SHARD_NATIVE_LIB_DIR=/directory/containing/it`

3. Install this package locally:

   ```
   pip install -e python/
   ```

   (Not published to PyPI — this is a local/dev install path. Multi-platform wheels bundling
   the native library per-RID are natural future work once the AOT publish itself has been
   verified across platforms.)

## Usage

```python
from shard_native import ShardDatabase

with ShardDatabase("evidence.db") as db:
    print(db.header)
    print(db.schema)
    for row in db.rows("users"):
        print(row)

    # Try every live table's schema against pages with no known owner.
    carved = db.carve(mode="tight")

    # Or build a complete recovered SQLite database in one call:
    result = db.recover_to_file("recovered.db", carve_mode="loose")
    print(result)

    # Extract a table's own rows — live only by default, or with deleted rows folded in:
    live_only = db.table_rows("users")
    live_and_deleted = db.table_rows("users", include_deleted=True)

    # Or run SQL directly against a recovered copy — deleted rows live in
    # _shard_recovered_<table>, alongside the original table's live rows:
    rows = db.query("""
        SELECT id, name FROM users
        UNION ALL
        SELECT id, name FROM _shard_recovered_users
    """)

# "recovered.db" is a normal SQLite file now — open it with the stdlib sqlite3 module.
import sqlite3
conn = sqlite3.connect("recovered.db")
```

See `smoke_test.py` for a runnable end-to-end example against one of the repo's test fixtures,
and `examples/` for more complete, runnable scripts.

## API reference

Every failure (bad path, unknown table, corrupted file, ...) raises `shard_native.ShardError`
with a human-readable message — there's no separate exception type per failure mode.

### `ShardDatabase(path)`

Opens a session against one evidence file. Use as a context manager (`with ShardDatabase(...) as
db:`) or call `.close()` explicitly — the underlying handle is just bookkeeping (each call
re-opens/re-parses the file), so nothing expensive is held open between calls.

| Member | Returns | Notes |
|---|---|---|
| `.header` | `dict` | `pageSize`, `textEncoding`, `sqliteVersion`, `databaseSizeInPages`, `totalFreelistPages`, ... |
| `.schema` | `list[dict]` | Every `sqlite_master` entry: `type` ("table"/"index"/"view"/"trigger"), `name`, `tableName`, `rootPage`, `sql`, `pageNumber`, `cellOffset`. |
| `.pages` | `list[dict]` | Every page: `pageNumber`, `type`, `tableName` (if known), `deletedCellCount`. |
| `.rows(table_name)` | `list[dict]` | Live rows: `rowId`, `pageNumber`, `cellOffset`, `fields` (dict keyed by the table's own column names — **not** camelCased, unlike the other keys here). |
| `.deleted_rows(table_name)` | `list[dict]` | Same shape as `.rows()`, for recoverable deleted/freeblock rows still within the table's own B-tree. |
| `.carve(mode="loose", tables=None)` | `list[dict]` | Read-only scan of pages with no known owner. `mode` is `"loose"` (declared column types only) or `"tight"` (narrowed to each table's own observed data — fewer false positives). Each result adds `tableName` to the row shape above. Optionally restrict candidates via `tables=["users", "notes"]`. |
| `.recover_to_file(output_path, process_wal=True, carve_mode=None, carve_table_filter=None)` | `dict` | Builds a complete recovered SQLite database at `output_path` (openable with the stdlib `sqlite3` module — no shard_native needed to read it back). Returns `outputPath`, `warnings`, `tables` (list of `{tableName, liveRowCount, recoveredRowCount}`), `walRecordsInserted`, `carvedRecords`, `carveAmbiguousSkipped`. Pass `carve_mode="loose"` or `"tight"` to also carve orphan pages into the output; omit it (the default) to skip carving. |
| `.query(sql, params=(), *, process_wal=True, carve_mode=None, carve_table_filter=None)` | `list[dict]` | Runs a SQL statement against a fully recovered copy of this database via the stdlib `sqlite3` module. Live rows stay under their original table name; recovered/deleted rows land in `_shard_recovered_<table>` alongside them, so a query can `UNION`/`JOIN` live and recovered data directly (see the metadata columns below). The recovered copy is built once, in a temp file, on first call (or again if called with different `process_wal`/`carve_mode`/`carve_table_filter` than last time) and reused across calls with matching options; cleaned up automatically on `close()`. `params` is passed straight through to `sqlite3`'s parameter binding (`?` placeholders). Non-`SELECT` statements return `[]`. |
| `.table_rows(table_name, *, include_deleted=False, process_wal=True, carve_mode=None, carve_table_filter=None)` | `list[dict]` | A `query()`-based shortcut: `table_name`'s rows, live only by default, or with recoverable deleted/freeblock rows folded in via `include_deleted=True`. Columns are exactly `table_name`'s own (read via `PRAGMA table_info`) — not `_shard_recovered_<table_name>`'s extra `_recovery_method` column, unlike a hand-written `SELECT *` `UNION`. Uses the same cached-recovered-copy behavior as `query()`. |

A `BLOB` field's value comes back as Python `bytes`.

`_shard_recovered_<table>` has every one of the table's own columns, plus `_page_number`, `_cell_offset`, `_overflow_page`, and `_recovery_method` — so `SELECT *` against it will include those; list the table's own columns explicitly if you're `UNION`-ing with the live table.

## Examples

Runnable, single-purpose scripts in `examples/` (each takes `--help`):

| Script | What it does |
|---|---|
| `inspect_database.py evidence.db` | Prints the header, schema, and live/deleted row counts per table — a quick first look at a file. |
| `export_deleted_rows.py evidence.db [--table users]` | Exports recovered deleted rows to CSV, one file per table (BLOBs hex-encoded). |
| `full_recovery.py evidence.db recovered.db [--carve-mode tight]` | Runs a complete recovery pass, prints a summary, and verifies the output via the stdlib `sqlite3` module. |
| `carve_report.py evidence.db [--output carved.json]` | Compares loose vs. tight orphan-page carving side by side, optionally writing the tight-mode results to JSON. |
| `query_deleted_records.py evidence.db users "SELECT * FROM {table} WHERE id > 100"` | Runs an arbitrary SQL query against a table's live rows, its recovered deleted rows, and both combined — `{table}`/`{recovered_table}` placeholders let one query template target either or both. |
| `extract_table.py evidence.db users [--include-deleted]` | Dumps a table's own rows — live only by default, or with recoverable deleted rows folded in via `--include-deleted`. |

## Testing

`smoke_test.py` is a quick one-file sanity check. `tests/test_corpus.py` is more thorough — it
replicates `SHARD.Core.Tests/CorpusTests.cs`'s live-row and deleted-row checks against the
[SQLite Forensic Corpus v2.0](../TestData/Corpus) through the Python bindings instead of calling
`SHARD.Core` directly, proving the C ABI/ctypes layer doesn't lose or distort anything relative
to the .NET engine. It's stdlib-only (`unittest`), so no extra install is needed:

```
SHARD_NATIVE_LIB=/path/to/shard_native.so python3 -m unittest discover -s tests -v
```

It skips automatically if the corpus (`../TestData/Corpus`, or `$SQLITE_CORPUS_PATH`) or the
native library isn't found. Any failures should exactly match whatever `dotnet test --filter
"FullyQualifiedName~CorpusTests"` reports in `SHARD.Core.Tests` at the same commit — the corpus
has a handful of known data-quality edge cases (documented via `GenerateCorpusReport`) that both
suites are expected to fail identically on; a Python-only failure would indicate a real bindings
bug.
