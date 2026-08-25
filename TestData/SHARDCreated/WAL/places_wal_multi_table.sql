-- SHARD WAL Multi-Table Recovery Test
-- Database: places_wal_multi_table.db
-- Created by: create_places_wal_multi_table.py
--
-- Two tables are populated and records deleted from each before a RESTART
-- checkpoint.  Verifies that WAL frame correlation is per-table and that
-- recovered records from page 2 only land in _shard_recovered_moz_places,
-- while records from page 3 only land in _shard_recovered_moz_historyvisits.
--
-- Page layout (4096-byte pages):
--   Page 1 — sqlite_schema (skipped by recovery)
--   Page 2 — moz_places (single leaf, root)
--   Page 3 — moz_historyvisits (single leaf, root)
--
-- Expected recovery:
--   moz_places        — rowsAlive=7  (ids 1-3, 7-10),   rowsDeleted=3  (ids 4-6  wal_frame)
--   moz_historyvisits — rowsAlive=15 (ids 1-7, 13-20),  rowsDeleted=5  (ids 8-12 wal_frame)

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

CREATE TABLE moz_historyvisits (
    id         INTEGER PRIMARY KEY,
    place_id   INTEGER NOT NULL,
    visit_date INTEGER NOT NULL,
    visit_type INTEGER NOT NULL,
    from_visit INTEGER DEFAULT 0
);

-- WAL frame 5 (historical): moz_places rows 1-10 before any deletions
BEGIN;
INSERT INTO moz_places VALUES (1,'https://example1.com/','Title 1',1,1700000001,10);
INSERT INTO moz_places VALUES (2,'https://example2.com/','Title 2',2,1700000002,20);
INSERT INTO moz_places VALUES (3,'https://example3.com/','Title 3',3,1700000003,30);
INSERT INTO moz_places VALUES (4,'https://example4.com/','Title 4',4,1700000004,40);
INSERT INTO moz_places VALUES (5,'https://example5.com/','Title 5',5,1700000005,50);
INSERT INTO moz_places VALUES (6,'https://example6.com/','Title 6',6,1700000006,60);
INSERT INTO moz_places VALUES (7,'https://example7.com/','Title 7',7,1700000007,70);
INSERT INTO moz_places VALUES (8,'https://example8.com/','Title 8',8,1700000008,80);
INSERT INTO moz_places VALUES (9,'https://example9.com/','Title 9',9,1700000009,90);
INSERT INTO moz_places VALUES (10,'https://example10.com/','Title 10',10,1700000010,100);
COMMIT;

-- WAL frame 6 (historical): moz_historyvisits rows 1-20 before any deletions
BEGIN;
INSERT INTO moz_historyvisits VALUES (1,1,1700001000,2,0);
INSERT INTO moz_historyvisits VALUES (2,2,1700002000,3,0);
INSERT INTO moz_historyvisits VALUES (3,3,1700003000,4,0);
INSERT INTO moz_historyvisits VALUES (4,4,1700004000,5,0);
INSERT INTO moz_historyvisits VALUES (5,5,1700005000,1,0);
INSERT INTO moz_historyvisits VALUES (6,6,1700006000,2,0);
INSERT INTO moz_historyvisits VALUES (7,7,1700007000,3,0);
INSERT INTO moz_historyvisits VALUES (8,8,1700008000,4,0);
INSERT INTO moz_historyvisits VALUES (9,9,1700009000,5,0);
INSERT INTO moz_historyvisits VALUES (10,10,1700010000,1,0);
INSERT INTO moz_historyvisits VALUES (11,11,1700011000,2,0);
INSERT INTO moz_historyvisits VALUES (12,12,1700012000,3,0);
INSERT INTO moz_historyvisits VALUES (13,13,1700013000,4,0);
INSERT INTO moz_historyvisits VALUES (14,14,1700014000,5,0);
INSERT INTO moz_historyvisits VALUES (15,15,1700015000,1,0);
INSERT INTO moz_historyvisits VALUES (16,16,1700016000,2,0);
INSERT INTO moz_historyvisits VALUES (17,17,1700017000,3,0);
INSERT INTO moz_historyvisits VALUES (18,18,1700018000,4,0);
INSERT INTO moz_historyvisits VALUES (19,19,1700019000,5,0);
INSERT INTO moz_historyvisits VALUES (20,20,1700020000,1,0);
COMMIT;

-- WAL frame 7 (historical): moz_places after deleting rows 4-6 → wal_frame
BEGIN;
DELETE FROM moz_places WHERE id BETWEEN 4 AND 6;
COMMIT;

-- WAL frame 8 (historical): moz_historyvisits after deleting rows 8-12 → wal_frame
BEGIN;
DELETE FROM moz_historyvisits WHERE id BETWEEN 8 AND 12;
COMMIT;

-- PRAGMA wal_checkpoint(RESTART) — all pre-checkpoint frames become IsCurrent=False
--   after the next write changes the WAL salt.

-- WAL frame 1 (current): moz_places with new row 11 — triggers salt change
BEGIN;
INSERT INTO moz_places VALUES (11,'https://new.example.com/','New Page',0,1700099000,100);
COMMIT;

-- Process killed with SIGKILL — WAL file preserved as-is.
