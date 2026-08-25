#!/usr/bin/env python3
"""
Creates carving_ambiguous_tables.db — the no-false-positive and tight-mode-
disambiguation scenario for SHARD orphan-page carving.

Two live tables, table_a and table_b, share byte-identical column shape (same
declared types/affinity/nullability, different table name only) — so
RecordStructure.FromSchema('loose' mode) produces indistinguishable
structures for both. table_a is bulk-deleted the same way as
create_carving_orphan_leaf.py to orphan one of its leaf pages; table_b is
never touched. Every carved record on that orphaned page is therefore
structurally ambiguous between table_a and table_b under loose matching.

The `num` column's *observed* content differs sharply between the two tables
(table_a: always the small integer 5, encoded in 1 byte; table_b: always
99999999, encoded in 4 bytes) — so 'tight' mode, which narrows each
candidate's RecordStructure to its own table's observed (kind, content-length)
pairs, disambiguates them: table_a's tightened structure requires a 1-byte
integer for `num`, which table_b's leftover bytes (were there any) would never
satisfy, and vice versa.

PRAGMA secure_delete is left OFF explicitly — see create_carving_orphan_leaf.py
for why this matters on this platform's SQLite build.

Page layout after insert + delete (4096-byte pages, verified via
`dotnet run --project SHARD.Cli -- pages` / `carve`):
  Page 2 — table_a interior (root)
  Page 3 — table_b interior (root)
  Page 6 — freelist trunk page (reformatted, no recoverable content)
  Page 7 — table_a leaf, UNREACHABLE from the tree but byte-for-byte intact:
           54 cells, ids 447-500. Orphan-carving target.
  (remaining pages: live table_a/table_b leaves)

Expected recovery:
  Loose mode (candidates = table_a + table_b): 0 rows carved from page 7;
    ambiguousSkipped > 0 — every candidate record matches both tables'
    identically-shaped loose structures, so nothing is attributed.
  Tight mode (candidates = table_a + table_b, each auto-tightened from its own
    observed rows): 54 rows carved, all correctly attributed to table_a
    (ids 447-500), _recovery_method = 'orphan_carving'. table_b gains none.
"""
import os, sqlite3

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
DB_PATH    = os.path.join(SCRIPT_DIR, 'carving_ambiguous_tables.db')

for path in (DB_PATH, DB_PATH + '-journal'):
    try:
        os.unlink(path)
    except FileNotFoundError:
        pass

conn = sqlite3.connect(DB_PATH, isolation_level=None)
conn.execute('PRAGMA secure_delete=OFF')

conn.execute('CREATE TABLE table_a (id INTEGER PRIMARY KEY, val TEXT NOT NULL, num INTEGER)')
conn.execute('CREATE TABLE table_b (id INTEGER PRIMARY KEY, val TEXT NOT NULL, num INTEGER)')

conn.execute('BEGIN')
conn.executemany(
    'INSERT INTO table_a VALUES (?,?,?)',
    [(i, f'row-{i}-padding-xx', 5) for i in range(1, 501)]
)
conn.execute('COMMIT')

conn.execute('BEGIN')
conn.executemany(
    'INSERT INTO table_b VALUES (?,?,?)',
    [(i, f'row-{i}-padding-xx', 99999999) for i in range(1, 501)]
)
conn.execute('COMMIT')

# Bulk delete from table_a only, spanning several of its leaves so SQLite's
# balance step frees a page rather than leaving an empty-but-linked leaf.
conn.execute('BEGIN')
conn.execute('DELETE FROM table_a WHERE id BETWEEN 150 AND 450')
conn.execute('COMMIT')

conn.close()
