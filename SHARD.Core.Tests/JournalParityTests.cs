using Microsoft.Data.Sqlite;
using SHARD.Core.Recovery;
using SHARD.Core.Session;
using SHARD.Core.Shadow;

namespace SHARD.Core.Tests;

/// <summary>
/// Rollback journals get what WALs get: live tables that hold what SQLite returns (for a hot
/// journal, the database rolled back), the uncommitted rows the rollback discards recovered from
/// the file, provenance (<c>_journal_record</c>), deleted data searched inside journal records,
/// row states, and problems reported. Every fixture is written by SQLite itself.
/// </summary>
public class JournalParityTests : IDisposable
{
    private readonly string _dir;

    public JournalParityTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"shard_journal_parity_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────

    /// <summary>
    /// A hot journal, as a crash mid-transaction leaves it: t holds rows 1-40 and other rows 1-3.
    /// The transaction changes row 20, deletes row 25 and inserts row 1000, then rewrites every
    /// filler row with a one-page cache, so SQLite spills those changes to the database file. The
    /// file and journal are copied before the transaction ends. secure_delete is on, so row 25
    /// leaves no trace in the file.
    /// </summary>
    private string CreateHotJournal()
    {
        string live = Path.Combine(_dir, $"live_{Guid.NewGuid():N}.db");
        using (var c = new SqliteConnection($"Data Source={live};Pooling=False"))
        {
            c.Open();
            Exec(c, """
                PRAGMA journal_mode=DELETE; PRAGMA secure_delete=ON;
                CREATE TABLE t (id INTEGER PRIMARY KEY, v TEXT);
                CREATE TABLE filler (id INTEGER PRIMARY KEY, v TEXT);
                CREATE TABLE other (id INTEGER PRIMARY KEY, v TEXT);
                WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 40)
                  INSERT INTO t SELECT i, 'row' || i || '-' || printf('%.*c', 60, 'x') FROM n;
                WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 400)
                  INSERT INTO filler SELECT i, printf('%.*c', 300, 'f') FROM n;
                INSERT INTO other VALUES (1, 'a'), (2, 'b'), (3, 'c');
                PRAGMA cache_size=1;
                BEGIN;
                UPDATE t SET v = 'changed' WHERE id = 20;
                DELETE FROM t WHERE id = 25;
                INSERT INTO t VALUES (1000, 'new uncommitted');
                UPDATE filler SET v = 'g';
                """);
            string evidence = Path.Combine(_dir, $"evidence_{Guid.NewGuid():N}.db");
            CopyShared(live, evidence);
            CopyShared(live + "-journal", evidence + "-journal");
            Exec(c, "ROLLBACK;");

            // The premise: the uncommitted writes reached the file, and the journal is hot
            var raw = File.ReadAllBytes(evidence);
            Assert.True(Contains(raw, "changed"u8) && Contains(raw, "new uncommitted"u8), "the transaction's writes should have spilled to the file");
            return evidence;
        }
    }

    /// <summary>
    /// journal_mode=PERSIST, secure_delete off: row 25 is deleted (its bytes stay in the page as
    /// freed space), then re-inserted with a value of the same length, which reuses that space.
    /// Only the journal's copy of the page from before the re-insert still holds the original row.
    /// </summary>
    private string CreatePersistJournalWithFreedRow()
    {
        string db = Path.Combine(_dir, $"persist_{Guid.NewGuid():N}.db");
        using (var c = new SqliteConnection($"Data Source={db};Pooling=False"))
        {
            c.Open();
            Exec(c, """
                PRAGMA journal_mode=PERSIST; PRAGMA secure_delete=OFF;
                CREATE TABLE t (id INTEGER PRIMARY KEY, v TEXT);
                WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 40)
                  INSERT INTO t SELECT i, 'row' || printf('%03d', i) || '-' || printf('%.*c', 60, 'x') FROM n;
                DELETE FROM t WHERE id = 25;
                INSERT INTO t VALUES (25, 'new' || printf('%03d', 25) || '-' || printf('%.*c', 60, 'y'));
                """);
        }
        Assert.False(Contains(File.ReadAllBytes(db), "row025-"u8), "the re-insert should have overwritten row 25 in the file");
        Assert.True(Contains(File.ReadAllBytes(db + "-journal"), "row025-"u8), "the journal should still hold row 25");
        return db;
    }

