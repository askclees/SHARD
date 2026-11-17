using System.Buffers.Binary;
using Microsoft.Data.Sqlite;
using SHARD.Core.Journal;
using SHARD.Core.Recovery;
using SHARD.Core.Session;
using SHARD.Core.Shadow;

namespace SHARD.Core.Tests;

/// <summary>
/// Recovery of deleted and previous-version records from a rollback journal's page images.
/// Each fixture mimics a hot journal: the table's leaf page is captured before a transaction,
/// the transaction deletes and updates rows with secure_delete on (so the database file itself
/// keeps no trace of them), and the captured page is written out as that page's journal record.
/// The batch path rolls such a journal back (see JournalParityTests); the project tests here
/// build from the file alone, so the journal's rows are recovered as deleted and earlier versions.
/// </summary>
public class JournalRecoveryTests : IDisposable
{
    private const int PageSize = 4096;
    private const uint Nonce = 0x5EED1234;

    private readonly string _dir;
    private readonly string _dbPath;

    public JournalRecoveryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"shard_journal_recovery_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "evidence.db");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    [Fact]
    public void BuildShadowDatabase_HotJournal_IsRolledBack_AndTheFilesVersionRecovered()
    {
        // The journal's record is valid, so it's hot: SQLite would roll it back, restoring the
        // page's earlier image. The live table shows that; the file's uncommitted rename is kept.
        BuildEvidenceWithJournal();
        string shadowPath = Path.Combine(_dir, "shadow.db");

        using var session = EvidenceSession.Open(_dbPath);
        Assert.True(session.HasJournal);
        Assert.True(session.JournalIsHot);

        var (_, walInserted, journalInserted) = session.BuildShadowDatabase(shadowPath);

        Assert.Equal(0, walInserted);
        Assert.Equal(1, journalInserted);
        Assert.Equal(["1|Alice", "2|Bob", "3|Charlie", "4|Dave", "5|Eve"], LiveRows(shadowPath));
        Assert.Empty(ReadRecovered(shadowPath)); // the journal's rows are live
        Assert.Equal((3L, "Charles", ShadowDatabaseBuilder.RecoveryMethodDbFileUncommittedVersion), ReadRecoveredFromFile(shadowPath).Single());
    }

    [Fact]
    public void BuildShadowDatabase_ProcessJournalOff_LiveTableIsTheFile()
    {
        BuildEvidenceWithJournal();
        string shadowPath = Path.Combine(_dir, "shadow.db");

        using var session = EvidenceSession.Open(_dbPath);
        session.BuildShadowDatabase(shadowPath, new RecoveryFlags(ProcessJournal: false));

        Assert.Equal(["1|Alice", "3|Charles", "5|Eve"], LiveRows(shadowPath));
    }

    [Fact]
    public void BuildShadowDatabase_ProcessJournalFlagOff_SkipsJournal()
    {
        BuildEvidenceWithJournal();
        string shadowPath = Path.Combine(_dir, "shadow.db");

        using var session = EvidenceSession.Open(_dbPath);
        var (_, _, journalInserted) = session.BuildShadowDatabase(shadowPath, new RecoveryFlags(ProcessJournal: false));

        Assert.Equal(0, journalInserted);
        Assert.Empty(ReadRecovered(shadowPath));
    }

    [Fact]
    public void RecoverJournal_RunTwiceAgainstSameProject_DoesNotDuplicateRows()
    {
        // Re-opening a saved project re-runs journal recovery against a shadow database that
        // already holds the journal's rows.
        BuildEvidenceWithJournal();

        using var session = EvidenceSession.Open(_dbPath);
        var (project, _) = ShadowProject.CreateTemporary(_dbPath, session.Database);
        using (project)
        {
            var first  = session.RecoverJournal(project, session.Journal!);
            var second = session.RecoverJournal(project, session.Journal!);

            Assert.Equal((3, (string?)null), first);
            Assert.Equal((0, (string?)null), second);
            Assert.Equal(3, ReadRecovered(project.ShadowDatabasePath).Count);
        }
    }

    [Fact]
    public void RecoverJournal_StaleRecordWithBadChecksum_IsStillUsed()
    {
        // A record whose checksum doesn't match the header nonce is left over from an earlier
        // transaction. SQLite wouldn't play it back, but it is still a real earlier page image.
        BuildEvidenceWithJournal(corruptChecksum: true);

        using var session = EvidenceSession.Open(_dbPath);
        Assert.Equal(Enums.JournalRecordStatus.Stale,
            session.Journal!.Segments[0].PageRecords[0].Status);

        var (project, _) = ShadowProject.CreateTemporary(_dbPath, session.Database);
        using (project)
        {
            Assert.Equal((3, (string?)null), session.RecoverJournal(project, session.Journal!));
        }
    }

    [Fact]
    public void BuildShadowDatabase_StaleRecordOnly_JournalIsNotRolledBack()
    {
        // SQLite stops playback at a record whose checksum doesn't match, so with only that record
        // nothing is restored: the live table is the file, and the record's rows are recovered.
        BuildEvidenceWithJournal(corruptChecksum: true);
        string shadowPath = Path.Combine(_dir, "shadow.db");

        using var session = EvidenceSession.Open(_dbPath);
        Assert.False(session.JournalIsHot);
        Assert.Null(session.CommittedDatabase);
        var (_, _, journalInserted) = session.BuildShadowDatabase(shadowPath);

        Assert.Equal(["1|Alice", "3|Charles", "5|Eve"], LiveRows(shadowPath));
        Assert.Equal(3, journalInserted);
        Assert.Equal(3, ReadRecovered(shadowPath).Count);
    }

