using System.Buffers.Binary;
using Microsoft.Data.Sqlite;
using SHARD.Core.Enums;
using SHARD.Core.Journal;
using SHARD.Core.Recovery;
using SHARD.Core.Schema;
using SHARD.Core.Session;
using SHARD.Core.Shadow;

namespace SHARD.Core.Tests.Session;

public class EvidenceSessionTests
{
    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "TestData", name);

    private static string WalFixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "TestData", "SHARDCreated", "WAL", name);

    [Fact]
    public void Open_DetectsSiblingWalFile()
    {
        using var session = EvidenceSession.Open(WalFixturePath("places_wal_multi_page.db"));

        Assert.True(session.HasWal);
        Assert.NotNull(session.Wal);
        Assert.NotEmpty(session.Wal!.Frames);
    }

    [Fact]
    public void Open_NoSiblingWalFile_HasWalIsFalse()
    {
        using var session = EvidenceSession.Open(FixturePath("single_leaf_no_overflow.db"));

        Assert.False(session.HasWal);
        Assert.Null(session.Wal);
    }

    [Fact]
    public void BuildShadowDatabase_ProcessesWalDeletedRecords_WhenFlagOn()
    {
        string shadowPath = Path.Combine(Path.GetTempPath(), $"shard_shadow_{Guid.NewGuid():N}.db");
        try
        {
            using var session = EvidenceSession.Open(WalFixturePath("places_wal_multi_page.db"));
            var (_, walInserted) = session.BuildShadowDatabase(shadowPath);

            // Per places_wal_multi_page.xml's documented expectations: 17 historical WAL-deleted rows.
            Assert.Equal(17, walInserted);
        }
        finally
        {
            if (File.Exists(shadowPath)) File.Delete(shadowPath);
        }
    }

    [Fact]
    public void BuildShadowDatabase_SkipsWal_WhenProcessWalFlagOff()
    {
        string shadowPath = Path.Combine(Path.GetTempPath(), $"shard_shadow_{Guid.NewGuid():N}.db");
        try
        {
            using var session = EvidenceSession.Open(WalFixturePath("places_wal_multi_page.db"));
            var (_, walInserted) = session.BuildShadowDatabase(shadowPath, new RecoveryFlags(ProcessWal: false));

            Assert.Equal(0, walInserted);
        }
        finally
        {
            if (File.Exists(shadowPath)) File.Delete(shadowPath);
        }
    }

    [Fact]
    public void SyncAndRecoverWal_ProcessWalFlagOff_ReturnsZero_EvenWithARealWal()
    {
        // The GUI passes whatever WAL is currently loaded (auto-detected or manually opened via
        // "Load WAL File…") explicitly, rather than this session always using its own — so the
        // flag gate must hold regardless of which WalFile is handed in.
        string evidencePath = WalFixturePath("places_wal_multi_page.db");
        using var project = MakeProject(evidencePath, out var session);
        using (session)
        {
            var (synced, recovered) = session.SyncAndRecoverWal(project, session.Wal!, new RecoveryFlags(ProcessWal: false));

            Assert.Equal(0, synced);
            Assert.Equal(0, recovered);
        }
    }

    [Fact]
    public void SyncAndRecoverWal_RecoversDeletedRows_ForAnyGivenWalFile()
    {
        // Matches BuildShadowDatabase_ProcessesWalDeletedRecords_WhenFlagOn's expectation (17
        // historical WAL-deleted rows), but exercised through the explicit-WalFile progressive
        // path instead of the one-shot batch path.
        string evidencePath = WalFixturePath("places_wal_multi_page.db");
        using var project = MakeProject(evidencePath, out var session);
        using (session)
        {
            var (_, recovered) = session.SyncAndRecoverWal(project, session.Wal!);
            Assert.Equal(17, recovered);
        }
    }

    [Fact]
    public void RecoverDroppedTables_CarvesFreedRootPage_WhenFlagOn_NotWhenOff()
    {
        string evidencePath = Path.Combine(Path.GetTempPath(), $"shard_evidence_{Guid.NewGuid():N}.db");
        try
        {
            using (var setup = new SqliteConnection($"Data Source={evidencePath}"))
            {
                setup.Open();
                using var cmd = setup.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE gone (id INTEGER PRIMARY KEY, name TEXT);
                    INSERT INTO gone (name) VALUES ('Alice'), ('Bob'), ('Charlie');
                    DROP TABLE gone;
                    """;
                cmd.ExecuteNonQuery();
            }

            // Flag off: nothing recovered for the dropped table.
            using (var project = MakeProject(evidencePath, out var sessionOff))
            using (sessionOff)
            {
                var dropped = GetDroppedTables(sessionOff);
                Assert.NotEmpty(dropped); // sanity: the fixture actually produced a dropped-table candidate

                var processed = sessionOff.RecoverDroppedTables(project, dropped, new RecoveryFlags(DroppedTableCarving: false));
                Assert.Empty(processed);
                Assert.Equal(0, CountDeletedTableRows(project.ShadowDatabasePath, "gone"));
            }

            // Flag on (default): the dropped table's rows are carved from its freed root page.
            using (var project = MakeProject(evidencePath, out var sessionOn))
            using (sessionOn)
            {
                var dropped = GetDroppedTables(sessionOn);
                var processed = sessionOn.RecoverDroppedTables(project, dropped);
                Assert.Single(processed);
                Assert.True(CountDeletedTableRows(project.ShadowDatabasePath, "gone") > 0);
            }
        }
        finally
        {
            if (File.Exists(evidencePath)) File.Delete(evidencePath);
        }
    }

    [Fact]
    public void Dispose_ReleasesEvidenceFileHandle()
    {
        string evidencePath = Path.Combine(Path.GetTempPath(), $"shard_evidence_{Guid.NewGuid():N}.db");
        using (var setup = new SqliteConnection($"Data Source={evidencePath}"))
        {
            setup.Open();
            using var cmd = setup.CreateCommand();
            cmd.CommandText = "CREATE TABLE t (id INTEGER PRIMARY KEY)";
            cmd.ExecuteNonQuery();
        }

        try
        {
            var session = EvidenceSession.Open(evidencePath);
            session.Dispose();

            // Should not throw — the file handle was released by Dispose.
            File.Delete(evidencePath);
            Assert.False(File.Exists(evidencePath));
        }
        finally
        {
            if (File.Exists(evidencePath)) File.Delete(evidencePath);
        }
    }

    // ── Sidecar detection: WAL vs rollback journal ───────────────────────────

    [Fact]
    public void Open_DetectsSiblingJournalFile()
    {
        string dir = MakeTempDir();
        try
        {
            string db = CreateRollbackModeDatabase(dir, pageSize: 512);
            WriteJournal(db + "-journal", pageSize: 512);

            using var session = EvidenceSession.Open(db);

            Assert.True(session.HasJournal);
            Assert.False(session.HasWal);
            Assert.Null(session.JournalError);
            Assert.Null(session.IgnoredSidecarNote);
            Assert.Single(session.Journal!.Segments);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Open_JournalWithMismatchedPageSize_IsRejectedWithError()
    {
        string dir = MakeTempDir();
        try
        {
            string db = CreateRollbackModeDatabase(dir, pageSize: 512);
            WriteJournal(db + "-journal", pageSize: 1024);

            using var session = EvidenceSession.Open(db);

            Assert.False(session.HasJournal);
            Assert.Contains("page size", session.JournalError);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Open_BothSidecars_WalModeDatabase_UsesWalAndIgnoresJournal()
    {
        string dir = MakeTempDir();
        try
        {
            string db = Path.Combine(dir, "evidence.db");
            File.Copy(WalFixturePath("places_wal_multi_page.db"), db);
            File.Copy(WalFixturePath("places_wal_multi_page.db-wal"), db + "-wal");
            WriteJournal(db + "-journal", pageSize: 4096);

            using var session = EvidenceSession.Open(db);

            Assert.True(session.HasWal);
            Assert.False(session.HasJournal);
            Assert.Null(session.JournalError);
            Assert.Equal("journal file ignored (database is in WAL mode)", session.IgnoredSidecarNote);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Open_BothSidecars_RollbackModeDatabase_UsesJournalAndIgnoresWal()
    {
        string dir = MakeTempDir();
        try
        {
            string db = CreateRollbackModeDatabase(dir, pageSize: 512);
            WriteJournal(db + "-journal", pageSize: 512);
            File.WriteAllBytes(db + "-wal", new byte[64]); // never parsed — wrong mode

            using var session = EvidenceSession.Open(db);

            Assert.True(session.HasJournal);
            Assert.False(session.HasWal);
            Assert.Null(session.WalError);
            Assert.Equal("WAL file ignored (database is in rollback-journal mode)", session.IgnoredSidecarNote);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Open_UnparseableWal_ReportsWalErrorInsteadOfSilentlyDroppingIt()
    {
        string dir = MakeTempDir();
        try
        {
            string db = Path.Combine(dir, "evidence.db");
            File.Copy(WalFixturePath("places_wal_multi_page.db"), db);
            File.WriteAllBytes(db + "-wal", new byte[10]); // shorter than the 32-byte WAL header

            using var session = EvidenceSession.Open(db);

            Assert.False(session.HasWal);
            Assert.NotNull(session.WalError);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string MakeTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"shard_session_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>A small database in the default rollback-journal mode (header write version 1).</summary>
    private static string CreateRollbackModeDatabase(string dir, int pageSize)
    {
        string path = Path.Combine(dir, "evidence.db");
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            PRAGMA page_size={pageSize};
            CREATE TABLE t (id INTEGER PRIMARY KEY, name TEXT);
            INSERT INTO t (name) VALUES ('a'), ('b');
            """;
        cmd.ExecuteNonQuery();
        return path;
    }

    /// <summary>A minimal valid single-segment rollback journal holding one page-2 record.</summary>
    private static void WriteJournal(string path, int pageSize)
    {
        const uint nonce = 0x11111111;
        const int sectorSize = 512;
        var header = new byte[sectorSize];
        new byte[] { 0xd9, 0xd5, 0x05, 0xf9, 0x20, 0xa1, 0x63, 0xd7 }.CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8),  1);       // page count
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(12), nonce);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(16),  2);       // original db size in pages
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(20),  sectorSize);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(24),  pageSize);

        var pageData = new byte[pageSize];
        Array.Fill(pageData, (byte)0x2A);
        var record = new byte[pageSize + 8];
        BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(0), 2);
        pageData.CopyTo(record, 4);
        BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(pageSize + 4),
            RollbackJournalPageRecord.ComputeChecksum(pageData, nonce));

        File.WriteAllBytes(path, [.. header, .. record]);
    }

    private static ShadowProject MakeProject(string evidencePath, out EvidenceSession session)
    {
        session = EvidenceSession.Open(evidencePath);
        var (project, _) = ShadowProject.CreateTemporary(evidencePath, session.Database);
        return project;
    }

    private static IReadOnlyList<(TableSchema Schema, uint RootPage, RootPageStatus Status)> GetDroppedTables(EvidenceSession session) =>
        session.Database.ReadDeletedSqliteMaster()
            .Where(d => d.Row.RootPage.HasValue && d.Row.Sql is not null)
            .Select(d => (CreateTableParser.ExtractTableSchema(d.Row.Sql!)!, d.Row.RootPage!.Value, d.RootPageStatus))
            .Where(t => t.Item1 is not null)
            .ToList();

    private static int CountDeletedTableRows(string shadowDbPath, string table)
    {
        using var connection = new SqliteConnection($"Data Source={shadowDbPath};Mode=ReadOnly");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM \"{ShadowDatabaseBuilder.DeletedTablePrefix}{table}\"";
        try
        {
            return (int)(long)cmd.ExecuteScalar()!;
        }
        catch (SqliteException)
        {
            return 0; // table was never created — nothing was recovered for it
        }
    }
}
