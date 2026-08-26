# Carve Unknown Pages Tab

Tries every candidate table's schema — live tables, plus any dropped-table schemas recovered from `sqlite_master` history — against pages with no known owner (not reachable from any live B-tree). Useful for recovering records left behind after a `DROP TABLE`, or orphaned by other page reuse.

## Standard vs. Focused

- **Standard** (read-only reference list) matches on each candidate table's declared column types only. Simple, but ambiguous between similarly-shaped tables.
- **Focused** narrows matching to each column's actually-observed byte-length range first, resolving cases Standard must skip as ambiguous — at the risk of missing rows whose shape differs from what's currently live. Review and adjust the detected ranges before running; every column must have Min ≤ Max.

Both sections share the same per-table include/exclude checkbox — deselecting a table in Standard also hides and excludes it in Focused, and vice versa.

## Exporting and reusing carving parameters

Tuning Focused's ranges and table selection by hand can take a while, and that tuning is normally lost as soon as you close the database. If you expect to see a similar database again — a newer version of the same browser's history file, a different case using the same application, etc. — you can save that tuning and reuse it.

- **Export Parameters…** saves every candidate table's include/exclude state and Focused column ranges to a JSON file. This includes tables you've *excluded*, not just the ones you kept — see below for why that matters. It also captures any narrowing beyond byte-length that Focused derived from the observed data — e.g. a column found to always be exactly 0 or 1, or a column's NULL-ability — even though those aren't shown as separate controls in the UI, and each table's original `CREATE TABLE` statement, so its full schema (column order, types, primary key) can be reconstructed from the profile alone if needed later.
- **Load Parameters…** loads a previously-exported file and applies it to the currently-open database's candidate tables, then shows a summary of what happened.

### New tables vs. excluded tables

Because an exported profile records every table it knew about — including excluded ones — loading it back can tell two situations apart:

- **A table the profile explicitly excluded.** It stays excluded; nothing to review.
- **A table the profile never saw at all** (e.g. a new table added in a later version of the same application's schema). This is called out separately in the load summary — it wasn't a deliberate exclusion, so it's worth a look before you carve, in case it's something you'd want included.

The load summary also lists any table the profile mentions that isn't present in the current database, and any column a profile table had that no longer exists in the current schema (both are just informational — nothing else in the profile is affected).

Loading a profile from a completely different database (sharing no table names at all) isn't blocked — every current table simply shows up as "new," and every profile table shows up as "not in this database." The summary makes a mismatched profile obvious without needing a separate warning step.