    [Fact]
    public void Facade_Recover_ReportsJournalRecordsInserted()
    {
        BuildEvidenceWithJournal();
        string outputPath = Path.Combine(_dir, "recovered.db");

        var result = SqliteRecoveryFacade.Recover(_dbPath, outputPath);

        Assert.Equal(1, result.JournalRecordsInserted); // the file's uncommitted version of row 3
        Assert.Equal(5, result.Tables.Single(t => t.TableName == "people").LiveRowCount);
    }

    // ── Fixture ──────────────────────────────────────────────────────────────

    private void BuildEvidenceWithJournal(bool corruptChecksum = false)
    {
        Execute($"""
            PRAGMA page_size={PageSize};
            PRAGMA secure_delete=ON;
            CREATE TABLE people (id INTEGER PRIMARY KEY, name TEXT);
            INSERT INTO people (id, name) VALUES (1, 'Alice'), (2, 'Bob'), (3, 'Charlie'), (4, 'Dave'), (5, 'Eve');
            """);

        uint rootPage = (uint)(long)Scalar("SELECT rootpage FROM sqlite_master WHERE name = 'people'")!;
        byte[] beforeImage = ReadPage(rootPage);

        Execute("""
            PRAGMA secure_delete=ON;
            DELETE FROM people WHERE id IN (2, 4);
            UPDATE people SET name = 'Charles' WHERE id = 3;
            """);

        WriteJournal(_dbPath + "-journal", rootPage, beforeImage, corruptChecksum);
    }

    private void Execute(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={_dbPath};Pooling=False");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private object? Scalar(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={_dbPath};Pooling=False");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    private byte[] ReadPage(uint pageNumber)
    {
        using var stream = File.OpenRead(_dbPath);
        var page = new byte[PageSize];
        stream.Seek((long)(pageNumber - 1) * PageSize, SeekOrigin.Begin);
        stream.ReadExactly(page);
        return page;
    }

    private static void WriteJournal(string path, uint pageNumber, byte[] pageData, bool corruptChecksum)
    {
        const int sectorSize = 512;
        var header = new byte[sectorSize];
        new byte[] { 0xd9, 0xd5, 0x05, 0xf9, 0x20, 0xa1, 0x63, 0xd7 }.CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8),  1);       // page count
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(12), Nonce);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(16),  2);       // original db size in pages
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(20),  sectorSize);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(24),  PageSize);

        uint checksum = RollbackJournalPageRecord.ComputeChecksum(pageData, Nonce);
        if (corruptChecksum) checksum ^= 0xFFFFFFFF;

        var record = new byte[PageSize + 8];
        BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(0), pageNumber);
        pageData.CopyTo(record, 4);
        BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(PageSize + 4), checksum);

        File.WriteAllBytes(path, [.. header, .. record]);
    }

    private static List<string> LiveRows(string shadowPath)
    {
        using var connection = new SqliteConnection($"Data Source={shadowPath};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, name FROM people ORDER BY id";
        using var reader = cmd.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read()) rows.Add($"{reader.GetInt64(0)}|{reader.GetString(1)}");
        return rows;
    }

    private static List<(long Id, string Name, string Method)> ReadRecoveredFromFile(string shadowPath)
    {
        using var connection = new SqliteConnection($"Data Source={shadowPath};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, name, "{ShadowDatabaseBuilder.RecoveryMethodColumn}"
            FROM "{ShadowDatabaseBuilder.RecoveredTablePrefix}people"
            WHERE "{ShadowDatabaseBuilder.RecoveryMethodColumn}" IN
                ('{ShadowDatabaseBuilder.RecoveryMethodDbFileUncommitted}', '{ShadowDatabaseBuilder.RecoveryMethodDbFileUncommittedVersion}')
            """;
        using var reader = cmd.ExecuteReader();
        var rows = new List<(long, string, string)>();
        while (reader.Read()) rows.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2)));
        return rows;
    }

    /// <summary>Recovered rows attributed to the journal. In-tree recovery also adds placeholder
    /// rows (rowid -1, no data) for the cells secure_delete zeroed; those aren't counted here.</summary>
    private static Dictionary<long, (string Name, string Method)> ReadRecovered(string shadowPath)
    {
        var rows = new Dictionary<long, (string, string)>();
        using var connection = new SqliteConnection($"Data Source={shadowPath};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, name, "{ShadowDatabaseBuilder.RecoveryMethodColumn}"
            FROM "{ShadowDatabaseBuilder.RecoveredTablePrefix}people"
            WHERE "{ShadowDatabaseBuilder.RecoveryMethodColumn}" IN
                ('{ShadowDatabaseBuilder.RecoveryMethodJournalPage}', '{ShadowDatabaseBuilder.RecoveryMethodJournalPreviousVersion}')
            """;
        try
        {
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                rows[reader.GetInt64(0)] = (reader.GetString(1), reader.GetString(2));
        }
        catch (SqliteException)
        {
            // recovered table never created — nothing was recovered
        }
        return rows;
    }
}
