-- SHARD WAL Multi-Page B-Tree Recovery Test
-- Database: places_wal_multi_page.db
-- Created by: create_places_wal_multi_page.py
--
-- 100 rows force a B-tree page split: page 2 becomes an interior root and
-- two leaf pages are allocated.  Records are deleted from both leaf pages
-- before a RESTART checkpoint, proving SHARD can correlate non-root leaf
-- WAL frames to the correct table and recover deleted records from each.
--
-- Page layout after INSERT 1-100 (4096-byte pages):
--   Page 1 — sqlite_schema (skipped)
--   Page 2 — moz_places interior root (1 divider key)
--   Page 3 — moz_places leaf,  85 cells, rowids 1-85
--   Page 4 — moz_places leaf,  15 cells, rowids 86-100
--
-- Deletions before checkpoint:
--   rows 40-50  → 11 records from page-3 leaf
--   rows 90-95  →  6 records from page-4 leaf
--
-- After the second delete SQLite rebalanced the B-tree:
--   Page 3: rowids 1-39, 51-56  (45 cells after rebalance)
--   Page 4: rowids 57-89, 96-100 (38 cells after rebalance)
--
-- Historical WAL frames (IsCurrent=False):
--   Frame 5:  pg=3, 85 cells, rowids 1-85   ← pre-deletion snapshot
--   Frame 6:  pg=4, 15 cells, rowids 86-100  ← pre-deletion snapshot
--   Frame 7:  pg=3, 74 cells, rowids 1-39,51-85 (after delete 40-50)
--   Frame 8:  pg=2  interior — skipped by recovery
--   Frame 9:  pg=3, 45 cells, rowids 1-39,51-56 (post-rebalance)
--   Frame 10: pg=4, 38 cells, rowids 57-89,96-100 (post-rebalance)
--
-- SHARD recovers deleted rows from the first historical snapshot of each leaf:
--   Frame 5 → rows 40-50 not in live → 11 wal_frame records
--   Frame 6 → rows 90-95 not in live →  6 wal_frame records
--
-- Expected recovery:
--   moz_places — rowsAlive=83 (1-39, 51-89, 96-100), rowsDeleted=17 (all wal_frame)

PRAGMA journal_mode=WAL;
PRAGMA wal_autocheckpoint=0;

CREATE TABLE moz_places (
    id              INTEGER PRIMARY KEY,
    url             TEXT    NOT NULL,
    title           TEXT,
    visit_count     INTEGER DEFAULT 0,
    last_visit_date INTEGER,
    frecency        INTEGER DEFAULT -1
);

-- WAL frames 5+6 (historical pre-deletion): rows 1-100 across two leaf pages
BEGIN;
INSERT INTO moz_places VALUES (1,'https://example1.com/','Title 1',1,1700000001,10);
-- ... rows 2-99 follow the same pattern ...
INSERT INTO moz_places VALUES (100,'https://example100.com/','Title 100',100,1700000100,1000);
COMMIT;

-- WAL frame 7 (historical): page-3 leaf after deleting rows 40-50
BEGIN;
DELETE FROM moz_places WHERE id BETWEEN 40 AND 50;
COMMIT;

-- WAL frames 8-10 (historical): B-tree rebalance after deleting rows 90-95
-- from page-4 leaf (9 survivors trigger rebalance across pages 3 and 4)
BEGIN;
DELETE FROM moz_places WHERE id BETWEEN 90 AND 95;
COMMIT;

-- PRAGMA wal_checkpoint(RESTART) — all pre-checkpoint frames become IsCurrent=False
--   after the next write changes the WAL salt.

-- WAL frame 1 (current): new row 101 inserted into page-4 leaf — triggers salt change
BEGIN;
INSERT INTO moz_places VALUES (101,'https://new.example.com/','New Page',0,1700099000,100);
COMMIT;

-- Process killed with SIGKILL — WAL file preserved as-is.
