using System.Text.Json;
using Microsoft.Data.Sqlite;
using SHARD.Core.Comparison;
using SHARD.Core.Enums;
using SHARD.Core.Pages;
using SHARD.Core.Records;
using SHARD.Core.Recovery;
using SHARD.Core.Schema;
using SHARD.Core.WAL;

namespace SHARD.Core.Shadow;

/// <summary>
/// A shadow SQLite database mirroring an evidence file's schema and row data.
/// A project starts as a temporary (unsaved) state backed by a temp file the
/// moment an evidence file is opened; calling <see cref="SaveTo"/> persists it
/// to a named folder on disk.
/// </summary>
public sealed class ShadowProject : IDisposable
{
    private string? _projectFolder;
    private string _shadowDatabasePath;
    private string? _tempDbPath;
    private readonly ProjectManifest _manifest;

    public string? ProjectFolder     => _projectFolder;
    public string? ManifestPath      => _projectFolder is null ? null : Path.Combine(_projectFolder, "project.json");
    public string  ShadowDatabasePath => _shadowDatabasePath;
    public string  EvidenceFilePath   => _manifest.EvidenceFilePath;
    public DateTime CreatedUtc        => _manifest.CreatedUtc;
    public bool    IsUnsaved          => _tempDbPath is not null;

    private ShadowProject(string? projectFolder, ProjectManifest manifest, string shadowDatabasePath, string? tempDbPath)
    {
        _projectFolder       = projectFolder;
        _manifest            = manifest;
        _shadowDatabasePath  = shadowDatabasePath;
        _tempDbPath          = tempDbPath;
    }

    /// <summary>
    /// Create a temporary project backed by a temp file, immediately usable for
    /// queries and record recovery. Call <see cref="SaveTo"/> to persist to disk.
    /// </summary>
    public static (ShadowProject Project, IReadOnlyList<string> Warnings) CreateTemporary(
        string evidenceFilePath, SqliteForensicDatabase database)
    {
        // GetTempFileName creates a zero-byte file; rename with .db so SQLite is happy.
        string tempBase = Path.GetTempFileName();
        string tempPath = Path.ChangeExtension(tempBase, ".db");
        File.Move(tempBase, tempPath);

        var manifest = new ProjectManifest
        {
            EvidenceFilePath = evidenceFilePath,
            CreatedUtc       = DateTime.UtcNow,
        };

        var warnings = ShadowDatabaseBuilder.Create(tempPath, database);
        return (new ShadowProject(null, manifest, tempPath, tempPath), warnings);
    }

    /// <summary>
    /// Persist the temporary project to <paramref name="projectFolder"/> on disk,
    /// writing the manifest and copying the shadow database. Deletes the temp file.
    /// Throws if the project is already saved or the target already contains a shadow DB.
    /// </summary>
    public void SaveTo(string projectFolder)
    {
        Directory.CreateDirectory(projectFolder);

        string shadowDbPath = Path.Combine(projectFolder, _manifest.ShadowDatabaseFileName);
        if (File.Exists(shadowDbPath))
            throw new InvalidOperationException($"A shadow database already exists at '{shadowDbPath}'.");

        File.Copy(_shadowDatabasePath, shadowDbPath);

        File.WriteAllText(
            Path.Combine(projectFolder, "project.json"),
            JsonSerializer.Serialize(_manifest, new JsonSerializerOptions { WriteIndented = true }));

        if (_tempDbPath is not null)
        {
            try { File.Delete(_tempDbPath); } catch { }
            _tempDbPath = null;
        }
        _projectFolder      = projectFolder;
        _shadowDatabasePath = shadowDbPath;
    }

    /// <summary>Open an existing saved project folder (must contain project.json and shadow DB).</summary>
    public static ShadowProject Open(string projectFolder)
    {
        string manifestPath = Path.Combine(projectFolder, "project.json");
        if (!File.Exists(manifestPath))
            throw new InvalidOperationException($"No project.json found in '{projectFolder}'.");

        var manifest = JsonSerializer.Deserialize<ProjectManifest>(File.ReadAllText(manifestPath))
            ?? throw new InvalidOperationException($"Failed to read project manifest at '{manifestPath}'.");

        string shadowDbPath = Path.Combine(projectFolder, manifest.ShadowDatabaseFileName);
        if (!File.Exists(shadowDbPath))
            throw new InvalidOperationException($"No shadow database found at '{shadowDbPath}'.");

        return new ShadowProject(projectFolder, manifest, shadowDbPath, null);
    }

    /// <summary>
    /// Creates (if not already present) and populates a <c>_shard_deleted_{tableName}</c>
    /// table in the shadow database with rows read from a dropped table's valid root page.
    /// </summary>
    public void AddDeletedTableRecords(TableSchema schema, IEnumerable<TableRow> rows)
    {
        using var connection = new SqliteConnection($"Data Source={_shadowDatabasePath}");
        connection.Open();
        ShadowDatabaseBuilder.CreateAndPopulateDeletedTable(connection, schema, rows);
    }

    /// <summary>
    /// Inserts B-tree leaf cells carved from a freed page's raw bytes into the
    /// <c>_shard_deleted_{tableName}</c> shadow table, creating it if necessary.
    /// Used when the original root page is now a freelist page but its bytes may
    /// still contain the original table records.
    /// </summary>
    public void AddFreedPageCarvedRecords(
        TableSchema schema, IReadOnlyList<BTreeLeafCell> cells, uint pageNumber)
    {
        using var connection = new SqliteConnection($"Data Source={_shadowDatabasePath}");
        connection.Open();
        ShadowDatabaseBuilder.AppendCarvedCellsToDeletedTable(connection, schema, cells, pageNumber);
    }

