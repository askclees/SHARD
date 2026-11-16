using Microsoft.Data.Sqlite;
using SHARD.Core.Recovery;
using SHARD.Core.Session;
using SHARD.Core.Shadow;

namespace SHARD.Core.Tests;

/// <summary>
/// _db_state / _log_state / _is_current on live and recovered shadow rows (see <see cref="RowStates"/>).
/// Each scenario is a real WAL written by SQLite, built through EvidenceSession.BuildShadowDatabase.
/// </summary>
public class RowStatesTests : IDisposable
{
    private readonly string _dir;

    public RowStatesTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"shard_states_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    /// <summary>One shadow row: which table it's in, its id and url, and its three states.</summary>
    private sealed record Row(string Table, long Id, string? Url, long Db, long? Log, long Current);

    // ── Scenarios ────────────────────────────────────────────────────────────

    [Fact]
    public void DeleteUpdateInsert_InTheWal()
    {
        // File: 1, 2, 3, 5. WAL: delete 2, update 3, insert 4 (secure_delete on).
        string db = CreateWal(
            setup: "INSERT INTO t VALUES (1,'one'),(2,'two'),(3,'three'),(5,'five');",
            wal:   "DELETE FROM t WHERE id = 2; UPDATE t SET url = 'three-changed' WHERE id = 3; INSERT INTO t VALUES (4,'four');",
            secureDelete: true);

        var rows = States(Build(db)).Where(r => r.Url is not null).ToList();

        Assert.Equal(
        [
            new Row("live",      1, "one",           1, 1, 1),
            new Row("live",      3, "three-changed", 3, 1, 1), // the file has rowid 3 with other values
            new Row("live",      4, "four",          0, 1, 1), // added by the WAL
            new Row("live",      5, "five",          1, 1, 1),
            new Row("recovered", 2, "two",           1, 2, 0), // live in the file, deleted by the WAL, no bytes left in it
            new Row("recovered", 3, "three",         1, 3, 0), // the file's version of the updated row — listed once
        ], rows);
    }

    [Fact]
    public void FreeblocksFixture()
    {
        string db = CopyFixture("places_wal_freeblocks.db");

        var rows = States(Build(db), "moz_places", urlPrefix: "https://example").ToList();

        Assert.Contains(new Row("recovered",  2, "https://example2.com/",  1, 2, 0), rows); // live in the file, deleted by the WAL
        Assert.Contains(new Row("recovered", 30, "https://example30.com/", 0, 2, 0), rows); // inserted and deleted within the WAL
        Assert.Contains(new Row("recovered", -1, "https://example50.com/", 0, 2, 0), rows); // only ever freed space in frames
        Assert.Contains(new Row("live",      51, "https://example51.com/", 0, 1, 1), rows); // added by the WAL
        Assert.Contains(new Row("live",       1, "https://example1.com/",  1, 1, 1), rows);
    }

    [Fact]
    public void PageTheWalNeverTouched_LogStateIsNull()
    {
        // Enough rows for several leaf pages; the WAL changes only the last row, so rows on the
        // first leaf page appear in no frame and their page was never rewritten.
        string db = CreateWal(
            setup: "WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 300) " +
                   "INSERT INTO t SELECT i, printf('%.*c', 100, 'x') FROM n;",
            wal:   "UPDATE t SET url = 'changed' WHERE id = 300;",
            secureDelete: true);

        var rows = States(Build(db));

        var first = rows.Single(r => r.Table == "live" && r.Id == 1);
        Assert.Equal((1L, (long?)null, 1L), (first.Db, first.Log, first.Current));
        var changed = rows.Single(r => r.Table == "live" && r.Id == 300);
        Assert.Equal((3L, (long?)1, 1L), (changed.Db, changed.Log, changed.Current));
    }

    [Fact]
    public void DeletedInTheFile_AndOverwrittenByTheWal_IsDb2Log0()
    {
        // secure_delete off. Row 2 is deleted before the checkpoint, so the file holds it as a
        // freeblock. Then in the WAL a row exactly the same size reuses that freeblock: the WAL
        // rewrote the page, and row 2 isn't in it.
        string db = CreateWal(
            setup: "INSERT INTO t VALUES (1,'one-one-one'),(2,'two-two-two'),(3,'three-three'); DELETE FROM t WHERE id = 2;",
            wal:   "INSERT INTO t VALUES (9,'nine-nine-n');",
            secureDelete: false);

        var row2 = Assert.Single(States(Build(db)), r => r.Url == "two-two-two");
        Assert.Equal((2L, (long?)0, 0L), (row2.Db, row2.Log, row2.Current));
    }

    // ── No WAL, WAL off, re-running ──────────────────────────────────────────

    [Fact]
    public void NoWal_LogStateNullEverywhere()
    {
        string db = Path.Combine(_dir, "plain.db");
        using (var c = new SqliteConnection($"Data Source={db};Pooling=False"))
        {
            c.Open();
            Exec(c, "CREATE TABLE t (id INTEGER PRIMARY KEY, url TEXT); INSERT INTO t VALUES (1,'one'),(2,'two');");
        }

        string shadow = Build(db);

        Assert.All(States(shadow), r => Assert.Null(r.Log));
        Assert.All(States(shadow).Where(r => r.Table == "live"), r => Assert.Equal((1L, 1L), (r.Db, r.Current)));
        Assert.Equal("none", Info(shadow));
    }

