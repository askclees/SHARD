#!/usr/bin/env python3
"""
Creates carving_orphan_leaf.db for SHARD orphan-page (multi-schema) carving testing.

Reuses the schema/insert pattern proven in
TestData/SHARDCreated/WAL/create_places_wal_multi_page.py, scaled up to 500
rows so a bulk mid-range DELETE reliably frees whole leaf pages via SQLite's
b-tree balancing, rather than merely emptying-in-place (empirically verified:
deleting a single leaf's exact row range in isolation just leaves an empty-
but-still-linked leaf; a larger bulk delete spanning several leaves is what
actually triggers SQLite to unlink and freelist a page).

IMPORTANT: PRAGMA secure_delete is left OFF explicitly. Some SQLite builds
(observed: the sqlite3 module bundled with Python 3.14 / SQLite 3.46.1 on
this platform) default secure_delete to ON, which zeroes freed page content
immediately — the opposite of the case this scenario is testing. The other
SHARDCreated/WAL generators are unaffected by this default because they
recover from WAL frame history (a snapshot taken before the delete), not from
freed-page bytes after the fact.

Page layout after insert + delete (4096-byte pages, verified via
`dotnet run --project SHARD.Cli -- pages`):
  Page 1 — sqlite_master
  Page 2 — moz_places interior (root)
  Page 3-6 — moz_places leaves, live survivors (ids 1-199, 401-477 minus the
             orphaned range below, redistributed across remaining leaves)
  Page 7 — freelist trunk page (reformatted, no recoverable content)
  Page 8 — moz_places leaf, UNREACHABLE from the tree but byte-for-byte intact:
           78 cells, ids 400-477. This is the orphan-carving target: no
           dropped-table sqlite_master entry is involved (the table itself is
           never dropped), and the page was never on any table's tree in the
           final structure — a genuine "no specific-table hint" orphan.

This is deliberately NOT produced via `DROP TABLE` + `VACUUM`: VACUUM rebuilds
the file from only currently-live content, discarding every freed page (and
its forensic remnants) outright — nothing would survive to carve.

Expected recovery (loose or tight mode; only one candidate table exists so
there is no ambiguity either way):
  moz_places — _shard_recovered_moz_places gains 78 rows (ids 400-477),
  _recovery_method = 'orphan_carving'.
"""
import os, sqlite3

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
DB_PATH    = os.path.join(SCRIPT_DIR, 'carving_orphan_leaf.db')

for path in (DB_PATH, DB_PATH + '-journal'):
    try:
        os.unlink(path)
    except FileNotFoundError:
        pass

conn = sqlite3.connect(DB_PATH, isolation_level=None)
conn.execute('PRAGMA secure_delete=OFF')

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

# Insert rows 1-500 (forces a multi-leaf B-tree split).
conn.execute('BEGIN')
conn.executemany(
    'INSERT INTO moz_places VALUES (?,?,?,?,?,?)',
    [(i, f'https://example{i}.com/', f'Title {i}', i, 1700000000 + i, i * 10)
     for i in range(1, 501)]
)
conn.execute('COMMIT')

# Bulk delete spanning several leaves so SQLite's balance step actually frees
# a page rather than just leaving an empty-but-linked leaf behind.
conn.execute('BEGIN')
conn.execute('DELETE FROM moz_places WHERE id BETWEEN 200 AND 400')
conn.execute('COMMIT')

conn.close()
