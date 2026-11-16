using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SHARD.Core.Integrity;
using SHARD.Core.Recovery;
using SHARD.Core.Session;
using SHARD.Core.Shadow;

namespace SHARD.Core.Tests;

public class EvidenceHashingTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;

    public EvidenceHashingTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"shard_hashing_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "evidence.db");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private static string WalFixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "TestData", "SHARDCreated", "WAL", name);

    /// <summary>Copies the places_wal fixture (database, -wal and -shm) into the test folder.</summary>
    private void CopyWalFixture()
    {
        File.Copy(WalFixturePath("places_wal.db"),     _dbPath);
        File.Copy(WalFixturePath("places_wal.db-wal"), _dbPath + "-wal");
        File.Copy(WalFixturePath("places_wal.db-shm"), _dbPath + "-shm");
    }

    // ── Hashing ──────────────────────────────────────────────────────────────

    [Fact]
    public void HashFile_MatchesKnownDigests()
    {
        string path = Path.Combine(_dir, "abc.bin");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes("abc"));

        var h = EvidenceHasher.HashFile(path, EvidenceFileRole.Database);

        Assert.Equal("900150983cd24fb0d6963f7d28e17f72", h.Md5);
        Assert.Equal("a9993e364706816aba3e25717850c26c9cd0d89d", h.Sha1);
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", h.Sha256);
        Assert.Equal(3, h.Length);
        Assert.Equal(Path.GetFullPath(path), h.Path);
    }

    [Fact]
    public void HashFile_EmptyFile_MatchesKnownDigests()
    {
        string path = Path.Combine(_dir, "empty.bin");
        File.WriteAllBytes(path, []);

        var h = EvidenceHasher.HashFile(path, EvidenceFileRole.Journal);

        Assert.Equal("d41d8cd98f00b204e9800998ecf8427e", h.Md5);
        Assert.Equal("da39a3ee5e6b4b0d3255bfef95601890afd80709", h.Sha1);
        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", h.Sha256);
    }

    [Fact]
    public void HashFile_LargerThanBuffer_HashesEveryByte()
    {
        // 2.5 MB spans several 1 MB read buffers
        var data = new byte[2_500_000];
        new Random(42).NextBytes(data);
        string path = Path.Combine(_dir, "big.bin");
        File.WriteAllBytes(path, data);

        var h = EvidenceHasher.HashFile(path, EvidenceFileRole.Database);

        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(data)), h.Sha256);
        Assert.Equal(data.Length, h.Length);
    }

    [Fact]
    public void HashEvidenceSet_IncludesEverySidecarPresent()
    {
        CopyWalFixture();

        var hashes = EvidenceHasher.HashEvidenceSet(_dbPath);

        Assert.Equal(
            [EvidenceFileRole.Database, EvidenceFileRole.Wal, EvidenceFileRole.Shm],
            hashes.Select(h => h.Role));
    }

    // ── Session and verification ─────────────────────────────────────────────

    [Fact]
    public void EvidenceSession_Open_HashesBeforeParsing_AndVerifyMatches()
    {
        CopyWalFixture();

        using var session = EvidenceSession.Open(_dbPath);

        Assert.Equal(3, session.Hashes.Count);
        Assert.All(session.VerifyHashes(), r => Assert.Equal(HashVerificationStatus.Match, r.Status));
    }

    [Fact]
    public void EvidenceSession_VerifyHashes_ReportsChangedMissingAndAddedFiles()
    {
        CopyWalFixture();
        using var session = EvidenceSession.Open(_dbPath);

        File.AppendAllText(_dbPath + "-wal", "x");                 // changed
        File.Delete(_dbPath + "-shm");                             // missing
        File.WriteAllBytes(_dbPath + "-journal", new byte[512]);   // added

        var results = session.VerifyHashes().ToDictionary(r => r.Role, r => r.Status);

        Assert.Equal(HashVerificationStatus.Match,   results[EvidenceFileRole.Database]);
        Assert.Equal(HashVerificationStatus.Changed, results[EvidenceFileRole.Wal]);
        Assert.Equal(HashVerificationStatus.Missing, results[EvidenceFileRole.Shm]);
        Assert.Equal(HashVerificationStatus.Added,   results[EvidenceFileRole.Journal]);
    }

    [Fact]
    public void BuildShadowDatabase_WritesEvidenceTable()
    {
        CopyWalFixture();
        string shadowPath = Path.Combine(_dir, "shadow.db");

        using (var session = EvidenceSession.Open(_dbPath))
            session.BuildShadowDatabase(shadowPath);

        using var connection = new SqliteConnection($"Data Source={shadowPath};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT role, sha256, size_bytes FROM \"{ShadowDatabaseBuilder.EvidenceTableName}\" ORDER BY rowid";
        using var reader = cmd.ExecuteReader();

        var expected = EvidenceHasher.HashEvidenceSet(_dbPath);
        foreach (var h in expected)
        {
            Assert.True(reader.Read());
            Assert.Equal(h.Role, reader.GetString(0));
            Assert.Equal(h.Sha256, reader.GetString(1));
            Assert.Equal(h.Length, reader.GetInt64(2));
        }
        Assert.False(reader.Read());
    }

    // ── Project manifest ─────────────────────────────────────────────────────

    [Fact]
    public void Project_SavesHashesInManifest_AndReadsThemBack()
    {
        CopyWalFixture();
        string projectFolder = Path.Combine(_dir, "project");

        using var session = EvidenceSession.Open(_dbPath);
        var (created, _) = ShadowProject.Create(_dbPath, session.Database, projectFolder, evidenceHashes: session.Hashes);
        created.Dispose();

        using var opened = ShadowProject.Open(projectFolder);
        Assert.NotNull(opened.EvidenceHashes);
        Assert.Equal(session.Hashes, opened.EvidenceHashes!);
        Assert.All(EvidenceHasher.Compare(opened.EvidenceHashes!, session.Hashes),
            r => Assert.Equal(HashVerificationStatus.Match, r.Status));

        var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(projectFolder, "project.json")));
        Assert.Equal(ProjectManifest.CurrentVersion, json.RootElement.GetProperty("ManifestVersion").GetInt32());
    }

    [Fact]
    public void Project_ManifestFromBeforeHashing_OpensWithNoHashes()
    {
        CopyWalFixture();
        string projectFolder = Path.Combine(_dir, "old-project");

        using (var session = EvidenceSession.Open(_dbPath))
        {
            var (created, _) = ShadowProject.Create(_dbPath, session.Database, projectFolder);
            created.Dispose();
        }
        // Exactly what a version-1 project.json looked like
        File.WriteAllText(Path.Combine(projectFolder, "project.json"), $$"""
            {
              "EvidenceFilePath": {{JsonSerializer.Serialize(_dbPath)}},
              "ShadowDatabaseFileName": "shadow.db",
              "CreatedUtc": "2026-09-01T10:00:00Z"
            }
            """);

        using var opened = ShadowProject.Open(projectFolder);
        Assert.Null(opened.EvidenceHashes);
    }

    // ── Facade ───────────────────────────────────────────────────────────────

    [Fact]
    public void Facade_Recover_IncludesEvidenceHashes()
    {
        CopyWalFixture();

        var result = SqliteRecoveryFacade.Recover(_dbPath, Path.Combine(_dir, "recovered.db"));

        Assert.NotNull(result.EvidenceHashes);
        Assert.Equal(SqliteRecoveryFacade.GetHashes(_dbPath).Select(h => h.Sha256),
                     result.EvidenceHashes!.Select(h => h.Sha256));
    }
}
