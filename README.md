# SHARD

[![Release](https://img.shields.io/github/v/release/askclees/SHARD)](https://github.com/askclees/SHARD/releases)
[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-blue.svg)](LICENSE)

**SHARD** (SQLite Forensic Analyser) is a forensic tool for examining SQLite database files at the byte level. It parses the raw file format directly — not through SQLite itself — so it can work on databases that are corrupted, truncated, or partially overwritten, and recover data a normal SQLite connection can no longer see:

- **Live and deleted records** — browses B-tree pages directly and recovers deleted/freeblock records still present in unallocated page space.
- **WAL recovery** — recovers deleted-record history from a database's `-wal` sidecar file, in addition to the main database file.
- **Orphan-page carving** — recovers records from pages whose owning table is no longer known (e.g. after a `DROP TABLE`), by matching raw page bytes against every candidate table's schema.
- **SQL querying** over recovered data via a generated "shadow" database.

It ships four ways to use the same recovery engine:

| | |
|---|---|
| **[`SHARD`](SHARD/)** | Cross-platform Avalonia desktop app — the primary way to inspect a file, browse pages/hex, and drive recovery interactively. See the [User Guide](docs/index.md). |
| **[`SHARD.Cli`](SHARD.Cli/)** (`shard-cli`) | Scriptable command-line inspector — dump rows, deleted rows, schema, pages, or carve results as JSON or text. |
| **[`SHARD.Core`](SHARD.Core/)** | The recovery engine itself, usable as a NuGet package by other .NET tools via `SqliteRecoveryFacade`. |
| **[`SHARD.Native`](SHARD.Native/) + [`python/`](python/)** | A Native AOT-published C ABI shared library, with pure-`ctypes` Python bindings on top — for use from Python or any other language with C FFI. |

## Building

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```
dotnet build SHARD.sln
```

### Desktop app

```
dotnet run --project SHARD
```

### Command-line tool

```
dotnet run --project SHARD.Cli -- --help
dotnet run --project SHARD.Cli -- rows evidence.db users
dotnet run --project SHARD.Cli -- deleted evidence.db users
dotnet run --project SHARD.Cli -- carve evidence.db --mode tight
```

### `SHARD.Core` as a library

```csharp
using SHARD.Core.Recovery;

var result = SqliteRecoveryFacade.Recover("evidence.db", "recovered.db",
    new RecoveryOptions(ProcessWal: true, CarveMode: CarveMode.Tight));
```

`recovered.db` is a normal SQLite database afterwards — open it with any SQLite client. See `SqliteRecoveryFacade` for the full API (`GetHeader`, `GetSchema`, `GetRows`, `GetDeletedRows`, `CarveUnknownPages`, `Recover`).

### Python

```
dotnet publish SHARD.Native -r <your-rid> -c Release
pip install -e python/
```

```python
from shard_native import ShardDatabase

with ShardDatabase("evidence.db") as db:
    print(db.rows("users"))
    db.recover_to_file("recovered.db", carve_mode="tight")
```

See [`python/README.md`](python/README.md) for setup details.

## Testing

```
dotnet test SHARD.sln --filter "Category!=Corpus"
```

Corpus-validation tests (`Category=Corpus`) run against the SQLite Forensic Corpus dataset checked into [`TestData/Corpus`](TestData/Corpus) and are excluded by default since they're slower and partly informational — see [`SHARD.Core.Tests/CorpusTests.cs`](SHARD.Core.Tests/CorpusTests.cs).

## License

[GPL v3](LICENSE)
