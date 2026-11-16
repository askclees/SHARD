using Microsoft.Data.Sqlite;
using SHARD.Core.Recovery;
using SHARD.Core.Session;
using SHARD.Core.Shadow;

namespace SHARD.Core.Tests;

/// <summary>
/// Recovery from every page image the WAL holds — including committed frames a later committed
/// frame replaced, and the deleted data (deleted cells, unallocated space, freeblocks) inside each
/// frame and inside the database file's replaced pages — through the path the product uses
/// (<see cref="EvidenceSession.BuildShadowDatabase"/>, live tables from the committed state).
/// </summary>
public class WalHistoryTests : IDisposable
{
    private readonly string _dir;

    public WalHistoryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"shard_walhist_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    /// <summary>Copies a WAL fixture (database, -wal, -shm) so tests never touch the originals.</summary>
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

    private sealed record Recovered(long Id, string Url, string Method, long? WalFrame);

    private static List<Recovered> ReadRecovered(string shadow)
    {
        using var c = new SqliteConnection($"Data Source={shadow};Mode=ReadOnly;Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, url, "{ShadowDatabaseBuilder.RecoveryMethodColumn}", "{ShadowDatabaseBuilder.WalFrameColumn}"
            FROM "{ShadowDatabaseBuilder.RecoveredTablePrefix}moz_places" ORDER BY url
            """;
        using var r = cmd.ExecuteReader();
        var rows = new List<Recovered>();
        while (r.Read())
            rows.Add(new Recovered(r.GetInt64(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetInt64(3)));
        return rows;
    }

    private static List<long> LiveIds(string shadow)
    {
        using var c = new SqliteConnection($"Data Source={shadow};Mode=ReadOnly;Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id FROM moz_places ORDER BY id";
        using var r = cmd.ExecuteReader();
        var ids = new List<long>();
        while (r.Read()) ids.Add(r.GetInt64(0));
        return ids;
    }

    // ── places_wal_freeblocks (secure_delete off) ────────────────────────────

    [Fact]
    public void Freeblocks_EachDeletedRowRecoveredOnce_FromWhereItSurvives()
    {
        string shadow = Build(CopyFixture("places_wal_freeblocks.db"));

        Assert.Equal([1L, 3L, 4L, 5L, 51L], LiveIds(shadow));

        var recovered = ReadRecovered(shadow);
        Assert.Equal(
            ["https://example2.com/", "https://example30.com/", "https://example50.com/"],
            recovered.Select(r => r.Url));

        // Row 2: live in the file, deleted by the WAL — the file's own live cell, rowid intact. Its
        // freed copy inside the WAL frames is the same record, so it isn't listed again.
        var row2 = recovered.Single(r => r.Url == "https://example2.com/");
        Assert.Equal((2L, ShadowDatabaseBuilder.RecoveryMethodDbFileDeletedInWal, (long?)null), (row2.Id, row2.Method, row2.WalFrame));

        // Row 30: inserted and deleted within the WAL
        var row30 = recovered.Single(r => r.Url == "https://example30.com/");
        Assert.Equal(30L, row30.Id);
        Assert.NotNull(row30.WalFrame);

        // Row 50: inserted and deleted in one transaction — never live; only freed space in frames
        var row50 = recovered.Single(r => r.Url == "https://example50.com/");
        Assert.Equal(ShadowDatabaseBuilder.RecoveryMethodFreeblock, row50.Method);
        Assert.NotNull(row50.WalFrame);
    }

    [Fact]
    public void Freeblocks_FreeblockCarvingOff_LosesTheRowThatOnlySurvivesAsFreedSpace()
    {
        string shadow = Build(CopyFixture("places_wal_freeblocks.db"), new RecoveryFlags(FreeblockCarving: false));

        Assert.DoesNotContain(ReadRecovered(shadow), r => r.Url == "https://example50.com/");
    }

    [Fact]
    public void Freeblocks_ProcessWalOff_NothingFromTheWal()
    {
        string shadow = Build(CopyFixture("places_wal_freeblocks.db"), new RecoveryFlags(ProcessWal: false));

        Assert.Equal([1L, 2L, 3L, 4L, 5L], LiveIds(shadow));
        Assert.All(ReadRecovered(shadow), r => Assert.Null(r.WalFrame));
    }

    // ── places_wal_no_post_restart_writes (secure_delete on) ─────────────────

    [Fact]
    public void SupersededCommittedFrame_IsTheOnlyCopy_AndIsRecovered()
    {
        string shadow = Build(CopyFixture("places_wal_no_post_restart_writes.db"));

        Assert.Equal([1L, 2L, 3L, 7L, 8L, 9L, 10L], LiveIds(shadow));
        var recovered = ReadRecovered(shadow).Where(r => r.Id > 0).OrderBy(r => r.Id).ToList();
        Assert.Equal([4L, 5L, 6L], recovered.Select(r => r.Id));
        Assert.All(recovered, r => Assert.Equal((ShadowDatabaseBuilder.RecoveryMethodWalFrame, (long?)3), (r.Method, r.WalFrame)));
    }

    // ── Freed space that a later frame overwrote ─────────────────────────────

    [Fact]
    public void FreedSpaceOverwrittenLater_IsRecoveredFromTheEarlierFrame()
    {
        // secure_delete off. Row 10 is inserted and deleted inside one transaction, so it is never a
        // live cell in any frame — the frame that transaction commits holds it only as a freeblock.
        // Then row 12, exactly the same size, reuses that freeblock, so in the latest page image row
        // 10's bytes are gone too. Only that earlier frame's freed space still holds them.
        string live = Path.Combine(_dir, "reuse_live.db");
        string db = Path.Combine(_dir, "reuse.db");
        using (var c = new SqliteConnection($"Data Source={live};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"""
                PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; PRAGMA secure_delete=OFF;
                CREATE TABLE moz_places (id INTEGER PRIMARY KEY, url TEXT NOT NULL);
                INSERT INTO moz_places VALUES (1, 'https://keep.example/');
                PRAGMA wal_checkpoint(TRUNCATE);
                BEGIN;
                INSERT INTO moz_places VALUES (10, 'https://ten.example/{new string('a', 60)}');
                INSERT INTO moz_places VALUES (11, 'https://eleven.example/');
                DELETE FROM moz_places WHERE id = 10;
                COMMIT;
                INSERT INTO moz_places VALUES (12, 'https://two.example/{new string('b', 60)}');
                """;
            cmd.ExecuteNonQuery();
            foreach (string suffix in new[] { "", "-wal" })
            {
                using var src = new FileStream(live + suffix, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var dst = File.Create(db + suffix);
                src.CopyTo(dst);
            }
        }

        // The latest image of the page really has lost row 10's bytes
        using (var session = EvidenceSession.Open(db))
        {
            var latest = session.Wal!.Frames.Last(f => f.Header.PageNumber == 2);
            Assert.DoesNotContain("https://ten.example/", System.Text.Encoding.ASCII.GetString(latest.PageData));
        }

        string shadow = Build(db);

        Assert.Equal([1L, 11L, 12L], LiveIds(shadow));
        var row10 = Assert.Single(ReadRecovered(shadow), r => r.Url.StartsWith("https://ten.example/"));
        Assert.Equal(ShadowDatabaseBuilder.RecoveryMethodFreeblock, row10.Method);
        Assert.NotNull(row10.WalFrame);
    }

    // ── Re-running and older shadows ─────────────────────────────────────────

    [Fact]
    public void RecoveringAgain_AddsNothing()
    {
        string db = CopyFixture("places_wal_freeblocks.db");
        using var session = EvidenceSession.Open(db);
        var (project, _) = ShadowProject.CreateTemporary(db, session.ShadowSource(), layoutDatabase: session.Database);
        using (project)
        {
            var first = session.SyncAndRecoverWal(project, session.Wal!);
            Assert.True(first.Recovered > 0);
            Assert.Equal(0, session.SyncAndRecoverWal(project, session.Wal!).Recovered);
        }
    }

    [Fact]
    public void ShadowWithoutWalFrameColumn_GetsItAdded()
    {
        // A recovered table from before _wal_frame existed
        string db = CopyFixture("places_wal_freeblocks.db");
        string shadow = Path.Combine(_dir, "old.db");
        using (var file = SqliteForensicDatabase.Open(db))
            ShadowDatabaseBuilder.Create(shadow, file);
        using (var c = new SqliteConnection($"Data Source={shadow};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"ALTER TABLE \"{ShadowDatabaseBuilder.RecoveredTablePrefix}moz_places\" DROP COLUMN \"{ShadowDatabaseBuilder.WalFrameColumn}\"";
            cmd.ExecuteNonQuery();

            using var session = EvidenceSession.Open(db);
            int inserted = ShadowDatabaseBuilder.InsertWalDeletedRows(c, session.Database, session.Wal!);
            Assert.True(inserted > 0);
        }
        Assert.Contains(ReadRecovered(shadow), r => r.WalFrame is not null);
    }
}
