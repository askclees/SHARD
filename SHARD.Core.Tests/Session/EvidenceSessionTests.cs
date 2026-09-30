using Microsoft.Data.Sqlite;
using SHARD.Core.Enums;
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
    public void SyncAndRecoverWal_NoWal_ReturnsZeroWithoutThrowing()
    {
        using var project = MakeProject(FixturePath("single_leaf_no_overflow.db"), out _);
        using var session = EvidenceSession.Open(FixturePath("single_leaf_no_overflow.db"));

        var (synced, recovered) = session.SyncAndRecoverWal(project);

        Assert.Equal(0, synced);
        Assert.Equal(0, recovered);
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

    // ── Helpers ──────────────────────────────────────────────────────────────

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
