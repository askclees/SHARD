-- SHARD WAL Deleted-Record Recovery Test Data
-- Database: places_wal.db
-- Created by: create_places_wal.py
--
-- Scenario: Firefox-like places database with WAL history.
-- The process is killed (SIGKILL) after the final INSERT so the WAL is not
-- checkpointed or deleted on connection close.
--
-- Expected SHARD analysis:
--   Live rows (main DB, raw):   15  (ids 1-5, 11-20; ids 11-13 have updated values)
--   WAL-recovered deleted rows:  8  (5 wal_frame + 3 wal_previous_version)

PRAGMA journal_mode = WAL;
PRAGMA wal_autocheckpoint = 0;

CREATE TABLE moz_places (
    id              INTEGER PRIMARY KEY,
    url             TEXT    NOT NULL,
    title           TEXT,
    visit_count     INTEGER DEFAULT 0,
    last_visit_date INTEGER,
    frecency        INTEGER DEFAULT -1
);

-- Phase 1: insert rows 1-20 → WAL frame (historical after checkpoint)
BEGIN;
INSERT INTO moz_places VALUES  (1,  'https://example1.com/',  'Title 1',  1,  1700000001,  10);
INSERT INTO moz_places VALUES  (2,  'https://example2.com/',  'Title 2',  2,  1700000002,  20);
INSERT INTO moz_places VALUES  (3,  'https://example3.com/',  'Title 3',  3,  1700000003,  30);
INSERT INTO moz_places VALUES  (4,  'https://example4.com/',  'Title 4',  4,  1700000004,  40);
INSERT INTO moz_places VALUES  (5,  'https://example5.com/',  'Title 5',  5,  1700000005,  50);
INSERT INTO moz_places VALUES  (6,  'https://example6.com/',  'Title 6',  6,  1700000006,  60);
INSERT INTO moz_places VALUES  (7,  'https://example7.com/',  'Title 7',  7,  1700000007,  70);
INSERT INTO moz_places VALUES  (8,  'https://example8.com/',  'Title 8',  8,  1700000008,  80);
INSERT INTO moz_places VALUES  (9,  'https://example9.com/',  'Title 9',  9,  1700000009,  90);
INSERT INTO moz_places VALUES (10, 'https://example10.com/', 'Title 10', 10, 1700000010, 100);
INSERT INTO moz_places VALUES (11, 'https://example11.com/', 'Title 11', 11, 1700000011, 110);
INSERT INTO moz_places VALUES (12, 'https://example12.com/', 'Title 12', 12, 1700000012, 120);
INSERT INTO moz_places VALUES (13, 'https://example13.com/', 'Title 13', 13, 1700000013, 130);
INSERT INTO moz_places VALUES (14, 'https://example14.com/', 'Title 14', 14, 1700000014, 140);
INSERT INTO moz_places VALUES (15, 'https://example15.com/', 'Title 15', 15, 1700000015, 150);
INSERT INTO moz_places VALUES (16, 'https://example16.com/', 'Title 16', 16, 1700000016, 160);
INSERT INTO moz_places VALUES (17, 'https://example17.com/', 'Title 17', 17, 1700000017, 170);
INSERT INTO moz_places VALUES (18, 'https://example18.com/', 'Title 18', 18, 1700000018, 180);
INSERT INTO moz_places VALUES (19, 'https://example19.com/', 'Title 19', 19, 1700000019, 190);
INSERT INTO moz_places VALUES (20, 'https://example20.com/', 'Title 20', 20, 1700000020, 200);
COMMIT;

-- Phase 2: delete rows 6-10 → WAL frame (historical after checkpoint)
-- These rows become wal_frame deleted records in SHARD recovery.
BEGIN;
DELETE FROM moz_places WHERE id BETWEEN 6 AND 10;
COMMIT;

-- Phase 3: update rows 11-13 → WAL frame (historical after checkpoint)
-- The original values of rows 11-13 become wal_previous_version records.
BEGIN;
UPDATE moz_places SET title = 'Updated Title', visit_count = 999 WHERE id BETWEEN 11 AND 13;
COMMIT;

-- Checkpoint: merge all WAL frames into main DB and reset the WAL salt.
-- After this, old frames have IsCurrent=False (old salt). Main DB contains:
--   rows 1-5 (original), rows 11-20 (11-13 updated, 14-20 original).
PRAGMA wal_checkpoint(RESTART);

-- Phase 4: insert rows 21-22 → new WAL frame (IsCurrent=True, new salt).
-- This write changes the WAL salt so all previous frames become IsCurrent=False.
-- Rows 21-22 are live in the WAL and must NOT be recovered as deleted.
BEGIN;
INSERT INTO moz_places VALUES (21, 'https://new1.example.com/', 'New Page 1', 0, 1700099000, 100);
INSERT INTO moz_places VALUES (22, 'https://new2.example.com/', 'New Page 2', 0, 1700099001, 101);
COMMIT;

-- Process is killed (SIGKILL) here to preserve the WAL file.
-- os.kill(os.getpid(), signal.SIGKILL)
