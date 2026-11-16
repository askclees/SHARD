using System.Globalization;
using Microsoft.Data.Sqlite;
using SHARD.Core.Enums;
using SHARD.Core.Pages;
using SHARD.Core.Records;
using SHARD.Core.Schema;
using SHARD.Core.WAL;

namespace SHARD.Core.Shadow;

/// <summary>
/// Labels every row of a shadow database's live and recovered tables with where that exact row
/// version (rowid plus values) stands in the database file and in the transaction log:
/// <list type="bullet">
/// <item><see cref="DbStateColumn"/>: 1 = live in the file with these values; 3 = the rowid is live in
/// the file with other values; 2 = found only in the file's deleted data; 0 = not in the file.</item>
/// <item><see cref="LogStateColumn"/> (WAL): NULL = no WAL, or the WAL doesn't cover the row; otherwise
/// 1 = live in the WAL's committed state, and the WAL holds it (a frame contains it, or its live
/// page came from the WAL); 3 = the rowid is live
/// in the committed state with other values; 2 = found in the WAL but not live in the committed
/// state, or live in the file and removed by the WAL (whether or not its bytes survive in the WAL);
/// 0 = covered, but none of those.</item>
/// <item><see cref="IsCurrentColumn"/>: 1 when this row version is what SQLite returns now — the
/// committed state when a WAL is applied, otherwise the file.</item>
/// </list>
/// A row is covered by the WAL when it appears in a WAL frame (live or as deleted data), when the
/// page it came from in the file is one the WAL rewrote, or when its rowid's live version is on a
/// page that came from the WAL. Rows are matched by rowid and values in tables with a rowid alias,
/// and by values alone otherwise (or when a recovered record lost its rowid). Rollback-journal
/// states aren't computed yet: with a journal, <see cref="LogStateColumn"/> stays NULL.
/// </summary>
public static class RowStates
{
    public const string DbStateColumn   = "_db_state";
    public const string LogStateColumn  = "_log_state";
    public const string IsCurrentColumn = "_is_current";

    /// <summary><see cref="ShadowDatabaseBuilder.InfoTableName"/> key: the transaction log the states
    /// describe — <c>wal</c>, <c>journal</c>, or <c>none</c>.</summary>
    public const string InfoKeyTransactionLog = "transaction_log";

    /// <summary>
    /// Computes the state columns for every live and recovered table in the shadow database on
    /// <paramref name="connection"/> (adding the columns if they're missing), and records
    /// <paramref name="transactionLog"/> in the info table. <paramref name="committed"/> is
    /// <paramref name="file"/> with <paramref name="wal"/>'s committed frames applied; pass both or
    /// neither. Safe to run again at any time — every value is recomputed.
    /// </summary>
    public static void Update(
        SqliteConnection connection,
        SqliteForensicDatabase file,
        WalFile? wal,
        SqliteForensicDatabase? committed,
        string transactionLog)
    {
        if (wal is null != committed is null)
            throw new ArgumentException("Pass the WAL and its committed state together, or neither.");

        ShadowDatabaseBuilder.WriteInfo(connection, InfoKeyTransactionLog, transactionLog);

        var fileTables = Tables(file);
        var committedTables = committed is null ? null : Tables(committed);
        var fileMap = file.BuildPageTableMap();
        var committedMap = committed?.BuildPageTableMap();
        var walPages = wal?.Frames.Select(f => f.Header.PageNumber).ToHashSet() ?? [];

        foreach (string table in (committedTables ?? fileTables).Keys)
        {
            var schema = (committedTables ?? fileTables)[table].Schema;
            string live = table;
            string recovered = ShadowDatabaseBuilder.RecoveredTablePrefix + table;
            if (!TableExists(connection, live)) continue;

            var fileSnap = Snapshot.Of(file, fileTables.GetValueOrDefault(table), schema, includeDeleted: true);
            var committedSnap = committed is null ? null
                : Snapshot.Of(committed, committedTables!.GetValueOrDefault(table), schema, includeDeleted: false);
            var walSeen = wal is null ? null : WalRecords.Of(wal, table, schema, committedMap!, fileMap, file.Header);

            foreach (string shadowTable in new[] { live, recovered })
            {
                if (!TableExists(connection, shadowTable)) continue;
                EnsureColumns(connection, shadowTable);
                bool isRecovered = shadowTable == recovered;
                UpdateTable(connection, shadowTable, schema, isRecovered, fileSnap, committedSnap, walSeen, walPages, committed);
            }
        }
    }

