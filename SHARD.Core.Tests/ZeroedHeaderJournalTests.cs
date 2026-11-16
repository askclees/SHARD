using System.Buffers.Binary;
using Microsoft.Data.Sqlite;
using SHARD.Core.Enums;
using SHARD.Core.Journal;
using SHARD.Core.Recovery;
using SHARD.Core.Session;
using SHARD.Core.Shadow;

namespace SHARD.Core.Tests;

/// <summary>
/// Opt-in reading of journal_mode=PERSIST journals, whose header SQLite zeroes when a transaction
/// commits (see <see cref="ZeroedHeaderJournalReader"/>). Every fixture is written by SQLite itself.
/// </summary>
public class ZeroedHeaderJournalTests : IDisposable
{
    private readonly string _dir;

    public ZeroedHeaderJournalTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"shard_persist_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    /// <summary>
    /// A PERSIST-mode database: 40 rows, then <paramref name="transactions"/> each in its own
    /// transaction. secure_delete is on, so the database file keeps no trace of deleted rows and
    /// anything recovered comes from the journal. Closing the connection leaves the journal with
    /// its header zeroed.
    /// </summary>
    private string CreatePersistDatabase(params string[] transactions)
    {
        string db = Path.Combine(_dir, $"persist_{Guid.NewGuid():N}.db");
        using (var c = new SqliteConnection($"Data Source={db};Pooling=False"))
        {
            c.Open();
            Exec(c, "PRAGMA journal_mode=PERSIST; PRAGMA secure_delete=ON;");
            Exec(c, "CREATE TABLE t (id INTEGER PRIMARY KEY, v TEXT);");
            Exec(c, "WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 40) " +
                    "INSERT INTO t SELECT i, 'row' || i || '-' || printf('%.*c', 60, 'x') FROM n;");
            foreach (string sql in transactions) Exec(c, sql);
        }
        Assert.True(File.Exists(db + "-journal"), "PERSIST mode should leave the journal behind");
        var journal = File.ReadAllBytes(db + "-journal");
        Assert.All(journal[..28], b => Assert.Equal(0, b)); // the premise: SQLite zeroed the header
        return db;
    }

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static int PageSize(string db)
    {
        var header = new byte[100];
        using (var fs = File.OpenRead(db)) fs.ReadExactly(header);
        int raw = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(16));
        return raw == 1 ? 65536 : raw;
    }

    // ── Opt-in ───────────────────────────────────────────────────────────────

    [Fact]
    public void FlagOff_JournalIsNotRead()
    {
        string db = CreatePersistDatabase("UPDATE t SET v = 'changed' WHERE id = 20;");

        using var session = EvidenceSession.Open(db);

        Assert.False(session.HasJournal);
        Assert.Contains("Not a valid rollback journal header", session.JournalError);
        Assert.Contains("The header is zeroed", session.JournalError); // points the user at the option
    }

    [Fact]
    public void FlagOn_ReadsRecordsWithInferredHeader()
    {
        string db = CreatePersistDatabase("UPDATE t SET v = 'changed' WHERE id = 20;");
        int pageSize = PageSize(db);
        long length = new FileInfo(db + "-journal").Length;

        using var session = EvidenceSession.Open(db, inferZeroedJournalHeader: true);

        var journal = session.Journal;
        Assert.NotNull(journal);
        Assert.True(journal!.HeaderInferred);
        Assert.Equal(pageSize, journal.Segments[0].Header.SizeOfJournalPages);
        Assert.Equal(length % (pageSize + 8), journal.Segments[0].Header.SizeOfDiskSector);
        Assert.Contains("from the journal's length", journal.Inference);

        var records = journal.Segments.SelectMany(s => s.PageRecords).ToList();
        Assert.NotEmpty(records);
        Assert.All(records, r =>
        {
            Assert.Equal(JournalRecordStatus.ZeroedHeader, r.Status);
            Assert.True(r.IsChecksumValid); // checked against the nonce inferred for its transaction
        });
        Assert.Contains(records, r => r.PageNumber == 1);
    }

    [Fact]
    public void FlagOn_ValidJournal_IsReadNormally()
    {
        // A journal with an intact header never goes near the inference code
        string db = CreatePersistDatabase();
        WriteValidJournal(db + "-journal", PageSize(db));

        using var session = EvidenceSession.Open(db, inferZeroedJournalHeader: true);

        Assert.True(session.HasJournal);
        Assert.False(session.Journal!.HeaderInferred);
    }

    // ── Recovery ─────────────────────────────────────────────────────────────

    [Fact]
    public void Recovery_RecoversWhatTheLastTransactionDeletedAndChanged()
    {
        string db = CreatePersistDatabase(
            "BEGIN; UPDATE t SET v = 'changed' WHERE id = 20; DELETE FROM t WHERE id = 25; COMMIT;");

        string withFlag = Path.Combine(_dir, "with.db"), without = Path.Combine(_dir, "without.db");
        int inserted;
        using (var session = EvidenceSession.Open(db, inferZeroedJournalHeader: true))
            (_, _, inserted) = session.BuildShadowDatabase(withFlag);
        using (var session = EvidenceSession.Open(db))
            session.BuildShadowDatabase(without);

        Assert.True(inserted >= 2);
        var recovered = Recovered(withFlag);
        Assert.Contains((20L, ShadowDatabaseBuilder.RecoveryMethodJournalPreviousVersion), recovered.Select(r => (r.Id, r.Method)));
        Assert.Contains((25L, ShadowDatabaseBuilder.RecoveryMethodJournalPage), recovered.Select(r => (r.Id, r.Method)));
        Assert.StartsWith("row20-", recovered.First(r => r.Id == 20).V);
        Assert.Empty(Recovered(without).Where(r => r.Id is 20 or 25));
    }

    [Fact]
    public void SmallerLastTransaction_LeavesAnOlderOne_GroupedSeparately()
    {
        // Grow the table over many pages (new pages aren't journaled), then a transaction that
        // modifies rows on all of them (journaling every page), then one that touches a single page.
        // PERSIST never truncates the journal, so the big transaction's later records are still
        // there after the last one's.
        string db = CreatePersistDatabase(
            "WITH RECURSIVE n(i) AS (SELECT 41 UNION ALL SELECT i + 1 FROM n WHERE i < 400) " +
            "INSERT INTO t SELECT i, printf('%.*c', 200, 'z') FROM n;",
            "UPDATE t SET v = 'a' WHERE id % 3 = 0;",
            "UPDATE t SET v = 'last' WHERE id = 5;");

        using var session = EvidenceSession.Open(db, inferZeroedJournalHeader: true);

        var journal = session.Journal!;
        Assert.True(journal.Segments.Count >= 2, $"expected the older transaction's leftovers as a second group, got {journal.Segments.Count}");
        Assert.NotEqual(journal.Segments[0].Header.Nonce, journal.Segments[1].Header.Nonce);
        Assert.All(journal.Segments.SelectMany(s => s.PageRecords), r => Assert.True(r.IsChecksumValid));
    }

    // ── What it refuses, and damaged files ───────────────────────────────────

    [Fact]
    public void NonZeroGarbageHeader_IsRefused()
    {
        string db = CreatePersistDatabase("UPDATE t SET v = 'changed' WHERE id = 20;");
        var bytes = File.ReadAllBytes(db + "-journal");
        bytes[3] = 0x7F;
        File.WriteAllBytes(db + "-journal", bytes);

        using var session = EvidenceSession.Open(db, inferZeroedJournalHeader: true);

        Assert.False(session.HasJournal);
        Assert.Contains("isn't zeroed", session.JournalError);
    }

    [Fact]
    public void ZeroedHeaderWithNoRecords_IsRefused()
    {
        string db = CreatePersistDatabase();
        File.WriteAllBytes(db + "-journal", new byte[512]);

        using var session = EvidenceSession.Open(db, inferZeroedJournalHeader: true);

        Assert.False(session.HasJournal);
        Assert.NotNull(session.JournalError);
    }

    [Fact]
    public void PartialTrailingRecord_FallsBackToFindingTheSector_AndKeepsWholeRecords()
    {
        string db = CreatePersistDatabase("UPDATE t SET v = 'changed' WHERE id = 20;");
        int pageSize = PageSize(db);
        long expectedSector = new FileInfo(db + "-journal").Length % (pageSize + 8);
        using (var fs = new FileStream(db + "-journal", FileMode.Append))
            fs.Write(new byte[100]); // length no longer gives the sector size

        using var session = EvidenceSession.Open(db, inferZeroedJournalHeader: true);

        var journal = session.Journal!;
        Assert.Contains("by finding the first valid record", journal.Inference);
        Assert.Equal(expectedSector, journal.Segments[0].Header.SizeOfDiskSector);
        Assert.Contains("less than a whole record", journal.StopReason);
        Assert.All(journal.Segments.SelectMany(s => s.PageRecords), r => Assert.True(r.IsChecksumValid));
    }

    [Fact]
    public void Facade_OptionIsOffByDefault()
    {
        string db = CreatePersistDatabase("DELETE FROM t WHERE id = 25;");

        var off = SqliteRecoveryFacade.Recover(db, Path.Combine(_dir, "off.db"));
        var on  = SqliteRecoveryFacade.Recover(db, Path.Combine(_dir, "on.db"), new RecoveryOptions(InferZeroedJournalHeader: true));

        Assert.Equal(0, off.JournalRecordsInserted);
        Assert.True(on.JournalRecordsInserted > 0);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private sealed record RecoveredRow(long Id, string V, string Method);

    private static List<RecoveredRow> Recovered(string shadow)
    {
        using var c = new SqliteConnection($"Data Source={shadow};Mode=ReadOnly;Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT id, v, \"{ShadowDatabaseBuilder.RecoveryMethodColumn}\" FROM \"{ShadowDatabaseBuilder.RecoveredTablePrefix}t\"";
        using var r = cmd.ExecuteReader();
        var rows = new List<RecoveredRow>();
        while (r.Read()) rows.Add(new RecoveredRow(r.GetInt64(0), r.IsDBNull(1) ? "" : r.GetString(1), r.GetString(2)));
        return rows;
    }

    /// <summary>A minimal journal with an intact header: one record holding page 2's current bytes.</summary>
    private static void WriteValidJournal(string path, int pageSize)
    {
        const uint nonce = 0x01020304;
        const int sector = 512;
        var header = new byte[sector];
        new byte[] { 0xd9, 0xd5, 0x05, 0xf9, 0x20, 0xa1, 0x63, 0xd7 }.CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(12), nonce);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(16), 2);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(20), sector);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(24), pageSize);
        var page = new byte[pageSize];
        page[0] = 0x0D;
        var record = new byte[pageSize + 8];
        BinaryPrimitives.WriteUInt32BigEndian(record, 2);
        page.CopyTo(record, 4);
        BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(pageSize + 4), RollbackJournalPageRecord.ComputeChecksum(page, nonce));
        File.WriteAllBytes(path, [.. header, .. record]);
    }
}
