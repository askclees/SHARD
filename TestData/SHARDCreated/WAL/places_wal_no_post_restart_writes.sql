-- SHARD WAL No-Post-Restart-Writes (No False Positive) Test
-- Database: places_wal_no_post_restart_writes.db
-- Created by: create_places_wal_no_post_restart_writes.py
--
-- PRAGMA wal_checkpoint(RESTART) is issued with NO subsequent writes.
-- The WAL salt only changes on the first write after a RESTART, so all
-- frames in the file retain the original salt and are IsCurrent=True.
-- InsertWalDeletedRows must recover ZERO records even though deleted rows
-- physically exist in the WAL frames.
--
-- This is a correctness / no-false-positive test.
--
-- Page layout (4096-byte pages):
--   Page 1 — sqlite_schema (skipped by recovery)
--   Page 2 — moz_places (single leaf, root)
--
-- WAL frames (ALL IsCurrent=True — salt unchanged after RESTART):
--   Frame 1: pg=1 sqlite_schema (CREATE TABLE, not a leaf)
--   Frame 2: pg=2 empty moz_places
--   Frame 3: pg=2 moz_places rows 1-10
--   Frame 4: pg=2 moz_places rows 1-3, 7-10 (after delete 4-6)
--
-- Expected recovery:
--   moz_places — rowsAlive=7 (ids 1-3, 7-10), rowsDeleted=0

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

-- WAL frame 3 (all current): moz_places rows 1-10
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

-- WAL frame 4 (all current): moz_places after deleting rows 4-6
-- These rows MUST NOT be recovered — all frames are IsCurrent=True.
BEGIN;
DELETE FROM moz_places WHERE id BETWEEN 4 AND 6;
COMMIT;

-- PRAGMA wal_checkpoint(RESTART) — salt is NOT changed because no write follows.
-- All frames remain IsCurrent=True.

-- Process killed with SIGKILL immediately — no new write, salt unchanged.
