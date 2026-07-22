# Search Tab

The Search tab lets you scan every byte of every page in the database using a regular expression.

## Running a search

1. Type a pattern in the search box. Patterns follow .NET regular expression syntax.
   - Literal text: `CREATE TABLE`
   - Hex byte sequences: `\x00\xFF`
   - Alternation: `DELETE|UPDATE`
2. Press **Enter** or click **Search**.

Results are grouped by page number. The summary line shows the total number of matches found.

## Navigating results

Each page group is a collapsible expander labelled with the page number, the owning table name (e.g. `[messages]`), and the hit count. Expanding it reveals individual hits.

Each hit shows:
- The byte offset and match length.
- A short ASCII preview of the matched bytes.
- Where the match falls within a table record: the row ID and field name (e.g. `Row 42 · display_name`). Falls back to a field index if the schema is unavailable, or `header` if the offset is in the cell header rather than a field value.

Click a hit to scroll the hex view on the right to that offset.

## Hex view

Works the same way as the hex view in the Pages tab — highlights, offset toggle, and data inspector are all available. The view loads the full page containing the selected hit.