    /// <summary>
    /// Updates <c>_shard_pages</c> to label the given page numbers as belonging to a
    /// dropped table (shown as <c>"tableName (deleted)"</c> in the Pages list).
    /// </summary>
    public void TagDeletedTablePages(string tableName, IEnumerable<uint> pageNumbers)
    {
        using var connection = new SqliteConnection($"Data Source={_shadowDatabasePath}");
        connection.Open();
        ShadowDatabaseBuilder.TagDeletedTablePages(connection, tableName, pageNumbers);
    }

    /// <summary>Insert a recovered (deleted) record into the shadow database.</summary>
    public void SaveRecoveredRecord(TableSchema schema, BTreeLeafCell cell, uint pageNumber, int cellOffset)
    {
        using var connection = new SqliteConnection($"Data Source={_shadowDatabasePath}");
        connection.Open();
        ShadowDatabaseBuilder.InsertRecoveredRecord(connection, schema, cell, pageNumber, cellOffset);
    }

    /// <summary>
    /// Explicit, user-triggered scan of pages with no known owning table: tries every candidate
    /// table's <see cref="RecordStructure"/> against each such page's raw bytes via
    /// <see cref="OrphanPageCarver"/>, and persists whatever uniquely matches. Never runs
    /// automatically — callers decide when to invoke this, and build <paramref name="candidates"/>
    /// themselves (typically via <see cref="OrphanPageCarver.BuildCandidates"/>, optionally with
    /// user-reviewed/adjusted structures — see <see cref="RecordStructure.NarrowColumn"/>).
    /// Returns the number of records carved and, via <paramref name="ambiguousSkipped"/>, how many
    /// candidate byte ranges were rejected for matching more than one table.
    /// </summary>
    public int CarveUnknownPages(
        SqliteForensicDatabase database,
        IReadOnlyList<(TableSchema Schema, RecordStructure Structure)> candidates,
        out int ambiguousSkipped)
    {
        var carved = OrphanPageCarver.Carve(database, candidates, out ambiguousSkipped);

        using var connection = new SqliteConnection($"Data Source={_shadowDatabasePath}");
        connection.Open();
        ShadowDatabaseBuilder.PersistCarvedOrphanRecords(connection, carved);

        return carved.Count;
    }

    /// <summary>
    /// Walks all WAL frames (including frames past the checkpoint boundary) and
    /// inserts into the recovered shadow tables any records that were deleted or
    /// overwritten before the current database version.
    /// Returns the number of records inserted.
    /// </summary>
    public int RecoverWalDeletedRows(WalFile walFile, SqliteForensicDatabase database)
    {
        using var connection = new SqliteConnection($"Data Source={_shadowDatabasePath}");
        connection.Open();
        return ShadowDatabaseBuilder.InsertWalDeletedRows(connection, database, walFile);
    }

    /// <summary>
    /// Compares each WAL frame against the current database page and inserts any
    /// records that are new in the WAL into the corresponding live shadow table.
    /// Returns the number of records inserted.
    /// </summary>
    public int SyncWalFramesToShadow(WalFile walFile, SqliteForensicDatabase database)
    {
        using var connection = new SqliteConnection($"Data Source={_shadowDatabasePath}");
        connection.Open();

        var latestFrames = walFile.Frames
            .GroupBy(f => f.Header.PageNumber)
            .Select(g => g.Last())
            .ToList();

        int inserted = 0;

        foreach (var frame in latestFrames)
        {
            if (frame.Page is not TableBTreeLeafPage walPage) continue;

            string? tableName = GetPageTableName(connection, frame.Header.PageNumber);
            if (tableName is null) continue;

            var schema = database.GetTableSchema(tableName);
            if (schema is null) continue;

            TableBTreeLeafPageComparison comparison;
            if (frame.Header.PageNumber <= database.PageCount &&
                database.ReadPage(frame.Header.PageNumber) is TableBTreeLeafPage dbLeafPage)
            {
                comparison = dbLeafPage.Compare(walPage);
            }
            else
            {
                comparison = new TableBTreeLeafPageComparison { AddedRecords = walPage.Cells };
            }

            using var transaction = connection.BeginTransaction();
            foreach (var cell in comparison.AddedRecords)
            {
                ShadowDatabaseBuilder.InsertWalRecord(
                    connection, schema, cell, frame.Header.PageNumber, cell.PageOffset);
                inserted++;
            }
            transaction.Commit();
        }

        return inserted;
    }

    private static string? GetPageTableName(SqliteConnection connection, uint pageNumber)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT table_name FROM \"{ShadowDatabaseBuilder.InternalTablePrefix}pages\" WHERE page_number = @p";
        command.Parameters.AddWithValue("@p", (long)pageNumber);
        return command.ExecuteScalar() as string;
    }

    /// <summary>Read the persisted page classifications from this project's shadow database.</summary>
    public IReadOnlyList<(uint PageNumber, PageType Type, string? TableName)> ReadPageTypes()
    {
        var result = new List<(uint PageNumber, PageType Type, string? TableName)>();

        using var connection = new SqliteConnection($"Data Source={_shadowDatabasePath};Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT page_number, page_type, table_name FROM "{ShadowDatabaseBuilder.InternalTablePrefix}pages" ORDER BY page_number
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            uint pageNumber = (uint)reader.GetInt64(0);
            var pageType = Enum.Parse<PageType>(reader.GetString(1));
            string? tableName = reader.IsDBNull(2) ? null : reader.GetString(2);
            result.Add((pageNumber, pageType, tableName));
        }

        return result;
    }

    public void Dispose()
    {
        if (_tempDbPath is not null && File.Exists(_tempDbPath))
            try { File.Delete(_tempDbPath); } catch { }
    }
}