    [Fact]
    public void ProcessWalOff_DescribesTheFileAlone()
    {
        string db = CreateWal(
            setup: "INSERT INTO t VALUES (1,'one'),(2,'two');",
            wal:   "DELETE FROM t WHERE id = 2;",
            secureDelete: true);

        string shadow = Build(db, new RecoveryFlags(ProcessWal: false));

        var rows = States(shadow);
        Assert.All(rows, r => Assert.Null(r.Log));
        Assert.Contains(new Row("live", 2, "two", 1, null, 1), rows); // current, as far as the file alone goes
        Assert.Equal("none", Info(shadow));
    }

    [Fact]
    public void UpdatingAgain_GivesTheSameStates_AndOldShadowsGetTheColumns()
    {
        string db = CreateWal(
            setup: "INSERT INTO t VALUES (1,'one'),(2,'two');",
            wal:   "DELETE FROM t WHERE id = 2;",
            secureDelete: true);
        string shadow = Build(db);
        var before = States(shadow);

        // As if built before the state columns existed
        using (var c = new SqliteConnection($"Data Source={shadow};Pooling=False"))
        {
            c.Open();
            foreach (string table in new[] { "t", $"{ShadowDatabaseBuilder.RecoveredTablePrefix}t" })
                foreach (string col in new[] { RowStates.DbStateColumn, RowStates.LogStateColumn, RowStates.IsCurrentColumn })
                    Exec(c, $"ALTER TABLE \"{table}\" DROP COLUMN \"{col}\"");
        }

        using (var session = EvidenceSession.Open(db))
            session.UpdateRowStates(shadow);

        Assert.Equal(before, States(shadow));
        Assert.Equal("wal", Info(shadow));
    }

    [Fact]
    public void Facade_OutputHasStates()
    {
        string db = CreateWal(
            setup: "INSERT INTO t VALUES (1,'one'),(2,'two');",
            wal:   "DELETE FROM t WHERE id = 2;",
            secureDelete: true);
        string output = Path.Combine(_dir, "recovered.db");

        SqliteRecoveryFacade.Recover(db, output, new RecoveryOptions(CarveMode: CarveMode.Loose));

        Assert.Contains(new Row("recovered", 2, "two", 1, 2, 0), States(output));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private string CreateWal(string setup, string wal, bool secureDelete)
    {
        string live = Path.Combine(_dir, $"live_{Guid.NewGuid():N}.db");
        using var c = new SqliteConnection($"Data Source={live};Pooling=False");
        c.Open();
        Exec(c, $"PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; PRAGMA secure_delete={(secureDelete ? "ON" : "OFF")};");
        Exec(c, "CREATE TABLE t (id INTEGER PRIMARY KEY, url TEXT);");
        Exec(c, setup);
        Exec(c, "PRAGMA wal_checkpoint(TRUNCATE);");
        Exec(c, wal);

        string copy = Path.Combine(_dir, $"evidence_{Guid.NewGuid():N}.db");
        foreach (string suffix in new[] { "", "-wal" })
        {
            using var src = new FileStream(live + suffix, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var dst = File.Create(copy + suffix);
            src.CopyTo(dst);
        }
        return copy;
    }

    private string CopyFixture(string name)
    {
        string src = Path.Combine(AppContext.BaseDirectory, "TestData", "SHARDCreated", "WAL", name);
        string dst = Path.Combine(_dir, name);
        foreach (string suffix in new[] { "", "-wal", "-shm" })
            if (File.Exists(src + suffix)) File.Copy(src + suffix, dst + suffix);
        return dst;
    }

    private string Build(string db, RecoveryFlags? flags = null)
    {
        string shadow = Path.Combine(_dir, $"shadow_{Guid.NewGuid():N}.db");
        using var session = EvidenceSession.Open(db);
        session.BuildShadowDatabase(shadow, flags);
        return shadow;
    }

    private static List<Row> States(string shadow, string table = "t", string? urlPrefix = null)
    {
        var rows = new List<Row>();
        using var c = new SqliteConnection($"Data Source={shadow};Mode=ReadOnly;Pooling=False");
        c.Open();
        foreach (var (name, label) in new[] { (table, "live"), (ShadowDatabaseBuilder.RecoveredTablePrefix + table, "recovered") })
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"""
                SELECT id, url, "{RowStates.DbStateColumn}", "{RowStates.LogStateColumn}", "{RowStates.IsCurrentColumn}"
                FROM "{name}" ORDER BY id, url
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                string? url = r.IsDBNull(1) ? null : r.GetString(1);
                if (urlPrefix is not null && url?.StartsWith(urlPrefix) != true) continue;
                rows.Add(new Row(label, r.GetInt64(0), url, r.GetInt64(2), r.IsDBNull(3) ? null : r.GetInt64(3), r.GetInt64(4)));
            }
        }
        return rows;
    }

    private static string? Info(string shadow)
    {
        using var c = new SqliteConnection($"Data Source={shadow};Mode=ReadOnly;Pooling=False");
        c.Open();
        return ShadowDatabaseBuilder.ReadInfo(c, RowStates.InfoKeyTransactionLog);
    }

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