    private static void UpdateTable(
        SqliteConnection connection, string shadowTable, TableSchema schema, bool isRecovered,
        Snapshot fileSnap, Snapshot? committedSnap, WalRecords? walSeen, HashSet<uint> walPages,
        SqliteForensicDatabase? committed)
    {
        int aliasIndex = schema.Columns.FindIndex(c => c.IsRowIdAlias);
        var current = committedSnap ?? fileSnap;

        var updates = new List<(long ShadowRowId, int Db, int? Log, int Current)>();
        using (var read = connection.CreateCommand())
        {
            var cols = schema.Columns.Select(c => ShadowDatabaseBuilder.QuoteIdentifier(c.Name)).ToList();
            cols.Add(ShadowDatabaseBuilder.QuoteIdentifier("_page_number"));
            cols.Add(isRecovered ? ShadowDatabaseBuilder.QuoteIdentifier(ShadowDatabaseBuilder.WalFrameColumn) : "NULL");
            read.CommandText = $"SELECT rowid, {string.Join(", ", cols)} FROM {ShadowDatabaseBuilder.QuoteIdentifier(shadowTable)}";
            using var r = read.ExecuteReader();
            int n = schema.Columns.Count;
            while (r.Read())
            {
                long shadowRowId = r.GetInt64(0);
                var values = new string?[n];
                for (int i = 0; i < n; i++) values[i] = Norm(r.IsDBNull(i + 1) ? null : r.GetValue(i + 1));
                long? rowId = aliasIndex >= 0 && !r.IsDBNull(aliasIndex + 1) && r.GetValue(aliasIndex + 1) is long id && id > 0 ? id : null;
                uint page = r.IsDBNull(n + 1) ? 0 : (uint)r.GetInt64(n + 1);
                bool fromWalFrame = !r.IsDBNull(n + 2);
                string key = ShadowDatabaseBuilder.ValueKey(schema, i => values[i]);

                // ── database file ──
                int db;
                if (rowId is { } rid && fileSnap.Live.TryGetValue(rid, out var fileVersion))
                    db = SameValues(values, fileVersion.Values, aliasIndex) ? 1 : 3;
                else if (rowId is null && fileSnap.LiveKeys.Contains(key)) db = 1;
                else if (rowId is { } dr
                         ? fileSnap.DeletedRowIdKeys.Contains((dr, key)) || fileSnap.DeletedRowlessKeys.Contains(key)
                         : fileSnap.DeletedKeys.Contains(key)) db = 2;
                else db = 0;

                // ── WAL ──
                int? log = null;
                if (committedSnap is not null && walSeen is not null)
                {
                    // With a rowid, only a WAL record with the same rowid, or one that lost its rowid,
                    // can be this row — another row that merely holds the same values isn't.
                    bool appearsInWal = rowId is { } wr
                        ? walSeen.RowIdKeys.Contains((wr, key)) || walSeen.RowlessKeys.Contains(key)
                        : walSeen.Keys.Contains(key);
                    // Where this row came from: a live row's page in the committed state, a recovered
                    // row's WAL frame, or a file page the WAL rewrote.
                    bool sourceCovered = isRecovered
                        ? fromWalFrame || walPages.Contains(page)
                        : committed!.WalFrameNumberFor(page) is not null;
                    Snapshot.Version? committedVersion = rowId is { } cr && committedSnap.Live.TryGetValue(cr, out var cv) ? cv : null;
                    bool liveVersionOnWalPage = committedVersion is { } v && committed!.WalFrameNumberFor(v.Page) is not null;

                    if (appearsInWal || sourceCovered || liveVersionOnWalPage)
                    {
                        bool liveInCommitted = committedVersion is { } c
                            ? SameValues(values, c.Values, aliasIndex)
                            : rowId is null && committedSnap.LiveKeys.Contains(key);
                        if (liveInCommitted) log = 1;
                        else if (committedVersion is not null) log = 3;
                        else if (appearsInWal || db == 1) log = 2;
                        else log = 0;
                    }
                }

                // ── current ──
                bool isCurrent = rowId is { } cur
                    ? current.Live.TryGetValue(cur, out var curVersion) && SameValues(values, curVersion.Values, aliasIndex)
                    : current.LiveKeys.Contains(key);

                updates.Add((shadowRowId, db, log, isCurrent ? 1 : 0));
            }
        }

        using var transaction = connection.BeginTransaction();
        using var write = connection.CreateCommand();
        write.Transaction = transaction;
        write.CommandText = $"""
            UPDATE {ShadowDatabaseBuilder.QuoteIdentifier(shadowTable)}
            SET {ShadowDatabaseBuilder.QuoteIdentifier(DbStateColumn)} = @db,
                {ShadowDatabaseBuilder.QuoteIdentifier(LogStateColumn)} = @log,
                {ShadowDatabaseBuilder.QuoteIdentifier(IsCurrentColumn)} = @cur
            WHERE rowid = @id
            """;
        var pDb = write.Parameters.Add("@db", SqliteType.Integer);
        var pLog = write.Parameters.Add("@log", SqliteType.Integer);
        var pCur = write.Parameters.Add("@cur", SqliteType.Integer);
        var pId = write.Parameters.Add("@id", SqliteType.Integer);
        foreach (var (id, db, log, cur) in updates)
        {
            pDb.Value = db;
            pLog.Value = log is { } l ? l : DBNull.Value;
            pCur.Value = cur;
            pId.Value = id;
            write.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    /// <summary>Same values over every non-alias column the evidence record actually has — a row
    /// written before an ALTER TABLE ... ADD COLUMN has fewer fields than the shadow table, which
    /// fills the rest with the column's default.</summary>
    private static bool SameValues(string?[] shadowValues, string?[] evidenceValues, int aliasIndex)
    {
        for (int i = 0; i < evidenceValues.Length && i < shadowValues.Length; i++)
        {
            if (i == aliasIndex) continue;
            if (!string.Equals(shadowValues[i], evidenceValues[i], StringComparison.Ordinal)) return false;
        }
        return true;
    }

    private static string? Norm(object? v) => v switch
    {
        null or DBNull => null,
        byte[] bytes   => "x" + Convert.ToHexString(bytes),
        _              => Convert.ToString(v, CultureInfo.InvariantCulture),
    };

    private static string?[] Norm(IReadOnlyList<SqliteValue?> fields) =>
        fields.Select(f => Norm(f?.Value)).ToArray();

    // ── Snapshots ────────────────────────────────────────────────────────────

    private sealed record TableEntry(TableSchema Schema, uint RootPage);

    private static Dictionary<string, TableEntry> Tables(SqliteForensicDatabase db)
    {
        var result = new Dictionary<string, TableEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in db.ReadSqliteMaster())
        {
            if (row.ObjectType != SqliteMasterObjectType.Table || row.Name is null || row.Sql is null || row.RootPage is null) continue;
            if (row.Name.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase)) continue;
            if (row.Sql.Contains("VIRTUAL TABLE", StringComparison.OrdinalIgnoreCase)) continue;
            var schema = CreateTableParser.ExtractTableSchema(row.Sql);
            if (schema is null || schema.Columns.Count == 0) continue;
            result[row.Name] = new TableEntry(schema, row.RootPage.Value);
        }
        return result;
    }