    // ── Hot journal: live tables, recovery ───────────────────────────────────

    [Fact]
    public void HotJournal_LiveTablesMatchSqlite_AndUncommittedRowsAreRecovered()
    {
        string db = CreateHotJournal();
        string shadow = Path.Combine(_dir, "shadow.db");
        using (var session = EvidenceSession.Open(db))
        {
            Assert.True(session.JournalIsHot);
            Assert.True(session.CommittedDatabase!.IncludesJournal);
            session.BuildShadowDatabase(shadow);
        }

        using var c = Open(shadow);
        Assert.Equal(SqliteView(db, "SELECT id, v FROM t ORDER BY id"), Rows(c, "SELECT id, v FROM t ORDER BY id"));
        Assert.Contains("25|row25-" + new string('x', 60), Rows(c, "SELECT id, v FROM t WHERE id = 25"));
        Assert.Equal(ShadowDatabaseBuilder.LiveSourceRolledBackJournal, Info(c));

        Assert.Equal(
            [$"20|changed|{ShadowDatabaseBuilder.RecoveryMethodDbFileUncommittedVersion}",
             $"1000|new uncommitted|{ShadowDatabaseBuilder.RecoveryMethodDbFileUncommitted}"],
            Rows(c, $"""
                SELECT id, v, "{ShadowDatabaseBuilder.RecoveryMethodColumn}" FROM "{ShadowDatabaseBuilder.RecoveredTablePrefix}t"
                WHERE id > 0 ORDER BY id
                """));
    }

    [Fact]
    public void HotJournal_ProcessJournalOff_LiveTablesAreTheFileAlone()
    {
        string db = CreateHotJournal();
        string shadow = Path.Combine(_dir, "shadow.db");
        using (var session = EvidenceSession.Open(db))
            session.BuildShadowDatabase(shadow, new RecoveryFlags(ProcessJournal: false));

        using var c = Open(shadow);
        Assert.Equal(["20|changed"], Rows(c, "SELECT id, v FROM t WHERE id = 20"));
        Assert.Equal(["1000|new uncommitted"], Rows(c, "SELECT id, v FROM t WHERE id = 1000"));
        Assert.Empty(Rows(c, "SELECT id FROM t WHERE id = 25"));
        Assert.Equal(ShadowDatabaseBuilder.LiveSourceDatabaseFile, Info(c));
    }

    [Fact]
    public void HotJournal_RolledBackState_MatchesTheJournal()
    {
        string db = CreateHotJournal();
        using var session = EvidenceSession.Open(db);
        var journal = session.Journal!;
        var rolledBack = session.CommittedDatabase!;

        var played = SqliteForensicDatabase.RecordsPlayedBack(journal);
        Assert.NotEmpty(played);
        Assert.Equal((uint)played[^1].Segment.Header.DatabaseSize, rolledBack.PageCount);
        var numbered = journal.Segments.SelectMany(s => s.PageRecords).ToList();
        foreach (var (record, number, _) in played)
        {
            Assert.Same(record, numbered[number - 1]);
            Assert.Equal(record.Page.Data, rolledBack.ReadPage(record.PageNumber).Data);
            Assert.NotNull(rolledBack.JournalRecordNumberFor(record.PageNumber));
        }
        Assert.Null(rolledBack.WalFrameNumberFor(1));
        Assert.False(rolledBack.IncludesWal);
    }

    [Fact]
    public void HotJournal_Facade_CountsLiveRowsFromTheRolledBackState()
    {
        string db = CreateHotJournal();
        var result = SqliteRecoveryFacade.Recover(db, Path.Combine(_dir, "recovered.db"));

        Assert.Equal(40, result.Tables.Single(t => t.TableName == "t").LiveRowCount);
        Assert.True(result.JournalRecordsInserted >= 2);
    }

