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

# "recovered.db" is a normal SQLite file now — open it with the stdlib sqlite3 module.
import sqlite3
conn = sqlite3.connect("recovered.db")
```

See `smoke_test.py` for a runnable end-to-end example against one of the repo's test fixtures.

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
