using SHARD.Core.Integrity;

namespace SHARD.Core.Shadow;

/// <summary>Persisted as project.json inside a project folder.</summary>
public sealed class ProjectManifest
{
    /// <summary>1 = original (no hashes); 2 = adds <see cref="EvidenceHashes"/>. A version-1
    /// project.json has no such field and deserializes with this left at 1.</summary>
    public const int CurrentVersion = 2;

    public int ManifestVersion { get; set; } = 1;
    public string EvidenceFilePath { get; set; } = "";
    public string ShadowDatabaseFileName { get; set; } = "shadow.db";
    public DateTime CreatedUtc { get; set; }

    /// <summary>Hashes of the evidence files taken when the project was created; null for a
    /// project created before hashing existed.</summary>
    public List<EvidenceFileHash>? EvidenceHashes { get; set; }
}