    [Fact]
    public void HotJournal_ProjectFromRolledBackState_RecoversOnce()
    {
        string db = CreateHotJournal();
        using var session = EvidenceSession.Open(db);
        var (project, _) = ShadowProject.CreateTemporary(db, session.ShadowSource(), layoutDatabase: session.Database);
        using (project)
        {
            Assert.True(project.LiveTablesIncludeJournal);
            var first = session.RecoverJournal(project, session.Journal!);
            Assert.Null(first.Error);
            Assert.True(first.Recovered >= 2);
            Assert.Equal((0, (string?)null), session.RecoverJournal(project, session.Journal!));

            using var c = Open(project.ShadowDatabasePath);
            Assert.Equal(SqliteView(db, "SELECT id, v FROM t ORDER BY id"), Rows(c, "SELECT id, v FROM t ORDER BY id"));
            Assert.Equal(["1000"], Rows(c, $"""
                SELECT id FROM "{ShadowDatabaseBuilder.RecoveredTablePrefix}t"
                WHERE "{ShadowDatabaseBuilder.RecoveryMethodColumn}" = '{ShadowDatabaseBuilder.RecoveryMethodDbFileUncommitted}'
                """));
        }
    }

    // ── Row states ───────────────────────────────────────────────────────────

    [Fact]
    public void HotJournal_RowStates()
    {
        string db = CreateHotJournal();
        string shadow = Path.Combine(_dir, "shadow.db");
        using (var session = EvidenceSession.Open(db))
            session.BuildShadowDatabase(shadow);

        using var c = Open(shadow);
        Assert.Equal("journal", ShadowDatabaseBuilder.ReadInfo(c, RowStates.InfoKeyTransactionLog));
        // live = rolled back: (db, log, current)
        Assert.Equal("3|1|1", State(c, "t", "id = 20"));                      // the file has an uncommitted version
        Assert.Equal("0|1|1", State(c, "t", "id = 25"));                      // deleted by the uncommitted transaction
        Assert.Equal("1|NULL|1", State(c, "other", "id = 1"));               // never journaled
        // recovered from the file
        Assert.Equal("1|3|0", State(c, "_shard_recovered_t", "id = 20"));    // uncommitted version
        Assert.Equal("1|0|0", State(c, "_shard_recovered_t", "id = 1000"));  // uncommitted insert
    }

    // ── Journal records' deleted data, provenance ────────────────────────────

    [Fact]
    public void PersistJournal_FreedSpaceInAJournalRecord_IsRecoveredWithItsRecordNumber()
    {
        string db = CreatePersistJournalWithFreedRow();
        string withJournal = Path.Combine(_dir, "with.db"), without = Path.Combine(_dir, "without.db");
        using (var session = EvidenceSession.Open(db))
            session.BuildShadowDatabase(without);
        using var s = EvidenceSession.Open(db, inferZeroedJournalHeader: true);
        s.BuildShadowDatabase(withJournal);

        string Find = $"""
            SELECT "{ShadowDatabaseBuilder.RecoveryMethodColumn}", "{ShadowDatabaseBuilder.JournalRecordColumn}", _page_number,
                   _db_state, _log_state, _is_current
            FROM "{ShadowDatabaseBuilder.RecoveredTablePrefix}t" WHERE v LIKE 'row025-%'
            """;
        using (var c = Open(without))
            Assert.Empty(Rows(c, Find));

        using var cw = Open(withJournal);
        var found = Rows(cw, Find);
        Assert.Single(found);
        var f = found[0].Split('|');
        Assert.Contains(f[0], new[] { ShadowDatabaseBuilder.RecoveryMethodFreeblock, ShadowDatabaseBuilder.RecoveryMethodDeletedCell, ShadowDatabaseBuilder.RecoveryMethodCarving });
        var record = s.Journal!.Segments.SelectMany(x => x.PageRecords).ElementAt(int.Parse(f[1]) - 1);
        Assert.Equal(f[2], record.PageNumber.ToString());
        Assert.Equal(["0", "2", "0"], f[3..]); // not in the file; deleted data in the journal; not current
    }

