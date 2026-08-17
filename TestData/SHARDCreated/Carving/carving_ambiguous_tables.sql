-- SHARD Orphan-Page Carving Test — ambiguous-schema / tight-mode variant
-- Database: carving_ambiguous_tables.db
-- Created by: create_carving_ambiguous_tables.py
--
-- table_a and table_b share byte-identical column shape (same declared
-- types/affinity/nullability, different table name only), so a loose,
-- affinity-only RecordStructure cannot tell their records apart. A bulk
-- DELETE of ids 150-450 from table_a only frees two of its leaf pages
-- outright (page 6, a freelist trunk, and page 7, a freelist leaf);
-- table_b is never touched.
--
-- table_a's `num` column is always the small integer 5 (encoded in 1 byte);
-- table_b's is always 99999999 (encoded in 4 bytes). Tight mode narrows each
-- candidate's RecordStructure to its own table's observed content-length
-- range, which disambiguates them even though loose mode cannot.
--
-- PRAGMA secure_delete=OFF is set explicitly — see carving_orphan_leaf.sql
-- for why this matters on this platform's SQLite build.
--
-- Page layout after INSERT + DELETE (4096-byte pages, verified via
-- `dotnet run --project SHARD.Cli -- pages` / `carve`):
--   Page 2 — table_a interior root
--   Page 3 — table_b interior root
--   Page 6 — freelist trunk page, UNREACHABLE from the tree, still holds
--            most of its original content beyond the small trunk header
--            (149 cells, ids 301-446, with a handful appearing twice —
--            a genuine overlapping stale snapshot from SQLite's own
--            rebalancing, see carving_ambiguous_tables.xml)
--   Page 7 — freelist leaf page, UNREACHABLE from the tree, byte-for-byte
--            intact: 54 cells, ids 447-500
--   (remaining pages: live table_a/table_b leaves)
--
-- Expected recovery:
--   Loose mode (candidates = table_a + table_b): 0 rows carved from either
--     page; every candidate record matches both tables' identically-shaped
--     loose structures, so nothing is attributed (ambiguousSkipped > 0).
--   Tight mode: 203 rows carved, all correctly attributed to table_a,
--     _recovery_method = 'orphan_carving'. table_b gains none.
--
-- Of the 203 rows, ids 301-450 were actually inside the DELETE range;
-- 451-500 remain live at a different, rebalanced page (see the identical
-- stale-snapshot note in carving_orphan_leaf.sql).

CREATE TABLE table_a (id INTEGER PRIMARY KEY, val TEXT NOT NULL, num INTEGER);
CREATE TABLE table_b (id INTEGER PRIMARY KEY, val TEXT NOT NULL, num INTEGER);

-- INSERT INTO table_a VALUES (i, 'row-{i}-padding-xx', 5)        for i in 1..500
-- INSERT INTO table_b VALUES (i, 'row-{i}-padding-xx', 99999999) for i in 1..500

-- DELETE FROM table_a WHERE id BETWEEN 150 AND 450
