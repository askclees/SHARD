# Pages Tab

The Pages tab is the primary workspace for forensic analysis. It is divided into three panels.

## Page list (left panel)

Lists every page in the database. Each entry shows:
- A colour swatch indicating the page type.
- The page number.
- The page type (e.g. `BTreeLeafTable`, `Overflow`).
- The associated table name, where known.

Click a page to load its detail in the centre and right panels.

### Page type colours

| Colour | Type |
|---|---|
| Light blue | B-tree leaf table page |
| Blue | B-tree interior table page |
| Light green | B-tree leaf index page |
| Green | B-tree interior index page |
| Red | Freelist trunk page |
| Dark red | Freelist leaf page |
| Orange | Overflow page |
| Violet | Pointer-map page (auto-vacuum databases) |
| Dark grey | Lock-byte page (databases over 1 GiB) |
| Grey | Unknown / unclassified |

Pointer-map and lock-byte pages are identified by their page number, not their contents. A pointer-map page's first byte can be 0x02 or 0x05, the same as a B-tree interior page's type byte, so reading it by content alone would misclassify it.

- **Pointer-map pages** exist only when auto-vacuum is on. Page 2 is the first, and each one is followed by the pages it describes: one 5-byte entry per page, giving the page's type (root, free, overflow or B-tree page) and the page that points to it.
- **The lock-byte page** is the page holding file offset 1 GiB. SQLite uses it only for file locks and never writes to it.

## Filter bar

The filter bar sits above the page list and is split into two rows.

**Row 1 — Type toggles:** Click any page type button to show only pages of that type. Multiple types can be active simultaneously.

**Row 2 — Additional filters:**

| Filter | Effect |
|---|---|
| Table | Restricts the list to pages belonging to a specific table |
| Has unallocated | Pages with any unallocated gap region |
| Unalloc ≥ N bytes | Pages whose unallocated region is at least N bytes |
| Non-zero ≥ N bytes | Pages whose unallocated region contains at least N non-zero bytes |
| unexplained only | With Non-zero ≥ N: counts only the non-zero bytes recovery leaves unexplained (see [Page Information](#page-information)), and includes pages no B-tree uses — freelist, unknown and orphaned pages — each counted as one region covering the whole page. Set N to 1 to list every page with anything left unexplained. Recovery runs on every page the first time it's turned on, so on a large database this can take a moment. |
| Has deleted pointers | Pages with residual cell pointer values in the gap after the pointer array |
| Has deleted records | Pages where at least one deleted pointer successfully decoded as a valid record |
| AND / OR toggle | Controls whether multiple active filters must all match (AND) or any one is sufficient (OR) |

The page count label at the right of the filter bar updates to reflect how many pages pass the current filters.

## Detail panel (centre)

When a page is selected the detail panel shows four tabs.

### Page Information

Displays parsed header fields for the selected page — page type, size, cell count, first freeblock offset, cell content area start, and fragmented free byte count.

Below them, when there is anything to explain, is a count of the non-zero bytes recovery has accounted for:

- **Unallocated space after recovery** (table leaf pages): of the non-zero bytes in the page's unallocated regions, how many are **explained** and how many are **unexplained**.
- **Whole page (no B-tree uses it)** (freelist, unknown and orphaned pages — the ones [Carve Unknown Pages](carve-unknown-pages.md) scans): the same count over the whole page, with any records carved from it explained.
- **Whole page after carving** (another page records were carved from as a whole, such as a dropped table's freed root page): the same.

A byte is explained if a recovered record covers it (deleted, carved, freeblock, annotated or carved from the whole page), or if it is part of SQLite's own structure: the page header and cell-pointer array, stale pointer slots after the array, freeblock headers, a freelist trunk's header and leaf list, the reserved bytes at the end of each page, or (on a carved page) a surviving cell pointer to a carved record. Unexplained bytes are data recovery couldn't read — a damaged or partly overwritten record, a record from a table it wasn't given, or something that isn't a record. The count updates when you remove a recovered record or annotate a new one. A collapsible **Cell Pointers** section lists the raw offset values from the pointer array. For a pointer-map page, the same section is titled **Pointer Map Entries** and lists each page the map describes, with its type and parent page.

### Cells

One collapsible expander per live cell on the page. Each expander shows:
- Payload size, row ID, and record header bytes (highlighted in the hex view when expanded).
- Each field's serial type and decoded value.

Expanding a cell scrolls the hex view to the cell's first byte and places the cursor there.

### Freeblocks

One collapsible expander per freeblock in the page's freeblock chain. Shows the offset, size, and next-freeblock pointer for each block.

### Recovered

Records recovered from the page: **Carved** (from unallocated space), **Freeblock**, **Annotated** (by hand), and **Carved (from the whole page)** — records Carve Unknown Pages found on this page, or carved from a dropped table's freed root page. Each can be removed with ✕ if it is a false positive.

### Potential Deleted

Cells decoded from residual pointer values found in the unallocated gap after the pointer array. Only entries that passed structural validation are shown here. These are candidate deleted records — they have not been saved to the project yet.

Expanding an entry scrolls the hex view to the candidate cell's location.

### Unallocated Regions

The gap between the end of the cell pointer array and the start of the cell content area, plus any gaps between live cells. Each region shows its offset, total size, and the number of non-zero bytes it contains — useful for identifying areas that may hold remnants of deleted data. **Unexplained** shows how many of those bytes are still unexplained after recovery (see [Page Information](#page-information)).

The page list's unallocated filters use the counts taken before recovery unless **unexplained only** is ticked.

## Hex view (right panel)

Displays the raw bytes of the selected page.

- **Highlights** — coloured regions mark parsed fields (page header, cell pointers, payload sizes, row IDs, record headers, field values). The toggle button turns highlights on or off.
- **Dec Offsets** — switches offset labels from hexadecimal to decimal.
- **Offset / Go** — type a byte offset (decimal, or `0x`-prefixed hex) and press Enter or click **Go** to jump straight there, cursor included — useful when the scrollbar alone is too imprecise, or you already know the offset from elsewhere (a cell/field offset shown in another panel, or `shard-cli`/Python output).
- **Cursor** — the currently selected byte is shown with an amber background. The offset updates the Data Inspector panel.
- **Hover label** — moving the mouse over a highlighted region shows the field name in the toolbar.

### Data Inspector

A panel to the right of the hex view interprets the bytes at the cursor position as various data types (int8, int16, int32, int64, float, double, text).

### Decoding a deleted record

Right-click any byte in the hex view and choose **Try to decode record at this location**. SHARD will attempt to parse a B-tree leaf cell starting at that offset.

- If the offset falls inside a live record, a warning is shown first.
- A result dialog displays the decoded fields (using column names from the schema if available) or the reasons the decode failed.
- If the result is valid and a project is open, click **Add to project** to save the recovered record to the shadow database.
