using Microsoft.Data.Sqlite;
using SHARD.Core.Enums;
using SHARD.Core.Pages;
using SHARD.Core.Records;
using SHARD.Core.Schema;
using SHARD.Core.Shadow;
using SHARD.Core.WAL;

namespace SHARD.Core.Recovery;

// ── DTOs ─────────────────────────────────────────────────────────────────────
// Plain, JSON-source-generation-friendly shapes: primitives, strings, and
// Dictionary<string, object?> (boxed long/double/string/bool/byte[]/null) for row
// field values — deliberately not tied to any internal parsing type, so this is a
// stable contract for out-of-process consumers (SHARD.Native's exports serialize
// these types directly).

/// <summary>Options controlling <see cref="SqliteRecoveryFacade.Recover"/>.</summary>
public sealed record RecoveryOptions(
    bool ProcessWal = true,
    CarveMode? CarveMode = null,
    IReadOnlyList<string>? CarveTableFilter = null);

public sealed record TableRecoverySummary(string TableName, int LiveRowCount, int RecoveredRowCount);

public sealed record RecoveryResult(
    string OutputPath,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<TableRecoverySummary> Tables,
    int WalRecordsInserted,
    int CarvedRecords,
    int CarveAmbiguousSkipped);

public sealed record DatabaseHeaderInfo(
    string Magic, int PageSize, string WriteVersion, int ReadVersion, int ReservedBytesPerPage,
    uint FileChangeCounter, uint DatabaseSizeInPages, uint FirstFreelistTrunkPage, uint TotalFreelistPages,
    uint SchemaCookie, uint SchemaFormat, string TextEncoding, uint UserVersion, uint ApplicationId,
    string SqliteVersion);

public sealed record SchemaEntryInfo(string Type, string Name, string TableName, uint? RootPage, string? Sql, uint PageNumber, int CellOffset);

public sealed record PageInfo(uint PageNumber, string Type, string? TableName, int? DeletedCellCount);

public sealed record RowInfo(long RowId, uint PageNumber, int CellOffset, IReadOnlyDictionary<string, object?> Fields);

public sealed record CarvedRowInfo(string TableName, long RowId, uint PageNumber, int CellOffset, IReadOnlyDictionary<string, object?> Fields);

/// <summary>
/// High-level entry point for third-party consumers (other .NET tools via NuGet, or any
/// language via <c>SHARD.Native</c>'s C ABI). Wraps the lower-level building blocks
/// (<see cref="SqliteForensicDatabase"/>, <see cref="ShadowDatabaseBuilder"/>,
/// <see cref="OrphanPageCarver"/>) so a caller doesn't need to know the right call order —
/// that knowledge previously only existed as a reference implementation in
/// <c>SHARD.Cli/Program.cs</c> and <c>ShadowDatabaseBuilder.Create</c>'s own callers.
/// </summary>
public static class SqliteRecoveryFacade
{
    /// <summary>
    /// Opens <paramref name="inputPath"/>, builds a fully recovered SQLite database at
    /// <paramref name="outputPath"/> (live rows, in-tree deleted/freeblock-recovered rows, and —
    /// per <paramref name="options"/> — WAL-recovered rows and/or orphan-page-carved rows), and
    /// returns a summary. <paramref name="outputPath"/> is itself a normal, valid SQLite database
    /// any SQLite library (including Python's stdlib <c>sqlite3</c>) can open directly.
    /// </summary>
    public static RecoveryResult Recover(string inputPath, string outputPath, RecoveryOptions? options = null)
    {
        options ??= new RecoveryOptions();

        using var database = SqliteForensicDatabase.Open(inputPath);
        var warnings = ShadowDatabaseBuilder.Create(outputPath, database);

        int walInserted = 0;
        if (options.ProcessWal)
        {
            string walPath = inputPath + "-wal";
            if (File.Exists(walPath))
            {
                var wal = new WalFile(walPath, database.Header.TextEncoding, database.Header.ReservedBytesPerPage);
                using var walConnection = new SqliteConnection($"Data Source={outputPath}");
                walConnection.Open();
                walInserted = ShadowDatabaseBuilder.InsertWalDeletedRows(walConnection, database, wal);
            }
        }

        int carved = 0, carveAmbiguous = 0;
        if (options.CarveMode is { } mode)
        {
            var candidates = OrphanPageCarver.BuildCandidates(database, mode, options.CarveTableFilter);
            var results = OrphanPageCarver.Carve(database, candidates, out carveAmbiguous);
            carved = results.Count;

            using var carveConnection = new SqliteConnection($"Data Source={outputPath}");
            carveConnection.Open();
            ShadowDatabaseBuilder.PersistCarvedOrphanRecords(carveConnection, results);
        }

        var tables = new List<TableRecoverySummary>();
        using (var summaryConnection = new SqliteConnection($"Data Source={outputPath};Mode=ReadOnly"))
        {
            summaryConnection.Open();
            foreach (var row in database.ReadSqliteMaster())
            {
                if (row.ObjectType != SqliteMasterObjectType.Table || row.RootPage is null) continue;
                int live = database.ReadTableRows(row.RootPage.Value).Count();
                int recovered = TryQueryCount(summaryConnection, $"\"{ShadowDatabaseBuilder.RecoveredTablePrefix}{row.Name}\"");
                tables.Add(new TableRecoverySummary(row.Name, live, recovered));
            }
        }

        return new RecoveryResult(outputPath, warnings, tables, walInserted, carved, carveAmbiguous);
    }

