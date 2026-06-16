namespace SHARD.Core.Shadow;

/// <summary>Persisted as project.json inside a project folder.</summary>
public sealed class ProjectManifest
{
    public string EvidenceFilePath { get; set; } = "";
    public string ShadowDatabaseFileName { get; set; } = "shadow.db";
    public DateTime CreatedUtc { get; set; }
}
