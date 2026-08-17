#!/usr/bin/env python3
"""
Creates places_wal_no_post_restart_writes.db for SHARD WAL false-positive testing.

The process is killed immediately after PRAGMA wal_checkpoint(RESTART) with NO
subsequent writes.  Because the WAL salt only changes on the first write after a
RESTART, all frames in the WAL file still carry the original salt and are
therefore IsCurrent=True from SHARD's perspective.  InsertWalDeletedRows must
skip every frame and recover ZERO records — even though the deleted rows (4-6)
physically exist in the historical WAL frames.

This is a correctness / no-false-positive test: confirming that SHARD does not
misclassify IsCurrent=True frames as recoverable historical data.

Historical WAL frames (all IsCurrent=True because salt unchanged):
  Page 2 frames: empty → rows 1-10 → rows 1-3,7-10 (after delete 4-6)

Expected recovery:
  moz_places — rowsAlive=7, rowsDeleted=0  (no IsCurrent=False frames to mine)
"""
import os, signal, sqlite3

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
DB_PATH    = os.path.join(SCRIPT_DIR, 'places_wal_no_post_restart_writes.db')
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

# Insert rows 1-10
conn.execute('BEGIN')
conn.executemany(
    'INSERT INTO moz_places VALUES (?,?,?,?,?,?)',
    [(i, f'https://example{i}.com/', f'Title {i}', i, 1700000000 + i, i * 10)
     for i in range(1, 11)]
)
conn.execute('COMMIT')

# Delete rows 4-6 — these appear in historical frames but MUST NOT be recovered
conn.execute('BEGIN')
conn.execute('DELETE FROM moz_places WHERE id BETWEEN 4 AND 6')
conn.execute('COMMIT')

# Checkpoint without any subsequent write.
# The WAL salt is NOT changed — all existing frames remain IsCurrent=True.
conn.execute('PRAGMA wal_checkpoint(RESTART)')

# Kill immediately — no new writes, salt unchanged.
os.kill(os.getpid(), signal.SIGKILL)
