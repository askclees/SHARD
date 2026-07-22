# Schema Tab

The Schema tab lists every object recorded in `sqlite_master` — the internal SQLite catalogue that tracks all tables, indexes, views, and triggers in the database.

## Object list (left panel)

Each row shows:

| Column | Description |
|---|---|
| Type | `table`, `index`, `view`, or `trigger` |
| Name | Object name |
| Table | The table the object belongs to (for indexes and triggers) |
| Root Page | The B-tree root page number |
| SQL | The original `CREATE` statement (truncated; hover for the full text) |

Click a row to load the root page's bytes in the hex view.

## Hex view (right panel)

Shows the raw bytes of the root page for the selected schema object, with field highlights applied. The toolbar displays the page number and the name of any field the mouse is hovering over.

This is useful for verifying that a table's root page contains the expected structure, or for examining the raw bytes of an index page.
