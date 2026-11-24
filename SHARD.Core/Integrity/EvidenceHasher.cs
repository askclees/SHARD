using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace SHARD.Core.Integrity;

/// <summary>Which part of an evidence set a hashed file is.</summary>
public static class EvidenceFileRole
{
    public const string Database = "database";
    public const string Wal      = "wal";
    public const string Journal  = "journal";
    public const string Shm      = "shm";

    /// <summary>Sidecar roles with the suffix SQLite appends to the database path.</summary>
    public static readonly IReadOnlyList<(string Role, string Suffix)> Sidecars =
    [
        (Wal,     "-wal"),
        (Journal, "-journal"),
        (Shm,     "-shm"),
    ];
}

/// <summary>
/// Hashes of one evidence file, plus the size and last-modified time it had when hashed.
/// Hashes are lowercase hex. <see cref="HashedUtc"/> is when hashing finished.
/// </summary>
public sealed record EvidenceFileHash(
    string Role,
    string Path,
    long Length,
    DateTime LastWriteUtc,
    string Md5,
    string Sha1,
    string Sha256,
    DateTime HashedUtc)
{
    /// <summary>True when the content hashes match. Path, timestamps and hashing time are ignored.</summary>
    public bool ContentMatches(EvidenceFileHash other) =>
        Length == other.Length
        && string.Equals(Md5,    other.Md5,    StringComparison.OrdinalIgnoreCase)
        && string.Equals(Sha1,   other.Sha1,   StringComparison.OrdinalIgnoreCase)
        && string.Equals(Sha256, other.Sha256, StringComparison.OrdinalIgnoreCase);
}

[JsonConverter(typeof(JsonStringEnumConverter<HashVerificationStatus>))]
public enum HashVerificationStatus
{
    /// <summary>File still exists with identical content.</summary>
    Match,
    /// <summary>File still exists but its content differs.</summary>
    Changed,
    /// <summary>File was in the recorded set but no longer exists.</summary>
    Missing,
    /// <summary>File exists now but wasn't in the recorded set (e.g. a -wal created since).</summary>
    Added,
}

/// <summary>One file's outcome when comparing a recorded hash set against the files now on disk.
/// <see cref="Recorded"/> is null for <see cref="HashVerificationStatus.Added"/>;
/// <see cref="Current"/> is null for <see cref="HashVerificationStatus.Missing"/>.</summary>
public sealed record HashVerification(
    string Role,
    HashVerificationStatus Status,
    EvidenceFileHash? Recorded,
    EvidenceFileHash? Current);

/// <summary>
/// Computes MD5, SHA-1 and SHA-256 of evidence files in a single read pass each. Files are opened
/// read-only and shared for read/write/delete, so hashing never blocks or alters another
/// process that has them open (a WAL owner may still be writing).
/// </summary>
public static class EvidenceHasher
{
    private const int BufferSize = 1024 * 1024;

    /// <summary>
    /// Hashes one file. <paramref name="reportProgress"/>, if given, is called after each buffer
    /// with (bytes hashed so far, total length) — for showing progress on a large file.
    /// </summary>
    public static EvidenceFileHash HashFile(string path, string role, Action<long, long>? reportProgress = null)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, BufferSize, FileOptions.SequentialScan);

        // Read after the file is open, so it describes the same file the hashed bytes come from.
        var info = new FileInfo(path);
        DateTime lastWriteUtc = info.LastWriteTimeUtc;

        using var md5    = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        using var sha1   = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        var buffer = new byte[BufferSize];
        long total = stream.Length;
        long read = 0;
        int n;
        while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            var chunk = buffer.AsSpan(0, n);
            md5.AppendData(chunk);
            sha1.AppendData(chunk);
            sha256.AppendData(chunk);
            read += n;
            reportProgress?.Invoke(read, total);
        }

        return new EvidenceFileHash(
            role, Path.GetFullPath(path), read, lastWriteUtc,
            Convert.ToHexStringLower(md5.GetHashAndReset()),
            Convert.ToHexStringLower(sha1.GetHashAndReset()),
            Convert.ToHexStringLower(sha256.GetHashAndReset()),
            DateTime.UtcNow);
    }

    /// <summary>
    /// Hashes the database at <paramref name="evidencePath"/> and every sidecar
    /// (<c>-wal</c>, <c>-journal</c>, <c>-shm</c>) that exists beside it — including one SHARD
    /// itself ignores, so the record covers everything that was on disk.
    /// <paramref name="reportProgress"/> gets (role, bytes hashed, total bytes) per buffer.
    /// </summary>
    public static IReadOnlyList<EvidenceFileHash> HashEvidenceSet(
        string evidencePath, Action<string, long, long>? reportProgress = null)
    {
        var hashes = new List<EvidenceFileHash>();
        foreach (var (role, path) in EvidenceSetPaths(evidencePath))
            hashes.Add(HashFile(path, role, reportProgress is null ? null : (done, total) => reportProgress(role, done, total)));
        return hashes;
    }

    /// <summary>
    /// Compares <paramref name="recorded"/> against <paramref name="current"/> by role, reporting
    /// every file as matching, changed, missing (recorded only) or added (current only).
    /// </summary>
    public static IReadOnlyList<HashVerification> Compare(
        IReadOnlyList<EvidenceFileHash> recorded, IReadOnlyList<EvidenceFileHash> current)
    {
        var results = new List<HashVerification>();
        foreach (var rec in recorded)
        {
            var cur = current.FirstOrDefault(c => c.Role == rec.Role);
            var status = cur is null           ? HashVerificationStatus.Missing
                       : rec.ContentMatches(cur) ? HashVerificationStatus.Match
                       :                           HashVerificationStatus.Changed;
            results.Add(new HashVerification(rec.Role, status, rec, cur));
        }
        foreach (var cur in current.Where(c => recorded.All(r => r.Role != c.Role)))
            results.Add(new HashVerification(cur.Role, HashVerificationStatus.Added, null, cur));
        return results;
    }

    /// <summary>Re-hashes the evidence set on disk and compares it against <paramref name="recorded"/>.</summary>
    public static IReadOnlyList<HashVerification> Verify(
        string evidencePath, IReadOnlyList<EvidenceFileHash> recorded,
        Action<string, long, long>? reportProgress = null) =>
        Compare(recorded, HashEvidenceSet(evidencePath, reportProgress));

    private static IEnumerable<(string Role, string Path)> EvidenceSetPaths(string evidencePath)
    {
        yield return (EvidenceFileRole.Database, evidencePath);
        foreach (var (role, suffix) in EvidenceFileRole.Sidecars)
        {
            string path = evidencePath + suffix;
            if (File.Exists(path)) yield return (role, path);
        }
    }
}
