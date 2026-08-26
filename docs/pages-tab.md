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
| Green | B-tree leaf table page |
| Blue | B-tree interior table page |
| Purple | B-tree leaf index page |
| Orange | Overflow page |
| Grey | Unknown / unclassified |

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
| Has deleted pointers | Pages with residual cell pointer values in the gap after the pointer array |
| Has deleted records | Pages where at least one deleted pointer successfully decoded as a valid record |
| AND / OR toggle | Controls whether multiple active filters must all match (AND) or any one is sufficient (OR) |

The page count label at the right of the filter bar updates to reflect how many pages pass the current filters.

## Detail panel (centre)

When a page is selected the detail panel shows four tabs.

### Page Information

Displays parsed header fields for the selected page — page type, size, cell count, first freeblock offset, cell content area start, and fragmented free byte count. A collapsible **Cell Pointers** section lists the raw offset values from the pointer array.

### Cells

One collapsible expander per live cell on the page. Each expander shows:
- Payload size, row ID, and record header bytes (highlighted in the hex view when expanded).
- Each field's serial type and decoded value.

Expanding a cell scrolls the hex view to the cell's first byte and places the cursor there.

### Freeblocks

One collapsible expander per freeblock in the page's freeblock chain. Shows the offset, size, and next-freeblock pointer for each block.

### Potential Deleted

Cells decoded from residual pointer values found in the unallocated gap after the pointer array. Only entries that passed structural validation are shown here. These are candidate deleted records — they have not been saved to the project yet.

Expanding an entry scrolls the hex view to the candidate cell's location.

### Unallocated Regions

The gap between the end of the cell pointer array and the start of the cell content area, plus any gaps between live cells. Each region shows its offset, total size, and the number of non-zero bytes it contains — useful for identifying areas that may hold remnants of deleted data.

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
