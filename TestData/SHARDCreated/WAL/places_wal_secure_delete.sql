-- SHARD WAL Deleted-Record Recovery Test Data (secure_delete variant)
-- Database: places_wal_secure_delete.db
-- Created by: create_places_wal_secure_delete.py
--
-- Scenario: Firefox-like places database with PRAGMA secure_delete=ON.
-- Rows are deleted in three separate stages, each producing a distinct WAL frame.
-- secure_delete zeros the freed cell space on every delete, so page-level carving
-- (DeletedCells / CarvedCells / FreeblockCells) recovers nothing from the main DB.
-- SHARD's WAL analysis reads Frame 3 (which holds all 30 original rows) and
-- recovers all 15 deleted rows as wal_frame records.
--
-- Expected SHARD analysis:
--   Live rows (main DB, raw):         15  (ids 1-5, 11-15, 21-25)
--   Page-level carving:                0  (secure_delete zeroed freed space)
--   WAL-recovered (wal_frame):        15  (ids 6-10, 16-20, 26-30)

PRAGMA journal_mode = WAL;
PRAGMA wal_autocheckpoint = 0;
PRAGMA secure_delete = ON;

CREATE TABLE moz_places (
    id              INTEGER PRIMARY KEY,
    url             TEXT    NOT NULL,
    title           TEXT,
    visit_count     INTEGER DEFAULT 0,
    last_visit_date INTEGER,
    frecency        INTEGER DEFAULT -1
);

-- Phase 1: insert rows 1-30 → WAL frame 3 (historical after checkpoint)
-- This is the only frame that contains all deleted rows intact.
BEGIN;
INSERT INTO moz_places VALUES  (1,  'https://example1.com/',  'Title 1',   1, 1700000001,  10);
INSERT INTO moz_places VALUES  (2,  'https://example2.com/',  'Title 2',   2, 1700000002,  20);
INSERT INTO moz_places VALUES  (3,  'https://example3.com/',  'Title 3',   3, 1700000003,  30);
INSERT INTO moz_places VALUES  (4,  'https://example4.com/',  'Title 4',   4, 1700000004,  40);
INSERT INTO moz_places VALUES  (5,  'https://example5.com/',  'Title 5',   5, 1700000005,  50);
INSERT INTO moz_places VALUES  (6,  'https://example6.com/',  'Title 6',   6, 1700000006,  60);
INSERT INTO moz_places VALUES  (7,  'https://example7.com/',  'Title 7',   7, 1700000007,  70);
INSERT INTO moz_places VALUES  (8,  'https://example8.com/',  'Title 8',   8, 1700000008,  80);
INSERT INTO moz_places VALUES  (9,  'https://example9.com/',  'Title 9',   9, 1700000009,  90);
INSERT INTO moz_places VALUES (10, 'https://example10.com/', 'Title 10',  10, 1700000010, 100);
INSERT INTO moz_places VALUES (11, 'https://example11.com/', 'Title 11',  11, 1700000011, 110);
INSERT INTO moz_places VALUES (12, 'https://example12.com/', 'Title 12',  12, 1700000012, 120);
INSERT INTO moz_places VALUES (13, 'https://example13.com/', 'Title 13',  13, 1700000013, 130);
INSERT INTO moz_places VALUES (14, 'https://example14.com/', 'Title 14',  14, 1700000014, 140);
INSERT INTO moz_places VALUES (15, 'https://example15.com/', 'Title 15',  15, 1700000015, 150);
INSERT INTO moz_places VALUES (16, 'https://example16.com/', 'Title 16',  16, 1700000016, 160);
INSERT INTO moz_places VALUES (17, 'https://example17.com/', 'Title 17',  17, 1700000017, 170);
INSERT INTO moz_places VALUES (18, 'https://example18.com/', 'Title 18',  18, 1700000018, 180);
INSERT INTO moz_places VALUES (19, 'https://example19.com/', 'Title 19',  19, 1700000019, 190);
INSERT INTO moz_places VALUES (20, 'https://example20.com/', 'Title 20',  20, 1700000020, 200);
INSERT INTO moz_places VALUES (21, 'https://example21.com/', 'Title 21',  21, 1700000021, 210);
INSERT INTO moz_places VALUES (22, 'https://example22.com/', 'Title 22',  22, 1700000022, 220);
INSERT INTO moz_places VALUES (23, 'https://example23.com/', 'Title 23',  23, 1700000023, 230);
INSERT INTO moz_places VALUES (24, 'https://example24.com/', 'Title 24',  24, 1700000024, 240);
INSERT INTO moz_places VALUES (25, 'https://example25.com/', 'Title 25',  25, 1700000025, 250);
INSERT INTO moz_places VALUES (26, 'https://example26.com/', 'Title 26',  26, 1700000026, 260);
INSERT INTO moz_places VALUES (27, 'https://example27.com/', 'Title 27',  27, 1700000027, 270);
INSERT INTO moz_places VALUES (28, 'https://example28.com/', 'Title 28',  28, 1700000028, 280);
INSERT INTO moz_places VALUES (29, 'https://example29.com/', 'Title 29',  29, 1700000029, 290);
INSERT INTO moz_places VALUES (30, 'https://example30.com/', 'Title 30',  30, 1700000030, 300);
COMMIT;

-- Stage 1: delete rows 6-10 → WAL frame 4
-- secure_delete zeroes the freed cells before writing this frame.
-- Rows 6-10 are now irrecoverable by page-level carving from frame 4 onwards.
BEGIN;
DELETE FROM moz_places WHERE id BETWEEN 6 AND 10;
COMMIT;

-- Stage 2: delete rows 16-20 → WAL frame 5
BEGIN;
DELETE FROM moz_places WHERE id BETWEEN 16 AND 20;
COMMIT;

-- Stage 3: delete rows 26-30 → WAL frame 6
BEGIN;
DELETE FROM moz_places WHERE id BETWEEN 26 AND 30;
COMMIT;

-- Checkpoint: merge all WAL frames into main DB and reset WAL salt.
-- Main DB now holds rows 1-5, 11-15, 21-25 with all freed space zeroed.
PRAGMA wal_checkpoint(RESTART);

-- Insert rows 31-32 → new WAL frame 1 (new salt; old frames become IsCurrent=False).
BEGIN;
INSERT INTO moz_places VALUES (31, 'https://new1.example.com/', 'New Page 1', 0, 1700099000, 100);
INSERT INTO moz_places VALUES (32, 'https://new2.example.com/', 'New Page 2', 0, 1700099001, 101);
COMMIT;

-- Process killed (SIGKILL) here to preserve the WAL.
-- os.kill(os.getpid(), signal.SIGKILL)
