#!/usr/bin/env python3
"""
Creates places_wal_multi_page.db for SHARD WAL multi-page B-tree recovery testing.

100 rows force a B-tree split: the root (page 2) becomes an interior page and
two leaf pages are allocated (page 3: rows 1-85, page 4: rows 86-100).  Rows
are deleted from BOTH leaf pages before a RESTART checkpoint, so SHARD must
correlate WAL frames for non-root leaf pages to the correct table and recover
deleted records from each.

Page layout after insert (4096-byte pages):
  Page 2 — moz_places interior (root, 1 divider key)
  Page 3 — moz_places leaf,  85 cells, rowids 1-85
  Page 4 — moz_places leaf,  15 cells, rowids 86-100

Deletions before checkpoint:
  Rows 40-50  → deleted from page 3 leaf (11 records)
  Rows 90-95  → deleted from page 4 leaf  (6 records)

At least one surviving rowid on each affected leaf page ensures the correlation
check (frameRowIds ∩ knownIds ≠ ∅) succeeds for both leaf frames.

Historical WAL frames (IsCurrent=False) after RESTART:
  Page 3 frames: empty → rows 1-85 → rows 1-39,51-85 (after delete 40-50)
  Page 4 frames: empty → rows 86-100 → rows 86-89,96-100 (after delete 90-95)
  (Page 2 interior frames are skipped — non-leaf pages yield null from correlation)

Expected recovery:
  moz_places — rowsAlive=83 (1-39, 51-89, 96-100), rowsDeleted=17 (all wal_frame)
    wal_frame from page 3: rows 40-50  (11 records)
    wal_frame from page 4: rows 90-95  ( 6 records)
"""
import os, signal, sqlite3

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
DB_PATH    = os.path.join(SCRIPT_DIR, 'places_wal_multi_page.db')
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

# Insert rows 1-100 (forces a B-tree page split: interior root + 2 leaf pages)
conn.execute('BEGIN')
conn.executemany(
    'INSERT INTO moz_places VALUES (?,?,?,?,?,?)',
    [(i, f'https://example{i}.com/', f'Title {i}', i, 1700000000 + i, i * 10)
     for i in range(1, 101)]
)
conn.execute('COMMIT')

# Delete rows 40-50 from page-3 leaf (11 records; 74 survivors remain on that leaf)
conn.execute('BEGIN')
conn.execute('DELETE FROM moz_places WHERE id BETWEEN 40 AND 50')
conn.execute('COMMIT')

# Delete rows 90-95 from page-4 leaf (6 records; 9 survivors remain on that leaf)
conn.execute('BEGIN')
conn.execute('DELETE FROM moz_places WHERE id BETWEEN 90 AND 95')
conn.execute('COMMIT')

# Checkpoint: old frames become IsCurrent=False after the next write changes the salt.
conn.execute('PRAGMA wal_checkpoint(RESTART)')

# New write to change the WAL salt.
conn.execute('BEGIN')
conn.execute("INSERT INTO moz_places VALUES (101,'https://new.example.com/','New Page',0,1700099000,100)")
conn.execute('COMMIT')

os.kill(os.getpid(), signal.SIGKILL)
