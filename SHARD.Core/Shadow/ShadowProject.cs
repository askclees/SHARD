using System.Text.Json;
using Microsoft.Data.Sqlite;
using SHARD.Core.Enums;
using SHARD.Core.Records;
using SHARD.Core.Schema;

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
