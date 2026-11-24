#!/usr/bin/env python3
"""
Creates places_wal_freeblocks.db: deleted rows that survive as freed space inside WAL frames.

Unlike the other WAL fixtures, secure_delete is OFF, so deleted cells keep their bytes in the
freeblocks and unallocated space of the page images written to the WAL. That lets SHARD's
recovery search each WAL frame's deleted data, not just its live cells.

Steps (secure_delete OFF throughout):
  1. Create moz_places, insert rows 1-5, PRAGMA wal_checkpoint(TRUNCATE) -> the database file holds
     rows 1-5 and the WAL is empty.
  2. DELETE row 2                                  -> frame: rows 1,3,4,5, row 2 in freed space
  3. INSERT row 30                                 -> frame: + row 30
  4. BEGIN; INSERT 50; INSERT 51; DELETE 50; COMMIT -> frame: + row 51; row 50 only ever freed space
  5. DELETE row 30                                 -> frame: rows 1,3,4,5,51
  Then the process is killed, so nothing is checkpointed.

Rows inserted later are much longer than row 2, so SQLite can't reuse row 2's freed space for them.
Row 51 is inserted after row 50 so that freeing row 50 leaves a freeblock rather than simply moving
the start of the cell content area.

What each deleted row tests:
  row 2  - live in the database file, deleted by a committed WAL transaction. Recovered from the
           file's own version of page 2 (db_file_deleted_in_wal); its freed copy in the WAL frames
           is a duplicate.
  row 30 - inserted and deleted within the WAL: a live cell in committed frames that a later
           committed frame replaced (wal_frame).
  row 50 - inserted and deleted within one transaction: it is never a live cell anywhere, and
           survives only as freed space inside WAL frames.

Expected recovery:
  Live (SQLite's view, file + WAL): rows 1, 3, 4, 5, 51
  Live (database file alone):       rows 1-5
  Recovered from the WAL (frame-based, the file-only path the fixture tests use): rows 30 and 50
"""
import os, signal, sqlite3

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
DB_PATH    = os.path.join(SCRIPT_DIR, 'places_wal_freeblocks.db')

for suffix in ('', '-wal', '-shm'):
    try:
        os.unlink(DB_PATH + suffix)
    except FileNotFoundError:
        pass

conn = sqlite3.connect(DB_PATH, isolation_level=None)
conn.execute('PRAGMA journal_mode=WAL')
conn.execute('PRAGMA wal_autocheckpoint=0')
conn.execute('PRAGMA secure_delete=OFF')  # explicit: the default depends on how SQLite was compiled

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

def row(i, long=False):
    title = f'Title {i}' + (' - ' + 'long page title ' * 8 if long else '')
    return (i, f'https://example{i}.com/', title, i, 1700000000 + i, i * 10)

conn.execute('BEGIN')
conn.executemany('INSERT INTO moz_places VALUES (?,?,?,?,?,?)', [row(i) for i in range(1, 6)])
conn.execute('COMMIT')
conn.execute('PRAGMA wal_checkpoint(TRUNCATE)')

conn.execute('DELETE FROM moz_places WHERE id = 2')
conn.execute('INSERT INTO moz_places VALUES (?,?,?,?,?,?)', row(30, long=True))

conn.execute('BEGIN')
conn.execute('INSERT INTO moz_places VALUES (?,?,?,?,?,?)', row(50, long=True))
conn.execute('INSERT INTO moz_places VALUES (?,?,?,?,?,?)', row(51, long=True))
conn.execute('DELETE FROM moz_places WHERE id = 50')
conn.execute('COMMIT')

conn.execute('DELETE FROM moz_places WHERE id = 30')

# Kill without closing: closing the last connection would checkpoint and delete the WAL.
os.kill(os.getpid(), signal.SIGKILL)
