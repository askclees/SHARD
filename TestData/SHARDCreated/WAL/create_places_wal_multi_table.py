#!/usr/bin/env python3
"""
Creates places_wal_multi_table.db for SHARD WAL multi-table recovery testing.

Two tables are populated and records are deleted from each before a RESTART
checkpoint.  The test verifies that WAL frames for moz_places pages only
contribute records to _shard_recovered_moz_places, and WAL frames for
moz_historyvisits pages only contribute to _shard_recovered_moz_historyvisits
— i.e. no cross-table contamination in the correlation logic.

Page layout (4096-byte pages):
  Page 1 — sqlite_schema (skipped by recovery)
  Page 2 — moz_places (single leaf, 10 rows)
  Page 3 — moz_historyvisits (single leaf, 20 rows)

Historical WAL frames (IsCurrent=False) after RESTART:
  Page 2 frames: empty → rows 1-10 → rows 1-3,7-10 (after delete 4-6)
  Page 3 frames: empty → rows 1-20 → rows 1-7,13-20 (after delete 8-12)

Expected recovery:
  moz_places        — rowsAlive=7  (1-3, 7-10), rowsDeleted=3  (4-6  wal_frame)
  moz_historyvisits — rowsAlive=15 (1-7, 13-20), rowsDeleted=5 (8-12 wal_frame)
"""
import os, signal, sqlite3

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
DB_PATH    = os.path.join(SCRIPT_DIR, 'places_wal_multi_table.db')
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

conn.execute('''
    CREATE TABLE moz_historyvisits (
        id         INTEGER PRIMARY KEY,
        place_id   INTEGER NOT NULL,
        visit_date INTEGER NOT NULL,
        visit_type INTEGER NOT NULL,
        from_visit INTEGER DEFAULT 0
    )
''')

# Insert rows 1-10 into moz_places
conn.execute('BEGIN')
conn.executemany(
    'INSERT INTO moz_places VALUES (?,?,?,?,?,?)',
    [(i, f'https://example{i}.com/', f'Title {i}', i, 1700000000 + i, i * 10)
     for i in range(1, 11)]
)
conn.execute('COMMIT')

# Insert rows 1-20 into moz_historyvisits
conn.execute('BEGIN')
conn.executemany(
    'INSERT INTO moz_historyvisits VALUES (?,?,?,?,?)',
    [(i, i, 1700000000 + i * 1000, (i % 5) + 1, 0)
     for i in range(1, 21)]
)
conn.execute('COMMIT')

# Delete from moz_places (rows 4-6 → wal_frame)
conn.execute('BEGIN')
conn.execute('DELETE FROM moz_places WHERE id BETWEEN 4 AND 6')
conn.execute('COMMIT')

# Delete from moz_historyvisits (rows 8-12 → wal_frame)
conn.execute('BEGIN')
conn.execute('DELETE FROM moz_historyvisits WHERE id BETWEEN 8 AND 12')
conn.execute('COMMIT')

# Checkpoint: merges all frames; old frames become IsCurrent=False after next write.
conn.execute('PRAGMA wal_checkpoint(RESTART)')

# One new write to change the salt (makes all pre-checkpoint frames IsCurrent=False).
conn.execute('BEGIN')
conn.execute("INSERT INTO moz_places VALUES (11,'https://new.example.com/','New Page',0,1700099000,100)")
conn.execute('COMMIT')

os.kill(os.getpid(), signal.SIGKILL)
