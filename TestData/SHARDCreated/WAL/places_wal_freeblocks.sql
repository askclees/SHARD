-- SHARD WAL Test Data: deleted rows in freed space inside WAL frames
-- Database: places_wal_freeblocks.db
-- Created by: create_places_wal_freeblocks.py
--
-- secure_delete is OFF. The process is killed (SIGKILL) after the last statement so the WAL is not
-- checkpointed or deleted on close.
--
-- Expected SHARD analysis:
--   Live rows (main DB, raw):          5  (ids 1-5)
--   Live rows (SQLite's view, + WAL):  5  (ids 1, 3, 4, 5, 51)
--   WAL-recovered deleted rows:        2  (file-only path: rows 30 and 50)

PRAGMA journal_mode = WAL;
PRAGMA wal_autocheckpoint = 0;
PRAGMA secure_delete = OFF;

CREATE TABLE moz_places (
    id              INTEGER PRIMARY KEY,
    url             TEXT    NOT NULL,
    title           TEXT,
    visit_count     INTEGER DEFAULT 0,
    last_visit_date INTEGER,
    frecency        INTEGER DEFAULT -1
);

-- rows 1-5 (short titles), then checkpointed into the database file
INSERT INTO moz_places VALUES (1, 'https://example1.com/', 'Title 1', 1, 1700000001, 10);
INSERT INTO moz_places VALUES (2, 'https://example2.com/', 'Title 2', 2, 1700000002, 20);
INSERT INTO moz_places VALUES (3, 'https://example3.com/', 'Title 3', 3, 1700000003, 30);
INSERT INTO moz_places VALUES (4, 'https://example4.com/', 'Title 4', 4, 1700000004, 40);
INSERT INTO moz_places VALUES (5, 'https://example5.com/', 'Title 5', 5, 1700000005, 50);
PRAGMA wal_checkpoint(TRUNCATE);

-- WAL only (rows 30, 50, 51 have long titles: 'Title N - ' followed by 'long page title ' x 8)
DELETE FROM moz_places WHERE id = 2;
INSERT INTO moz_places VALUES (30, 'https://example30.com/', 'Title 30 - long page title ...', 30, 1700000030, 300);
BEGIN;
INSERT INTO moz_places VALUES (50, 'https://example50.com/', 'Title 50 - long page title ...', 50, 1700000050, 500);
INSERT INTO moz_places VALUES (51, 'https://example51.com/', 'Title 51 - long page title ...', 51, 1700000051, 510);
DELETE FROM moz_places WHERE id = 50;
COMMIT;
DELETE FROM moz_places WHERE id = 30;
