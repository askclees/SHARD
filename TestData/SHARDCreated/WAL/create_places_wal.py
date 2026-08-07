#!/usr/bin/env python3
"""
Creates places_wal.db and places_wal.db-wal for SHARD WAL deleted-record recovery testing.

The process kills itself with SIGKILL after writing the final transaction so that
SQLite has no opportunity to checkpoint and delete the WAL on connection close.

Expected state after creation:
  Main DB (raw, 2 pages):     15 rows — id 1-5 and 11-20 (11-13 have updated values)
  WAL (5 frames, 20632 bytes):
    Frame 1  IsCurrent=True   page 2, 17 cells (rows 1-5, 11-22) — new salt after checkpoint
    Frame 2  IsCurrent=False  page 2,  0 cells (CREATE TABLE, empty moz_places)
    Frame 3  IsCurrent=False  page 2, 20 cells (rows 1-20, original values)
    Frame 4  IsCurrent=False  page 2, 15 cells (rows 1-5, 11-20 — after DELETE 6-10)
    Frame 5  IsCurrent=False  page 2, 15 cells (rows 1-5, 11-20 — after UPDATE 11-13)

WAL recovery (IsCurrent=False frames only):
  wal_frame (deleted before checkpoint):           rows 6-10  (5 records)
  wal_previous_version (updated before checkpoint): rows 11-13 (3 records, original values)
  Total recovered: 8 records
"""
import os, signal, sqlite3

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
DB_PATH    = os.path.join(SCRIPT_DIR, 'places_wal.db')
WAL_PATH   = DB_PATH + '-wal'

for path in (DB_PATH, WAL_PATH, DB_PATH + '-shm'):
    try:
        os.unlink(path)
    except FileNotFoundError:
        pass

conn = sqlite3.connect(DB_PATH, isolation_level=None)
conn.execute('PRAGMA journal_mode=WAL')
conn.execute('PRAGMA wal_autocheckpoint=0')

conn.execute('''
    CREATE TABLE moz_places (
        id              INTEGER PRIMARY KEY,
        url             TEXT    NOT NULL,
        title           TEXT,
        visit_count     INTEGER DEFAULT 0,
        last_visit_date INTEGER,
        frecency        INTEGER DEFAULT -1
    )
''')

# Phase 1: insert rows 1-20 (becomes WAL frame 3)
conn.execute('BEGIN')
conn.executemany(
    'INSERT INTO moz_places VALUES (?,?,?,?,?,?)',
    [(i,
      f'https://example{i}.com/',
      f'Title {i}',
      i,
      1700000000 + i,
      i * 10)
     for i in range(1, 21)]
)
conn.execute('COMMIT')

# Phase 2: delete rows 6-10 (becomes WAL frame 4)
conn.execute('BEGIN')
conn.execute('DELETE FROM moz_places WHERE id BETWEEN 6 AND 10')
conn.execute('COMMIT')

# Phase 3: update rows 11-13 original values become wal_previous_version (frame 5)
conn.execute('BEGIN')
conn.execute(
    "UPDATE moz_places SET title='Updated Title', visit_count=999 WHERE id BETWEEN 11 AND 13"
)
conn.execute('COMMIT')

# Checkpoint: merges all three WAL frames into main DB; old frames stay physically in
# the WAL file with the old salt, so they become IsCurrent=False once the salt changes.
conn.execute('PRAGMA wal_checkpoint(RESTART)')

# Phase 4: insert rows 21-22 (new WAL frame 1 with new salt; changes salt so frames 2-5
# become IsCurrent=False).  These are live records in the current WAL only — they are
# NOT in the raw main-DB pages and must NOT be recovered as deleted.
conn.execute('BEGIN')
conn.executemany(
    'INSERT INTO moz_places VALUES (?,?,?,?,?,?)',
    [(21, 'https://new1.example.com/', 'New Page 1', 0, 1700099000, 100),
     (22, 'https://new2.example.com/', 'New Page 2', 0, 1700099001, 101)]
)
conn.execute('COMMIT')

# Kill the process before SQLite can close the connection and delete the WAL.
os.kill(os.getpid(), signal.SIGKILL)
