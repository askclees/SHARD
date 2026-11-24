#!/usr/bin/env python3
"""
Creates places_wal_no_post_restart_writes.db: rows deleted within the WAL that survive only in an
earlier committed frame.

The process is killed immediately after PRAGMA wal_checkpoint(RESTART) with NO subsequent writes.
Because the WAL salt only changes on the first write after a RESTART, every frame keeps the
original salt and is committed; the checkpoint has also copied them all into the database file.

WAL frames:
  Page 2 frames: empty -> rows 1-10 (frame 3) -> rows 1-3,7-10 (frame 4, after deleting 4-6)

secure_delete is on, so deleting rows 4-6 zeroes their bytes in frame 4 and in the database file;
frame 3 (committed, but superseded by frame 4) is the only surviving copy. SHARD must recover
rows 4-6 from it, and must not report the 7 live rows (present in every frame) as deleted.

Expected recovery:
  moz_places - rowsAlive=7, rowsDeleted=3 (ids 4-6, wal_frame, frame 3)
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
conn.execute('PRAGMA secure_delete=ON')  # explicit: the default depends on how SQLite was compiled

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

# Delete rows 4-6 — with secure_delete on, frame 3 becomes the only copy of them
conn.execute('BEGIN')
conn.execute('DELETE FROM moz_places WHERE id BETWEEN 4 AND 6')
conn.execute('COMMIT')

# Checkpoint without any subsequent write.
# The WAL salt is NOT changed — all existing frames remain IsCurrent=True.
conn.execute('PRAGMA wal_checkpoint(RESTART)')

# Kill immediately — no new writes, salt unchanged.
os.kill(os.getpid(), signal.SIGKILL)