    /// <summary>One table's rows in one version of the database: live rows by rowid (with the page
    /// each is on), their value keys, and — for the file — the value keys of its deleted data.</summary>
    private sealed class Snapshot
    {
        public readonly record struct Version(string?[] Values, uint Page);

        public Dictionary<long, Version> Live { get; } = [];
        public HashSet<string> LiveKeys { get; } = [];
        public HashSet<string> DeletedKeys { get; } = [];
        public HashSet<(long RowId, string Key)> DeletedRowIdKeys { get; } = [];
        public HashSet<string> DeletedRowlessKeys { get; } = [];

        public static Snapshot Of(SqliteForensicDatabase db, TableEntry? entry, TableSchema schema, bool includeDeleted)
        {
            var snap = new Snapshot();
            if (entry is null) return snap; // the table doesn't exist in this version

            foreach (var row in db.ReadTableRows(entry.RootPage))
            {
                snap.Live[row.RowId] = new Version(Norm(row.FieldValues), row.PageNumber);
                snap.LiveKeys.Add(ShadowDatabaseBuilder.ValueKey(schema, row.FieldValues));
            }

            if (includeDeleted)
            {
                var structure = RecordStructure.FromSchema(schema);
                foreach (uint p in db.GetTreePageNumbers(entry.RootPage))
                {
                    if (db.ReadPage(p) is not TableBTreeLeafPage leaf) continue;
                    leaf.CarveDeletedCells(structure);
                    leaf.CarveFreeblockCells(structure);
                    foreach (var cell in leaf.DeletedCells.Concat(leaf.CarvedCells).Concat(leaf.FreeblockCells))
                    {
                        string key = ShadowDatabaseBuilder.ValueKey(schema, cell.FieldValues);
                        snap.DeletedKeys.Add(key);
                        if (cell.RowId.Value > 0) snap.DeletedRowIdKeys.Add((cell.RowId.Value, key));
                        else snap.DeletedRowlessKeys.Add(key);
                    }
                }
            }
            return snap;
        }
    }

