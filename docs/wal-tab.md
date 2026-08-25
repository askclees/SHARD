# WAL Tab

SQLite's Write-Ahead Log (WAL) is a file written alongside the main database (typically named `<database>.db-wal`) that holds committed but not yet checkpointed changes.

## Loading a WAL file

SHARD detects a WAL file automatically when you open a database or create a project. If one is found, you are offered the option to load it. You can also load one manually via **File → Load WAL File**.

When a WAL file is loaded a **WAL** tab appears. If a project is open, any records present in the WAL but absent from the main database are automatically inserted into the shadow database's live tables.

## WAL Header

A collapsible section at the top of the tab shows the parsed WAL file header:

| Field | Description |
|---|---|
| Magic Number | Identifies the WAL format (big-endian or little-endian checksums) |
| File Format Version | WAL format version number |
| Database Page Size | Page size used when the WAL was written |
| Checkpoint Sequence | Increments with each checkpoint |
| Salt-1 / Salt-2 | Salts used for checksum verification |
| Checksum-1 / Checksum-2 | Cumulative checksums for the WAL header |
| Frame Count | Total number of frames in the file |

## Frame list (left panel)

Lists every frame in the WAL. Each entry shows:
- Frame index and the database page number it covers.
- Page type (where parseable).
- The name of the table or index that owns the page (e.g. `messages`, `sqlite_master`). Pages not reachable from any known B-tree root show no label.
- A **COMMIT** label on frames that carry a non-zero database size in their header, indicating a transaction boundary.

Select a frame to view its contents on the right.

## Frame detail (centre panel)

### Page Data tab

Shows the parsed structure of the page carried by the selected frame — identical in layout to the **Cells**, **Freeblocks**, and **Unallocated** tabs in the Pages tab. Expanding a cell section scrolls the WAL hex view and places the cursor on the cell's first byte.

### Changes tab

Compares the selected frame's page against a baseline:
- If an earlier frame in the WAL covers the same page number, the comparison is against that frame.
- Otherwise, the comparison is against the corresponding page in the main database.

Changes are grouped into **Added records**, **Removed records**, and **Updated records**. Updated records show a field-by-field diff.

If the page type does not support comparison (e.g. overflow or interior pages), a placeholder message is shown.

#### Show whole transaction

By default the Changes tab shows only the selected frame's own page. Checking **Show whole
transaction** instead shows every page touched between the previous COMMIT frame (exclusive)
and the selected frame's own transaction's COMMIT frame (inclusive) — one expandable section
per page, each showing the same Added/Removed/Updated breakdown. This gives a single view of
everything one transaction changed, rather than clicking through its frames one page at a time.
Each page's baseline is the state immediately before the transaction began, even if that page
happens to have been written more than once within the same transaction.

## Hex view (right panel)

Displays the raw bytes of the frame's page, with the same highlights, offset toggle, and data inspector as the Pages tab hex view.