    public static DatabaseHeaderInfo GetHeader(string inputPath)
    {
        using var database = SqliteForensicDatabase.Open(inputPath);
        var h = database.Header;
        return new DatabaseHeaderInfo(
            h.Magic.TrimEnd('\0'), h.PageSize, h.WriteVersionName, h.ReadVersion, h.ReservedBytesPerPage,
            h.FileChangeCounter, h.DatabaseSizeInPages, h.FirstFreelistTrunkPage, h.TotalFreelistPages,
            h.SchemaCookie, h.SchemaFormat, h.TextEncodingName, h.UserVersion, h.ApplicationId,
            $"{h.SqliteVersionNumber / 1_000_000}.{h.SqliteVersionNumber % 1_000_000 / 1_000}.{h.SqliteVersionNumber % 1_000}");
    }

    public static IReadOnlyList<SchemaEntryInfo> GetSchema(string inputPath)
    {
        using var database = SqliteForensicDatabase.Open(inputPath);
        return database.ReadSqliteMaster()
            .Select(r => new SchemaEntryInfo(r.ObjectType.ToString().ToLowerInvariant(), r.Name, r.TableName, r.RootPage, r.Sql, r.PageNumber, r.CellOffset))
            .ToList();
    }

    public static IReadOnlyList<PageInfo> GetPages(string inputPath)
    {
        using var database = SqliteForensicDatabase.Open(inputPath);
        var pageMap = database.BuildPageTableMap();
        var pages = new List<PageInfo>();
        for (uint n = 1; n <= database.PageCount; n++)
        {
            var page = database.ReadPage(n);
            pageMap.TryGetValue(n, out string? tableName);
            int? deleted = page is TableBTreeLeafPage tlp ? tlp.DeletedCells.Count : null;
            pages.Add(new PageInfo(n, page.PageType.ToString(), tableName, deleted));
        }
        return pages;
    }

    public static IReadOnlyList<RowInfo> GetRows(string inputPath, string tableName)
    {
        using var database = SqliteForensicDatabase.Open(inputPath);
        var master = FindTable(database, tableName);
        var schema = database.GetTableSchema(tableName) ?? throw new InvalidOperationException($"Could not parse schema for table '{tableName}'.");

        return database.ReadTableRows(master.RootPage!.Value)
            .Select(row => new RowInfo(row.RowId, row.PageNumber, row.CellOffset, FieldsFromRow(schema, row)))
            .ToList();
    }