    /// <summary>Every record of one table found anywhere in the WAL — live cells and deleted data
    /// of every frame — by value key, by (rowid, value key) where the rowid survives, and separately
    /// the value keys of records that lost their rowid.</summary>
    private sealed class WalRecords
    {
        public HashSet<string> Keys { get; } = [];
        public HashSet<(long RowId, string Key)> RowIdKeys { get; } = [];
        public HashSet<string> RowlessKeys { get; } = [];

        public static WalRecords Of(WalFile wal, string table, TableSchema schema,
            Dictionary<uint, string> committedMap, Dictionary<uint, string> fileMap, DatabaseHeader header)
        {
            var seen = new WalRecords();
            var structure = RecordStructure.FromSchema(schema);
            foreach (var frame in wal.Frames)
            {
                uint p = frame.Header.PageNumber;
                string? owner = committedMap.GetValueOrDefault(p) ?? fileMap.GetValueOrDefault(p);
                if (!string.Equals(owner, table, StringComparison.OrdinalIgnoreCase)) continue;
                if (frame.Page is not TableBTreeLeafPage leaf) continue;

                // Parse a fresh copy: carving stores its results on the page object
                if (SqlitePage.FromBytes(p, leaf.Data.Length, (byte[])leaf.Data.Clone(), header.TextEncoding, header.ReservedBytesPerPage)
                    is not TableBTreeLeafPage page) continue;
                page.CarveDeletedCells(structure);
                page.CarveFreeblockCells(structure);
                foreach (var cell in page.Cells.Concat(page.DeletedCells).Concat(page.CarvedCells).Concat(page.FreeblockCells))
                {
                    string key = ShadowDatabaseBuilder.ValueKey(schema, cell.FieldValues);
                    seen.Keys.Add(key);
                    if (cell.RowId.Value > 0) seen.RowIdKeys.Add((cell.RowId.Value, key));
                    else seen.RowlessKeys.Add(key);
                }
            }
            return seen;
        }
    }

    // ── Schema helpers ───────────────────────────────────────────────────────

    private static bool TableExists(SqliteConnection connection, string table)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @n";
        cmd.Parameters.AddWithValue("@n", table);
        return cmd.ExecuteScalar() is not null;
    }

    private static void EnsureColumns(SqliteConnection connection, string table)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var info = connection.CreateCommand())
        {
            info.CommandText = $"SELECT name FROM pragma_table_info({ShadowDatabaseBuilder.QuoteLiteral(table)})";
            using var r = info.ExecuteReader();
            while (r.Read()) existing.Add(r.GetString(0));
        }
        foreach (string column in new[] { DbStateColumn, LogStateColumn, IsCurrentColumn })
        {
            if (existing.Contains(column)) continue;
            using var alter = connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE {ShadowDatabaseBuilder.QuoteIdentifier(table)} ADD COLUMN {ShadowDatabaseBuilder.QuoteIdentifier(column)} INTEGER";
            alter.ExecuteNonQuery();
        }
    }
}
