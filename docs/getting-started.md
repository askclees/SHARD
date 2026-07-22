# Getting Started

## Opening a database

- **File → Open Database** — opens a file picker filtered to `.db`, `.sqlite`, `.sqlite3`, and `.db3` files.
- **Drag and drop** — drag any SQLite file directly onto the SHARD window.

Once a file is loaded the status bar at the bottom shows the file path and basic statistics.

## Interface overview

The main area is divided into tabs that appear once a database is open:

| Tab | Purpose |
|---|---|
| **Database Header** | Parsed SQLite file header fields with a hex view of the first 100 bytes |
| **Pages** | Full page list with filters, structural detail, and hex view |
| **Schema** | All objects from `sqlite_master` (tables, indexes, views, triggers) |
| **Search** | Regex search across all page bytes |
| **Query** | SQL query runner against the shadow database (requires a project) |
| **WAL** | Write-Ahead Log viewer (appears when a WAL file is loaded) |

## Status bar

The status bar at the bottom shows:
- A summary of the loaded file (page count, page size, encoding).
- The active project folder path (when a project is open).
- WAL sync results when a WAL file is loaded alongside a project.

## Closing a file

**File → Close** unloads the current database and returns to the empty state. The WAL tab (if present) is also removed.
