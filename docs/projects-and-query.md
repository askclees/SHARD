# Projects & Query Tab

## What is a project?

A project is a folder that SHARD creates alongside your evidence file. It contains:

- **`project.json`** — a manifest recording the evidence file path and creation time.
- **A shadow SQLite database** — a copy of the evidence file's schema and data, rebuilt forensically from parsed page bytes rather than by opening the file with a SQLite library. Each row carries provenance columns (`_page_number`, `_cell_offset`, `_overflow_page`) recording exactly where in the evidence file the data came from.

Recovered deleted records are stored in `_shard_recovered_<tablename>` tables inside the shadow database.

## Creating a project

1. Open a database file.
2. Choose **File → Create Project**.
3. Select a folder to store the project in.
4. If a WAL file is detected alongside the evidence file, SHARD will offer to load it. Any records present in the WAL but not in the main database are automatically synced into the shadow database's live tables.

The status bar shows how many WAL records were synced after the project is created.

## Opening an existing project

**File → Open Project** — select the project folder. SHARD reopens the evidence file and reloads the shadow database. WAL sync runs automatically if a WAL file is already loaded.

## Query Tab

The Query tab provides a SQL interface against the shadow database. It is only active when a project is open.

### Table list

The left panel lists all user tables in the shadow database (internal `_shard_*` tables are hidden). Double-click a table name to run `SELECT * FROM <table>` immediately.

### Writing queries

Type any SQL into the query box and press **Ctrl+Enter** or click **Run**. Standard SQLite syntax is supported. The shadow database is read-write, so `INSERT`, `UPDATE`, and `DELETE` are possible, though modifying shadow data is not recommended for evidential integrity.

### Include recovered records

Checking **Include recovered records** silently modifies the executed query to `UNION ALL` results from the corresponding `_shard_recovered_<table>` table. A `_is_recovered` column is added to the result set (0 = live record, 1 = recovered). The query text in the editor is not changed.

This only applies when the query references a known table name. If no matching recovered table exists, the query runs unchanged.

### Results

Results are displayed in a grid. Column names are shown in the header row. The row count and execution status appear below the query box.
