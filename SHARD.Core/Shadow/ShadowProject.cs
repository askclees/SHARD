using System.Text.Json;
using Microsoft.Data.Sqlite;
using SHARD.Core.Comparison;
using SHARD.Core.Enums;
using SHARD.Core.Pages;
using SHARD.Core.Records;
using SHARD.Core.Schema;
using SHARD.Core.WAL;

namespace SHARD.Core.Shadow;

/// <summary>
/// A project folder containing a manifest (project.json) and a shadow SQLite
/// database mirroring the structure of an evidence file's schema.
/// </summary>
public sealed class ShadowProject
{
    public string ProjectFolder { get; }
    public string ManifestPath { get; }
    public string ShadowDatabasePath { get; }
    public string EvidenceFilePath { get; }
    public DateTime CreatedUtc { get; }

    private ShadowProject(string projectFolder, ProjectManifest manifest)
    {
        ProjectFolder      = projectFolder;
        ManifestPath       = Path.Combine(projectFolder, "project.json");
        ShadowDatabasePath = Path.Combine(projectFolder, manifest.ShadowDatabaseFileName);
        EvidenceFilePath   = manifest.EvidenceFilePath;
        CreatedUtc         = manifest.CreatedUtc;
    }

    public static ShadowProject Create(string projectFolder, string evidenceFilePath, SqliteForensicDatabase database)
    {
        Directory.CreateDirectory(projectFolder);

        var manifest = new ProjectManifest
        {
            EvidenceFilePath = evidenceFilePath,
            CreatedUtc       = DateTime.UtcNow,
        };

        string shadowDbPath = Path.Combine(projectFolder, manifest.ShadowDatabaseFileName);
        if (File.Exists(shadowDbPath))
            throw new InvalidOperationException($"A shadow database already exists at '{shadowDbPath}'.");

        ShadowDatabaseBuilder.Create(shadowDbPath, database);

        string manifestPath = Path.Combine(projectFolder, "project.json");
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

        return new ShadowProject(projectFolder, manifest);
    }

    /// <summary>Open an existing project folder (must already contain a project.json and shadow database).</summary>
    public static ShadowProject Open(string projectFolder)
    {
        string manifestPath = Path.Combine(projectFolder, "project.json");
        if (!File.Exists(manifestPath))
            throw new InvalidOperationException($"No project.json found in '{projectFolder}'.");

        var manifest = JsonSerializer.Deserialize<ProjectManifest>(File.ReadAllText(manifestPath))
            ?? throw new InvalidOperationException($"Failed to read project manifest at '{manifestPath}'.");

        var project = new ShadowProject(projectFolder, manifest);
        if (!File.Exists(project.ShadowDatabasePath))
            throw new InvalidOperationException($"No shadow database found at '{project.ShadowDatabasePath}'.");

        return project;
    }

    /// <summary>
    /// Inserts a recovered (deleted) record into the shadow database's
    /// <c>_shard_recovered_{tableName}</c> table.
    /// </summary>
    public void SaveRecoveredRecord(TableSchema schema, BTreeLeafCell cell, uint pageNumber, int cellOffset)
    {
        using var connection = new SqliteConnection($"Data Source={ShadowDatabasePath}");
        connection.Open();
        ShadowDatabaseBuilder.InsertRecoveredRecord(connection, schema, cell, pageNumber, cellOffset);
    }

    /// <summary>
    /// Compares each WAL frame against the current database page and inserts any
    /// records that are new in the WAL into the corresponding live shadow table.
    /// Uses the last frame per page (the current WAL state) and skips pages whose
    /// table is not recorded in the shadow database.
    /// Returns the number of records inserted.
    /// </summary>
    public int SyncWalFramesToShadow(WalFile walFile, SqliteForensicDatabase database)
    {
        using var connection = new SqliteConnection($"Data Source={ShadowDatabasePath}");
        connection.Open();

        // Latest WAL frame wins for each page number.
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

            // Compare WAL page against the current database page to find new records.
            TableBTreeLeafPageComparison comparison;
            if (frame.Header.PageNumber <= database.PageCount &&
                database.ReadPage(frame.Header.PageNumber) is TableBTreeLeafPage dbLeafPage)
            {
                comparison = dbLeafPage.Compare(walPage);
            }
            else
            {
                // Page exists only in the WAL — every cell is new.
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

        using var connection = new SqliteConnection($"Data Source={ShadowDatabasePath};Mode=ReadOnly");
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
}
