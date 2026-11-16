using System.Buffers.Binary;
using Microsoft.Data.Sqlite;
using SHARD.Core.Enums;
using SHARD.Core.WAL;

namespace SHARD.Core.Tests;

/// <summary>
/// <see cref="SqliteForensicDatabase.OpenCommittedState"/> must read exactly what SQLite itself
/// returns for a database with its WAL. Each scenario builds a real WAL with SQLite, then compares
/// every row of every table as SHARD reads it against SQLite reading a separate copy.
/// </summary>
public class WalCommittedStateTests : IDisposable
{
    private readonly string _dir;

    public WalCommittedStateTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"shard_walstate_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    public static TheoryData<string, string, string, int> Scenarios => new()
    {
        {
            "inserts",
            "CREATE TABLE t (id INTEGER PRIMARY KEY, v TEXT); INSERT INTO t VALUES (1,'a'),(2,'b');",
            "INSERT INTO t VALUES (3,'c'); INSERT INTO t VALUES (4,'d');",
            0
        },
        {
            "deletes",
            "CREATE TABLE t (id INTEGER PRIMARY KEY, v TEXT); INSERT INTO t VALUES (1,'a'),(2,'b'),(3,'c'),(4,'d');",
            "DELETE FROM t WHERE id = 2; DELETE FROM t WHERE id = 4;",
            0
        },
        {
            "updates",
            "CREATE TABLE t (id INTEGER PRIMARY KEY, v TEXT); INSERT INTO t VALUES (1,'a'),(2,'b'),(3,'c');",
            "UPDATE t SET v = 'changed' WHERE id = 2; UPDATE t SET v = 'again' WHERE id = 2;",
            0
        },
        {
            // Hundreds of rows split the leaf page, so the WAL holds pages beyond the end of the file
            "page splits and overflow",
            "CREATE TABLE t (id INTEGER PRIMARY KEY, v TEXT); INSERT INTO t VALUES (1,'a');",
            "WITH RECURSIVE n(i) AS (SELECT 2 UNION ALL SELECT i + 1 FROM n WHERE i < 400) " +
            "INSERT INTO t SELECT i, printf('%.*c', 120, 'x') FROM n; " +
            "INSERT INTO t VALUES (9999, printf('%.*c', 9000, 'y'));",
            1
        },
        {
            // Changes the schema itself, so page 1 (and the header) come from the WAL
            "schema changes",
            "CREATE TABLE t (id INTEGER PRIMARY KEY, v TEXT); INSERT INTO t VALUES (1,'a');" +
            "CREATE TABLE gone (id INTEGER PRIMARY KEY, v TEXT); INSERT INTO gone VALUES (1,'x');",
            "CREATE TABLE added (id INTEGER PRIMARY KEY, v TEXT); INSERT INTO added VALUES (7,'new'); " +
            "DROP TABLE gone; INSERT INTO t VALUES (2,'b');",
            0
        },
        {
            // With auto-vacuum the last commit shrinks the database below the file's size
            "database shrinks",
            "PRAGMA auto_vacuum = FULL; CREATE TABLE t (id INTEGER PRIMARY KEY, v TEXT); " +
            "WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 300) " +
            "INSERT INTO t SELECT i, printf('%.*c', 200, 'z') FROM n;",
            "DELETE FROM t WHERE id > 5;",
            -1
        },
    };

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void CommittedState_MatchesSqlite(string scenario, string setupSql, string walSql, int pageCountChange)
    {
        string db = CreateWalDatabase(setupSql, walSql);
        AssertMatchesSqlite(db, scenario);

        // Check the scenario exercises what it claims
        var wal = new WalFile(db + "-wal", TextEncoding.Utf8, 0);
        using var committed = SqliteForensicDatabase.OpenCommittedState(db, wal);
        uint filePages = (uint)(new FileInfo(db).Length / committed.Header.PageSize);
        Assert.NotEmpty(committed.WalPageNumbers);
        if (pageCountChange > 0) Assert.True(committed.PageCount > filePages, $"{scenario}: expected the WAL to grow the database past the file ({committed.PageCount} vs {filePages})");
        if (pageCountChange < 0) Assert.True(committed.PageCount < filePages, $"{scenario}: expected the last commit to shrink the database ({committed.PageCount} vs {filePages})");
    }

    [Fact]
    public void CommittedState_DamagedFrame_MatchesSqliteStoppingThere()
    {
        string db = CreateWalDatabase(
            "CREATE TABLE t (id INTEGER PRIMARY KEY, v TEXT); INSERT INTO t VALUES (1,'a');",
            "INSERT INTO t VALUES (2,'b'); INSERT INTO t VALUES (3,'c'); INSERT INTO t VALUES (4,'d');");

        // Damage frame 2 (row 3's commit): SQLite and SHARD should both stop there and see rows 1-2
        var bytes = File.ReadAllBytes(db + "-wal");
        int pageSize = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(8));
        bytes[32 + 1 * (24 + pageSize) + 24 + 100] ^= 0xFF;
        File.WriteAllBytes(db + "-wal", bytes);

