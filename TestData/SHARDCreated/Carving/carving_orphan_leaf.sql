-- SHARD Orphan-Page Carving Test — single-table variant
-- Database: carving_orphan_leaf.db
-- Created by: create_carving_orphan_leaf.py
--
-- 500 rows split moz_places across several leaf pages. A bulk DELETE of ids
-- 200-400 causes SQLite's b-tree balancing to unlink and freelist TWO pages
-- outright, rather than merely emptying them in place. Freed pages' bytes are
-- never zeroed or reused, so their original content is fully recoverable —
-- but neither page carries a b-tree pointer linking it back to moz_places,
-- and no dropped-table sqlite_master entry points at either either (the
-- table itself is never dropped). This is exactly the "no specific-table
-- hint" case the orphan-page carver targets — including the case of a
-- freelist *trunk* page, which only has its first ~8-12 bytes overwritten
-- with the trunk header/leaf-pointer array; the rest of the page is
-- untouched and just as carveable as an ordinary freelist leaf page.
--
-- PRAGMA secure_delete=OFF is set explicitly: some SQLite builds (observed on
-- this platform) default it ON, which would zero the freed page immediately
-- and defeat the whole scenario.
--
-- Page layout after INSERT 1-500, DELETE 200-400 (4096-byte pages, verified
-- via `dotnet run --project SHARD.Cli -- pages` / `carve`):
--   Page 1 — sqlite_schema (skipped)
--   Page 2 — moz_places interior root
--   Page 3-6 — moz_places leaves, live survivors after rebalance
--   Page 7 — freelist trunk page, UNREACHABLE from the tree, still holds its
--            original 78 cells (ids 322-399) beyond the small trunk header
--   Page 8 — freelist leaf page, UNREACHABLE from the tree, byte-for-byte
--            intact: 78 cells, ids 400-477
--
-- Expected recovery (either loose or tight mode — only one candidate table
-- exists so there's no ambiguity to resolve either way):
--   moz_places — _shard_recovered_moz_places gains 156 rows (ids 322-477),
--   _recovery_method = 'orphan_carving'.
--
-- Of those 156, ids 322-400 were actually inside the DELETE range; ids
-- 401-477 remain live at a different, rebalanced page — page 8 is simply an
-- untouched stale snapshot from before the rebalance, so recovering it
-- surfaces some rows that are also still currently live elsewhere.

CREATE TABLE moz_places (
    id              INTEGER PRIMARY KEY,
    url             TEXT    NOT NULL,
    title           TEXT,
    visit_count     INTEGER DEFAULT 0,
    last_visit_date INTEGER,
    frecency        INTEGER DEFAULT -1
);

-- INSERT INTO moz_places VALUES (i, 'https://example{i}.com/', 'Title {i}', i, 1700000000+i, i*10)
--   for i in 1..500

-- DELETE FROM moz_places WHERE id BETWEEN 200 AND 400
