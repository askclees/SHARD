using System.Text.Json;

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
}