    [Fact]
    public void JournalRows_CarryTheirRecordNumber()
    {
        // A project built from the file alone: the hot journal's rows are recovered from its records
        string db = CreateHotJournal();
        using var session = EvidenceSession.Open(db);
        var (project, _) = ShadowProject.CreateTemporary(db, session.Database);
        using (project)
        {
            Assert.Null(session.RecoverJournal(project, session.Journal!).Error);

            using var c = Open(project.ShadowDatabasePath);
            var rows = Rows(c, $"""
                SELECT id, "{ShadowDatabaseBuilder.RecoveryMethodColumn}", "{ShadowDatabaseBuilder.JournalRecordColumn}",
                       "{ShadowDatabaseBuilder.WalFrameColumn}", _page_number
                FROM "{ShadowDatabaseBuilder.RecoveredTablePrefix}t"
                WHERE id IN (20, 25) ORDER BY id
                """);
            Assert.Equal(2, rows.Count);
            Assert.StartsWith($"20|{ShadowDatabaseBuilder.RecoveryMethodJournalPreviousVersion}|", rows[0]);
            Assert.StartsWith($"25|{ShadowDatabaseBuilder.RecoveryMethodJournalPage}|", rows[1]);
            var records = session.Journal!.Segments.SelectMany(x => x.PageRecords).ToList();
            foreach (var f in rows.Select(r => r.Split('|')))
            {
                Assert.Equal("NULL", f[3]);
                Assert.Equal(f[4], records[int.Parse(f[2]) - 1].PageNumber.ToString());
            }
        }
    }

    // ── Problems ─────────────────────────────────────────────────────────────

    [Fact]
    public void HotJournal_IsReported()
    {
        string db = CreateHotJournal();
        using var session = EvidenceSession.Open(db);
        var problems = EvidenceSession.DescribeJournalProblems(session.Journal!);
        Assert.Contains(problems, p => p.StartsWith("hot:"));

        var (warnings, _, _) = session.BuildShadowDatabase(Path.Combine(_dir, "shadow.db"));
        Assert.Contains(warnings, w => w.StartsWith("Journal: hot:"));
    }

    [Fact]
    public void TruncatedHotJournal_ReportsTheCutOffRecord_AndStillRollsBack()
    {
        // Cut the file part-way through the last record a header counts. (Anything after it is a
        // segment SQLite hadn't synced yet, whose header magic it leaves zeroed until it does.)
        string db = CreateHotJournal();
        long cut;
        using (var before = EvidenceSession.Open(db))
        {
            var lastCounted = before.Journal!.Segments.Last().PageRecords.Last();
            cut = lastCounted.FileOffset + 100;
        }
        using (var fs = new FileStream(db + "-journal", FileMode.Open))
            fs.SetLength(cut);

        using var session = EvidenceSession.Open(db);
        var problems = EvidenceSession.DescribeJournalProblems(session.Journal!);
        Assert.Contains(problems, p => p.Contains("less than a whole record"));
        Assert.Contains(problems, p => p.Contains("the file ends after"));
        Assert.True(session.JournalIsHot);
        Assert.NotNull(session.CommittedDatabase);
    }

    [Fact]
    public void ZeroedHeaderJournal_IsNeverRolledBack()
    {
        string db = CreatePersistJournalWithFreedRow();
        using var session = EvidenceSession.Open(db, inferZeroedJournalHeader: true);
        Assert.True(session.HasJournal);
        Assert.False(session.JournalIsHot);
        Assert.Null(session.CommittedDatabase);
        Assert.Empty(SqliteForensicDatabase.RecordsPlayedBack(session.Journal!));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string State(SqliteConnection c, string table, string where) =>
        Rows(c, $"SELECT _db_state, _log_state, _is_current FROM \"{table}\" WHERE {where}").Single();

    /// <summary>What SQLite itself returns, from a separate copy (opening it rolls the journal back).</summary>
    private List<string> SqliteView(string db, string sql)
    {
        string copy = Path.Combine(_dir, $"sqlite_{Guid.NewGuid():N}.db");
        File.Copy(db, copy);
        File.Copy(db + "-journal", copy + "-journal");
        using var c = new SqliteConnection($"Data Source={copy};Pooling=False");
        c.Open();
        return Rows(c, sql);
    }

    private static string? Info(SqliteConnection c) =>
        ShadowDatabaseBuilder.ReadInfo(c, ShadowDatabaseBuilder.InfoKeyLiveTablesSource);

    private static bool Contains(byte[] haystack, ReadOnlySpan<byte> needle) => haystack.AsSpan().IndexOf(needle) >= 0;

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
