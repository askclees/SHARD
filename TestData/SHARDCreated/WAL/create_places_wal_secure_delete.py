#!/usr/bin/env python3
"""
Creates places_wal_secure_delete.db and its WAL for SHARD WAL recovery testing.

Key difference from places_wal.db: PRAGMA secure_delete=ON is enabled, which
overwrites freed cell space with zeros on every delete transaction.  This
destroys the evidence that page-level carving relies on, making page-level
recovery return nothing.  The historical WAL frames (IsCurrent=False) still
hold the original page images captured *before* each deletion, so SHARD's
WAL analysis can recover all deleted rows even when secure_delete defeated
conventional carving.

Deletion stages (each creates a separate WAL frame):
  Stage 1 — DELETE rows  6-10  → Frame 4: 25 cells remain, rows  6-10 zeroed
  Stage 2 — DELETE rows 16-20  → Frame 5: 20 cells remain, rows 16-20 zeroed
  Stage 3 — DELETE rows 26-30  → Frame 6: 15 cells remain, rows 26-30 zeroed

Expected state after creation:
  Main DB (raw, 2 pages):    15 rows — ids 1-5, 11-15, 21-25
                              (freed space entirely zeroed by secure_delete)
  WAL (6 frames, 24752 bytes):
    Frame 1  IsCurrent=True   page 2, 17 cells (rows 1-5,11-15,21-25,31-32) — new salt
    Frame 2  IsCurrent=False  page 2,  0 cells (CREATE TABLE, empty moz_places)
    Frame 3  IsCurrent=False  page 2, 30 cells (rows 1-30, original values)
    Frame 4  IsCurrent=False  page 2, 25 cells (rows 1-5,11-30; rows 6-10 zeroed)
    Frame 5  IsCurrent=False  page 2, 20 cells (rows 1-5,11-15,21-30; 16-20 zeroed)
    Frame 6  IsCurrent=False  page 2, 15 cells (rows 1-5,11-15,21-25; 26-30 zeroed)

WAL recovery (IsCurrent=False frames only):
  Frame 3 is the only source: all 30 rows are intact there.
  wal_frame (deleted rows 6-10, 16-20, 26-30):  15 records
  wal_previous_version:                            0 records (no updates)
  Total recovered: 15 records

Page-level carving (DeletedCells / CarvedCells / FreeblockCells): 0 records
  — secure_delete zeroed all freed space before writing each WAL frame.
"""
import os, signal, sqlite3

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
DB_PATH    = os.path.join(SCRIPT_DIR, 'places_wal_secure_delete.db')
WAL_PATH   = DB_PATH + '-wal'

for path in (DB_PATH, WAL_PATH, DB_PATH + '-shm'):
    try:
        os.unlink(path)
    except FileNotFoundError:
        pass

conn = sqlite3.connect(DB_PATH, isolation_level=None)
conn.execute('PRAGMA journal_mode=WAL')
conn.execute('PRAGMA wal_autocheckpoint=0')
conn.execute('PRAGMA secure_delete=ON')

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

# Phase 1: insert rows 1-30 (becomes WAL frame 3 — all 30 rows intact)
conn.execute('BEGIN')
conn.executemany(
    'INSERT INTO moz_places VALUES (?,?,?,?,?,?)',
    [(i,
      f'https://example{i}.com/',
      f'Title {i}',
      i,
      1700000000 + i,
      i * 10)
     for i in range(1, 31)]
)
conn.execute('COMMIT')

# Stage 1: delete rows 6-10 (becomes WAL frame 4; freed space zeroed by secure_delete)
conn.execute('BEGIN')
conn.execute('DELETE FROM moz_places WHERE id BETWEEN 6 AND 10')
conn.execute('COMMIT')

# Stage 2: delete rows 16-20 (becomes WAL frame 5)
conn.execute('BEGIN')
conn.execute('DELETE FROM moz_places WHERE id BETWEEN 16 AND 20')
conn.execute('COMMIT')

# Stage 3: delete rows 26-30 (becomes WAL frame 6)
conn.execute('BEGIN')
conn.execute('DELETE FROM moz_places WHERE id BETWEEN 26 AND 30')
conn.execute('COMMIT')

# Checkpoint: merges all six WAL frames into main DB (with secure_delete zeros).
# Old frames retain the old salt → they become IsCurrent=False after the next write.
conn.execute('PRAGMA wal_checkpoint(RESTART)')

# Phase 2: insert rows 31-32 (new WAL frame 1 with new salt).
# This write changes the salt so all previous frames become IsCurrent=False.
# Rows 31-32 are live in the current WAL only and must NOT be recovered as deleted.
conn.execute('BEGIN')
conn.executemany(
    'INSERT INTO moz_places VALUES (?,?,?,?,?,?)',
    [(31, 'https://new1.example.com/', 'New Page 1', 0, 1700099000, 100),
     (32, 'https://new2.example.com/', 'New Page 2', 0, 1700099001, 101)]
)
conn.execute('COMMIT')

# Kill the process to prevent SQLite's clean-close checkpoint from removing the WAL.
os.kill(os.getpid(), signal.SIGKILL)
