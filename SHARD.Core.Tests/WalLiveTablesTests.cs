using Microsoft.Data.Sqlite;
using SHARD.Core.Recovery;
using SHARD.Core.Session;
using SHARD.Core.Shadow;

namespace SHARD.Core.Tests;

/// <summary>
/// With a WAL, shadow live tables hold what SQLite returns (the file with committed frames
/// applied), and rows the WAL deleted or changed are kept in _shard_recovered_* from the database
/// file's own version of the page. Scenario: the file holds rows 1-3; the WAL deletes row 2,
/// updates row 3 and inserts row 4.
/// </summary>
public class WalLiveTablesTests : IDisposable
{
    private readonly string _dir;
    private readonly string _db;

    public WalLiveTablesTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"shard_wallive_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _db = CreateEvidence();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private string CreateEvidence()
    {
        string live = Path.Combine(_dir, "live.db");
        using var c = new SqliteConnection($"Data Source={live};Pooling=False");
        c.Open();
        Exec(c, """
            PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0;
            CREATE TABLE t (id INTEGER PRIMARY KEY, v TEXT);
            INSERT INTO t VALUES (1, 'row1'), (2, 'row2'), (3, 'row3');
            PRAGMA wal_checkpoint(TRUNCATE);
            DELETE FROM t WHERE id = 2;
            UPDATE t SET v = 'changed' WHERE id = 3;
            INSERT INTO t VALUES (4, 'row4');
            """);
        string evidence = Path.Combine(_dir, "evidence.db");
        CopyShared(live, evidence);
        CopyShared(live + "-wal", evidence + "-wal");
        return evidence;
    }

    // ── Batch path (CLI / facade / Python) ───────────────────────────────────

    [Fact]
    public void BuildShadowDatabase_LiveTablesMatchSqlite_AndWalChangesAreRecovered()
    {
        string shadow = Path.Combine(_dir, "shadow.db");
        using (var session = EvidenceSession.Open(_db))
            session.BuildShadowDatabase(shadow);

        using var c = Open(shadow);
        Assert.Equal(["1|row1", "3|changed", "4|row4"], Rows(c, "SELECT id, v FROM t ORDER BY id"));
        Assert.Equal(SqliteView(), Rows(c, "SELECT id, v FROM t ORDER BY id"));
        AssertWalChangesRecovered(c);
        Assert.Equal(ShadowDatabaseBuilder.LiveSourceCommittedWal, Info(c));
    }

    [Fact]
    public void Facade_Recover_CountsLiveRowsFromCommittedState()
    {
        var result = SqliteRecoveryFacade.Recover(_db, Path.Combine(_dir, "recovered.db"));

        Assert.Equal(3, result.Tables.Single(t => t.TableName == "t").LiveRowCount);
        using var c = Open(Path.Combine(_dir, "recovered.db"));
        Assert.Equal(SqliteView(), Rows(c, "SELECT id, v FROM t ORDER BY id"));
    }

    [Fact]
    public void ProcessWalOff_LiveTablesAreTheFileAlone()
    {
        string shadow = Path.Combine(_dir, "shadow.db");
        using (var session = EvidenceSession.Open(_db))
            session.BuildShadowDatabase(shadow, new RecoveryFlags(ProcessWal: false));

        using var c = Open(shadow);
        Assert.Equal(["1|row1", "2|row2", "3|row3"], Rows(c, "SELECT id, v FROM t ORDER BY id"));
        Assert.Equal(ShadowDatabaseBuilder.LiveSourceDatabaseFile, Info(c));
    }

    [Fact]
    public void PageLayout_DescribesTheDatabaseFile()
    {
        string shadow = Path.Combine(_dir, "shadow.db");
        using (var session = EvidenceSession.Open(_db))
            session.BuildShadowDatabase(shadow);

        long filePages = new FileInfo(_db).Length / 4096;
        using var c = Open(shadow);
        Assert.Equal([filePages.ToString()], Rows(c, "SELECT count(*) FROM _shard_pages"));
    }

    // ── GUI path (project, then sync) ────────────────────────────────────────

    [Fact]
    public void ProjectFromCommittedState_SyncAddsNothing_AndRecoversWalChanges()
    {
        using var session = EvidenceSession.Open(_db);
        var (project, _) = ShadowProject.CreateTemporary(_db, session.ShadowSource(), layoutDatabase: session.Database);
        using (project)
        {
            Assert.True(project.LiveTablesIncludeWal);
            var result = session.SyncAndRecoverWal(project, session.Wal!);
            Assert.Equal(0, result.Synced);
            Assert.Null(result.RecoveryError);

            // Running it again (as re-opening a saved project does) adds nothing
            Assert.Equal(0, session.SyncAndRecoverWal(project, session.Wal!).Recovered);

            using var c = Open(project.ShadowDatabasePath);
            Assert.Equal(SqliteView(), Rows(c, "SELECT id, v FROM t ORDER BY id"));
            AssertWalChangesRecovered(c);
        }
    }

    [Fact]
    public void ProjectFromFileAlone_StillSyncsCommittedRows()
    {
        using var session = EvidenceSession.Open(_db);
        var (project, _) = ShadowProject.CreateTemporary(_db, session.Database);
        using (project)
        {
            Assert.False(project.LiveTablesIncludeWal);
            var result = session.SyncAndRecoverWal(project, session.Wal!);
            Assert.True(result.Synced > 0);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static void AssertWalChangesRecovered(SqliteConnection c)
    {
        var recovered = Rows(c, $"""
            SELECT id, v, "{ShadowDatabaseBuilder.RecoveryMethodColumn}" FROM "{ShadowDatabaseBuilder.RecoveredTablePrefix}t"
            WHERE "{ShadowDatabaseBuilder.RecoveryMethodColumn}" IN
              ('{ShadowDatabaseBuilder.RecoveryMethodDbFileDeletedInWal}', '{ShadowDatabaseBuilder.RecoveryMethodDbFilePreviousVersion}')
            ORDER BY id
            """);
        Assert.Equal(
            [$"2|row2|{ShadowDatabaseBuilder.RecoveryMethodDbFileDeletedInWal}",
             $"3|row3|{ShadowDatabaseBuilder.RecoveryMethodDbFilePreviousVersion}"],
            recovered);
    }

    /// <summary>The rows SQLite itself returns, read from a separate copy.</summary>
    private List<string> SqliteView()
    {
        string copy = Path.Combine(_dir, $"sqlite_{Guid.NewGuid():N}.db");
        File.Copy(_db, copy);
        File.Copy(_db + "-wal", copy + "-wal");
        using var c = new SqliteConnection($"Data Source={copy};Pooling=False");
        c.Open();
        return Rows(c, "SELECT id, v FROM t ORDER BY id");
    }

    private static string? Info(SqliteConnection c) =>
        ShadowDatabaseBuilder.ReadInfo(c, ShadowDatabaseBuilder.InfoKeyLiveTablesSource);

    private static SqliteConnection Open(string path)
    {
        var c = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        c.Open();
        return c;
    }

    private static List<string> Rows(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        var rows = new List<string>();
        while (r.Read())
            rows.Add(string.Join("|", Enumerable.Range(0, r.FieldCount).Select(i => r.IsDBNull(i) ? "NULL" : r.GetValue(i).ToString())));
        return rows;
    }

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static void CopyShared(string from, string to)
    {
        using var src = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var dst = File.Create(to);
        src.CopyTo(dst);
    }
}