        var rows = AssertMatchesSqlite(db, "damaged frame");
        Assert.Equal(["1|a", "2|b"], rows["t"]);
    }

    [Fact]
    public void FileAlone_DiffersFromCommittedState()
    {
        // Guards the comparison itself: the file without the WAL must not already match.
        string db = CreateWalDatabase(
            "CREATE TABLE t (id INTEGER PRIMARY KEY, v TEXT); INSERT INTO t VALUES (1,'a');",
            "INSERT INTO t VALUES (2,'b');");

        var wal = new WalFile(db + "-wal", TextEncoding.Utf8, 0);
        using var fileOnly  = SqliteForensicDatabase.Open(db);
        using var committed = SqliteForensicDatabase.OpenCommittedState(db, wal);

        Assert.False(fileOnly.IncludesWal);
        Assert.True(committed.IncludesWal);
        Assert.Equal(["1|a"],        ReadWithShard(fileOnly)["t"]);
        Assert.Equal(["1|a", "2|b"], ReadWithShard(committed)["t"]);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>Runs <paramref name="setupSql"/> and checkpoints it into the file, then runs
    /// <paramref name="walSql"/> (each statement its own transaction) and copies the database and
    /// WAL out while the connection is open — closing it would checkpoint and delete the WAL.</summary>
    private string CreateWalDatabase(string setupSql, string walSql)
    {
        string live = Path.Combine(_dir, $"live_{Guid.NewGuid():N}.db");
        using var c = new SqliteConnection($"Data Source={live};Pooling=False");
        c.Open();
        Exec(c, setupSql); // runs before journal_mode=WAL, so a leading PRAGMA auto_vacuum still takes effect
        Exec(c, "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; PRAGMA wal_checkpoint(TRUNCATE);");
        Exec(c, walSql);

        string copy = Path.Combine(_dir, $"evidence_{Guid.NewGuid():N}.db");
        CopyShared(live, copy);
        CopyShared(live + "-wal", copy + "-wal");
        return copy;
    }

    /// <summary>Compares every table's rows as SHARD reads the committed state against SQLite
    /// reading its own copy, and returns SHARD's rows.</summary>
    private Dictionary<string, List<string>> AssertMatchesSqlite(string db, string scenario)
    {
        string sqliteCopy = Path.Combine(_dir, $"sqlite_{Guid.NewGuid():N}.db");
        File.Copy(db, sqliteCopy);
        File.Copy(db + "-wal", sqliteCopy + "-wal");
        var expected = ReadWithSqlite(sqliteCopy);

        var wal = new WalFile(db + "-wal", TextEncoding.Utf8, 0);
        using var committed = SqliteForensicDatabase.OpenCommittedState(db, wal);
        var actual = ReadWithShard(committed);

        Assert.True(expected.Keys.Order().SequenceEqual(actual.Keys.Order()),
            $"{scenario}: tables differ — SQLite [{string.Join(", ", expected.Keys.Order())}], SHARD [{string.Join(", ", actual.Keys.Order())}]");
        foreach (var (table, rows) in expected)
            Assert.True(rows.SequenceEqual(actual[table]),
                $"{scenario}: table '{table}' differs — SQLite {rows.Count} rows, SHARD {actual[table].Count} rows; " +
                $"first difference: SQLite '{rows.Except(actual[table]).FirstOrDefault()}', SHARD '{actual[table].Except(rows).FirstOrDefault()}'");
        return actual;
    }

    private static Dictionary<string, List<string>> ReadWithSqlite(string path)
    {
        var result = new Dictionary<string, List<string>>();
        using var c = new SqliteConnection($"Data Source={path};Pooling=False");
        c.Open();
        var tables = new List<string>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'";
            using var r = cmd.ExecuteReader();
            while (r.Read()) tables.Add(r.GetString(0));
        }
        foreach (string table in tables)
        {
            var rows = new List<string>();
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"SELECT * FROM \"{table}\" ORDER BY rowid";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                rows.Add(string.Join("|", Enumerable.Range(0, r.FieldCount).Select(i => r.IsDBNull(i) ? "NULL" : r.GetValue(i).ToString())));
            result[table] = rows;
        }
        return result;
    }

    private static Dictionary<string, List<string>> ReadWithShard(SqliteForensicDatabase db)
    {
        var result = new Dictionary<string, List<string>>();
        foreach (var master in db.ReadSqliteMaster())
        {
            if (master.ObjectType != SqliteMasterObjectType.Table || master.RootPage is null || master.Name is null) continue;
            if (master.Name.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase)) continue;
            var schema = db.GetTableSchema(master.Name)!;
            int aliasIndex = schema.Columns.FindIndex(col => col.IsRowIdAlias);
            result[master.Name] = db.ReadTableRows(master.RootPage.Value)
                .OrderBy(row => row.RowId)
                .Select(row => string.Join("|", row.FieldValues.Select((v, i) =>
                    i == aliasIndex ? row.RowId.ToString()
                    : v is null || v.Value is null ? "NULL"
                    : v.Value.ToString())))
                .ToList();
        }
        return result;
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
