# Projects & Query Tab

## What is a project?

A project is a folder that SHARD creates alongside your evidence file. It contains:

- **`project.json`** — a manifest recording the evidence file path and creation time.
- **A shadow SQLite database** — a copy of the evidence file's schema and data, rebuilt forensically from parsed page bytes rather than by opening the file with a SQLite library. Each row carries provenance columns (`_page_number`, `_cell_offset`, `_overflow_page`) recording exactly where in the evidence file the data came from. Recovered rows also carry `_recovery_method` (how the row was found) and `_wal_frame` (the WAL frame it came from, or NULL for the database file).

Recovered deleted records are stored in `_shard_recovered_<tablename>` tables inside the shadow database.

## Creating a project

1. Open a database file.
2. Choose **File → Create Project**.
3. Select a folder to store the project in.
4. If a WAL file is detected alongside the evidence file, the shadow database's live tables hold what SQLite itself returns: the database file with the WAL's committed frames applied. Rows the WAL has deleted or changed since the last checkpoint are kept in `_shard_recovered_<tablename>`, taken from the database file's own version of the page:
   - `db_file_deleted_in_wal`: live in the database file, deleted by a committed WAL transaction.
   - `db_file_previous_version`: the database file's version of a row the WAL has since changed.

   A WAL loaded later with **File → Load WAL File…** is applied to an already-built shadow database instead: its committed rows are added to the live tables.

5. If a rollback journal (`-journal` file) is detected instead, each journal record is an earlier image of a database page. Records in those images that no longer exist in the database are added to `_shard_recovered_<tablename>` with `_recovery_method` = `journal_page`; older versions of records that still exist but have since changed are added as `journal_previous_version`.

The status bar shows how many WAL or journal records were recovered after the project is created.

## Opening an existing project

**File → Open Project** — select the project folder. SHARD reopens the evidence file and reloads the shadow database. WAL and journal recovery run automatically if a WAL or journal file is loaded; records already recovered into the project are not added twice.

## Query Tab

The Query tab provides a SQL interface against the shadow database. It is only active when a project is open.

### Table list

The left panel lists all user tables in the shadow database (internal `_shard_*` tables are hidden). Double-click a table name to run `SELECT * FROM <table>` immediately.

### Writing queries

Type any SQL into the query box and press **Ctrl+Enter** or click **Run**. Standard SQLite syntax is supported. The shadow database is read-write, so `INSERT`, `UPDATE`, and `DELETE` are possible, though modifying shadow data is not recommended for evidential integrity.

### Include recovered records

Checking **Include recovered records** silently modifies the executed query to `UNION ALL` results from the corresponding `_shard_recovered_<table>` table. A `_is_recovered` column is added to the result set (0 = live record, 1 = recovered). The query text in the editor is not changed.

This only applies when the query references a known table name. If no matching recovered table exists, the query runs unchanged.

### Row states

Every row in a live or recovered table has three state columns, describing that exact version of the row (its rowid and values):

| Column | Value | Meaning |
|---|---|---|
| `_db_state` | 1 | Live in the database file with these values |
| | 3 | The same rowid is live in the database file with other values |
| | 2 | Found only in the database file's deleted data |
| | 0 | Not in the database file |
| `_log_state` | NULL | No WAL, or the WAL doesn't cover this row (it appears in no frame and its page was never rewritten) |
| | 1 | Live in the WAL's committed state, and the WAL holds it |
| | 3 | The same rowid is live in the committed state with other values |
| | 2 | In the WAL but not live in its committed state, or live in the database file and deleted by the WAL (whether or not its bytes survive) |
| | 0 | The WAL covers this row's page but the row isn't in it, and wasn't live before |
| `_is_current` | 1 | This is the version SQLite returns now |

A WAL is newer than the database file, so for a row the WAL updated, the new version is `_db_state = 3, _log_state = 1` and the file's old version is `_db_state = 1, _log_state = 3`. `_shard_info` records which transaction log the states describe (`transaction_log`: `wal`, `journal` or `none`). Rollback journal states aren't computed yet: with a journal, `_log_state` is NULL.

The **Row state** list next to the scope buttons filters any query by these columns. It offers: Current, Deleted by the WAL, Added by the WAL, Changed by the WAL (earlier version), Only ever in the WAL, and Deleted in the database file. It wraps the query as `SELECT * FROM (query) WHERE …`, so the query's results must include the state columns, as `SELECT *` does.

### Results

Results are displayed in a grid. Column names are shown in the header row. The row count and execution status appear below the query box.
