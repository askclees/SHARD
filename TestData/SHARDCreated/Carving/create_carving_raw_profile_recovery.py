#!/usr/bin/env python3
"""
Creates carving_raw_profile_recovery.db for testing profile-only raw carving: recovering every
row in a database whose own schema/header is completely gone, using only a CarvingProfile
exported while the database was still readable (see CarvingProfileCandidateBuilder).

10 tables, column counts stepping from 4 to 20, covering every SQLite type affinity with a mix
of NOT NULL and nullable columns, plus an always-0/1 "flag" column per table (exercises the
Int0/Int1 serial-type narrowing). Every table has an INTEGER PRIMARY KEY rowid alias. Row/field
sizes are kept small on purpose so every record fits on a single leaf page with no overflow —
overflow-chain resolution is not what this fixture is testing.

No rows are deleted: the point is that even a fully live, undeleted database becomes
unrecoverable through the normal SqliteForensicDatabase.Open()/sqlite_master path once its first
page is destroyed, and must come back solely through CarvingProfile-driven raw byte carving.
"""
import os, sqlite3

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
DB_PATH    = os.path.join(SCRIPT_DIR, 'carving_raw_profile_recovery.db')

for path in (DB_PATH, DB_PATH + '-journal'):
    try:
        os.unlink(path)
    except FileNotFoundError:
        pass

conn = sqlite3.connect(DB_PATH, isolation_level=None)
conn.execute('PRAGMA secure_delete=OFF')

# Column-type cycle covering every affinity: INTEGER, TEXT, REAL, BLOB, NUMERIC-declared.
TYPE_CYCLE = [
    ('INTEGER', 'int'),
    ('TEXT', 'text'),
    ('REAL', 'real'),
    ('BLOB', 'blob'),
    ('DECIMAL(10,2)', 'numeric'),
]

# Distinct column counts (not just within [4, 20], but pairwise distinct) so no two tables can
# ever be structurally ambiguous purely on column count alone — DeletedRecordParser rejects any
# record that matches more than one candidate rather than guessing, so two same-shaped tables
# built from the same deterministic value generator below would otherwise make every one of their
# rows unrecoverable (ambiguous), which would defeat the point of this fixture.
TABLE_COLUMN_COUNTS = [4, 5, 6, 7, 8, 9, 10, 12, 15, 20]

table_defs = []  # (table_name, [ (col_name, decl_type, not_null, kind) ... ] excluding id/flag)

for t_idx, n_cols in enumerate(TABLE_COLUMN_COUNTS):
    table_name = f'wide_table_{t_idx:02d}'
    cols = []
    # -2 to leave room for the always-present id (rowid alias) and flag columns.
    for c_idx in range(n_cols - 2):
        decl_type, kind = TYPE_CYCLE[c_idx % len(TYPE_CYCLE)]
        not_null = (c_idx % 3 == 0)  # every 3rd column is NOT NULL, rest nullable
        col_name = f'col_{kind}_{c_idx:02d}'
        cols.append((col_name, decl_type, not_null, kind))
    table_defs.append((table_name, cols))

conn.execute('BEGIN')
for table_name, cols in table_defs:
    col_sql = ['id INTEGER PRIMARY KEY', 'flag_bit INTEGER NOT NULL']
    for col_name, decl_type, not_null, _kind in cols:
        col_sql.append(f'{col_name} {decl_type}' + (' NOT NULL' if not_null else ''))
    conn.execute(f'CREATE TABLE {table_name} ({", ".join(col_sql)})')
conn.execute('COMMIT')

ROWS_PER_TABLE = 40


def value_for(kind, not_null, row_i, col_i):
    """Deterministic, small, non-overflowing value; nullable columns get NULL every 5th row."""
    if not not_null and row_i % 5 == 0:
        return None
    if kind == 'int':
        return row_i * 100 + col_i
    if kind == 'text':
        return f'row{row_i}_col{col_i}_text_value'
    if kind == 'real':
        return row_i * 1.5 + col_i * 0.25
    if kind == 'blob':
        return bytes([(row_i + col_i) % 256] * 12)
    if kind == 'numeric':
        return round(row_i * 3.3 + col_i, 2)
    raise ValueError(kind)


conn.execute('BEGIN')
for table_name, cols in table_defs:
    placeholders = ', '.join(['?'] * (2 + len(cols)))
    insert_sql = f'INSERT INTO {table_name} VALUES ({placeholders})'
    for row_i in range(1, ROWS_PER_TABLE + 1):
        flag_bit = row_i % 2  # always exactly 0 or 1
        values = [row_i, flag_bit]
        for col_i, (col_name, decl_type, not_null, kind) in enumerate(cols):
            values.append(value_for(kind, not_null, row_i, col_i))
        conn.execute(insert_sql, values)
conn.execute('COMMIT')

conn.close()

print(f'Created {DB_PATH} with {len(table_defs)} tables, {ROWS_PER_TABLE} rows each.')