    public static IReadOnlyList<RowInfo> GetDeletedRows(string inputPath, string tableName)
    {
        using var database = SqliteForensicDatabase.Open(inputPath);
        var master = FindTable(database, tableName);
        var schema = database.GetTableSchema(tableName) ?? throw new InvalidOperationException($"Could not parse schema for table '{tableName}'.");
        var recordStructure = RecordStructure.FromSchema(schema);

        return database.GetTreePageNumbers(master.RootPage!.Value)
            .Select(p => database.ReadPage(p))
            .OfType<TableBTreeLeafPage>()
            .SelectMany(p =>
            {
                p.CarveDeletedCells(recordStructure);
                p.CarveFreeblockCells(recordStructure);
                return p.DeletedCells.Concat(p.CarvedCells).Concat(p.FreeblockCells)
                    .Select(cell => new RowInfo(cell.RowId.Value, p.PageNumber, cell.PageOffset, FieldsFromCell(schema, cell)));
            })
            .ToList();
    }

    /// <summary>
    /// Read-only scan: tries every candidate table's schema against pages with no known owner and
    /// returns whatever uniquely matches. Does not write anything — pass a <see cref="CarveMode"/>
    /// to <see cref="Recover"/> if you want results persisted into the output database.
    /// </summary>
    public static IReadOnlyList<CarvedRowInfo> CarveUnknownPages(string inputPath, CarveMode mode, IReadOnlyList<string>? tableFilter = null)
    {
        using var database = SqliteForensicDatabase.Open(inputPath);
        var candidates = OrphanPageCarver.BuildCandidates(database, mode, tableFilter);
        var results = OrphanPageCarver.Carve(database, candidates, out _);
        return results
            .Select(r => new CarvedRowInfo(r.Schema.TableName, r.Cell.RowId.Value, r.PageNumber, r.Cell.PageOffset, FieldsFromCell(r.Schema, r.Cell)))
            .ToList();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static SqliteMasterRow FindTable(SqliteForensicDatabase database, string tableName)
    {
        var master = database.ReadSqliteMaster().FirstOrDefault(r =>
            r.ObjectType == SqliteMasterObjectType.Table && string.Equals(r.Name, tableName, StringComparison.OrdinalIgnoreCase));
        if (master is null || master.RootPage is null)
            throw new InvalidOperationException($"Table '{tableName}' not found.");
        return master;
    }

    /// <summary>Maps a decoded row's fields by schema column position — correctly, unlike SHARD.Cli's RowToDict (which has a known off-by-one for the rowid-alias column).</summary>
    private static Dictionary<string, object?> FieldsFromRow(TableSchema schema, TableRow row)
    {
        var dict = new Dictionary<string, object?>();
        for (int i = 0; i < schema.Columns.Count; i++)
        {
            var col = schema.Columns[i];
            dict[col.Name] = col.IsRowIdAlias ? row.RowId : (i < row.FieldValues.Count ? row.FieldValues[i]?.Value : null);
        }
        return dict;
    }

    private static Dictionary<string, object?> FieldsFromCell(TableSchema schema, BTreeLeafCell cell)
    {
        var dict = new Dictionary<string, object?>();
        for (int i = 0; i < schema.Columns.Count; i++)
        {
            var col = schema.Columns[i];
            dict[col.Name] = col.IsRowIdAlias ? cell.RowId.Value : (i < cell.FieldValues.Count ? cell.FieldValues[i]?.Value : null);
        }
        return dict;
    }

    private static int TryQueryCount(SqliteConnection connection, string quotedTable)
    {
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM {quotedTable}";
            var result = cmd.ExecuteScalar();
            return result is long l ? (int)l : 0;
        }
        catch (SqliteException)
        {
            return 0; // table doesn't exist — nothing was recovered for it
        }
    }
}
