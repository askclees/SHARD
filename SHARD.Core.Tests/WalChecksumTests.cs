using System.Buffers.Binary;
using Microsoft.Data.Sqlite;
using SHARD.Core.Enums;
using SHARD.Core.Session;
using SHARD.Core.Shadow;
using SHARD.Core.WAL;

namespace SHARD.Core.Tests;

/// <summary>
/// WAL checksum validation and frame status. Most fixtures are WALs written by SQLite itself and
/// then damaged, so the checksum code is checked against SQLite's own output rather than itself.
/// </summary>
public class WalChecksumTests : IDisposable
{
    private readonly string _dir;

    public WalChecksumTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"shard_walck_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "TestData", "SHARDCreated", "WAL", name);

    // ── Fixture: a real WAL ──────────────────────────────────────────────────

    /// <summary>
    /// Creates a WAL-mode database whose file holds rows 1-2 (checkpointed), then writes each of
    /// <paramref name="walRows"/> in its own transaction, so the WAL has one commit frame per row.
    /// <paramref name="bigTransactionRows"/>, if non-zero, then adds that many rows in a single
    /// transaction spanning several pages. Copies the database and WAL out while the connection is
    /// still open (closing it would checkpoint and delete the WAL) and returns the copy's path.
    /// </summary>
    private string CreateRealWal(int[] walRows, int bigTransactionRows = 0)
    {
        string livePath = Path.Combine(_dir, $"live_{Guid.NewGuid():N}.db");
        using (var c = new SqliteConnection($"Data Source={livePath};Pooling=False"))
        {
            c.Open();
            Exec(c, "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0;");
            Exec(c, "CREATE TABLE t (id INTEGER PRIMARY KEY, v TEXT); INSERT INTO t VALUES (1, 'row1'), (2, 'row2');");
            Exec(c, "PRAGMA wal_checkpoint(TRUNCATE);");
            foreach (int id in walRows)
                Exec(c, $"INSERT INTO t VALUES ({id}, 'row{id}');");
            if (bigTransactionRows > 0)
            {
                var sql = new System.Text.StringBuilder("BEGIN;");
                for (int i = 0; i < bigTransactionRows; i++)
                    sql.Append($"INSERT INTO t VALUES ({1000 + i}, '{new string('x', 200)}');");
                sql.Append("COMMIT;");
                Exec(c, sql.ToString());
            }

            string copy = Path.Combine(_dir, $"evidence_{Guid.NewGuid():N}.db");
            CopyShared(livePath, copy);
            CopyShared(livePath + "-wal", copy + "-wal");
            return copy;
        }
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

    private static WalFile Parse(string walPath) => new(walPath, TextEncoding.Utf8, 0);

    private static int PageSize(string walPath)
    {
        var header = new byte[32];
        using (var fs = File.OpenRead(walPath)) fs.ReadExactly(header);
        return (int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8));
    }

    /// <summary>Flips a byte in the page data of 1-based frame <paramref name="frameNumber"/>.
    /// Offset 100 of a small table leaf page is unallocated space, so the page still parses.</summary>
    private static void CorruptFrame(string walPath, int frameNumber)
    {
        var bytes = File.ReadAllBytes(walPath);
        int pageSize = PageSize(walPath);
        bytes[32 + (frameNumber - 1) * (24 + pageSize) + 24 + 100] ^= 0xFF;
        File.WriteAllBytes(walPath, bytes);
    }

    // ── Checksum algorithm ───────────────────────────────────────────────────

    [Fact]
    public void Compute_KnownAnswer_BigAndLittleEndian()
    {
        byte[] data = [0, 0, 0, 1, 0, 0, 0, 2];
        // big-endian words 1, 2:  s0 = 0 + 1 + 0 = 1;  s1 = 0 + 2 + 1 = 3
        Assert.Equal((1u, 3u), WalChecksum.Compute(data, bigEndian: true));
        // little-endian words 0x01000000, 0x02000000
        Assert.Equal((0x01000000u, 0x03000000u), WalChecksum.Compute(data, bigEndian: false));
    }

    [Fact]
    public void Compute_WrapsAsUnsigned()
    {
        byte[] data = [0xFF, 0xFF, 0xFF, 0xFF, 0, 0, 0, 0];
        // s0 = 5 + (0xFFFFFFFF + 7) = 11 (mod 2^32);  s1 = 7 + 0 + 11 = 18
        Assert.Equal((11u, 18u), WalChecksum.Compute(data, bigEndian: true, 5, 7));
    }

    [Fact]
    public void Compute_RejectsLengthNotMultipleOfEight()
    {
        Assert.Throws<ArgumentException>(() => WalChecksum.Compute(new byte[7], bigEndian: true));
    }

    // ── SQLite-written WALs ──────────────────────────────────────────────────

    [Fact]
    public void RealWal_HeaderAndEveryFrameValidate()
    {
        var wal = Parse(CreateRealWal([3, 4, 5]) + "-wal");

        Assert.True(wal.HeaderChecksumValid);
        Assert.Equal(3, wal.Frames.Count);
        Assert.Equal(3, wal.CommittedFrameCount);
        Assert.All(wal.Frames, f =>
        {
            Assert.Equal(WalFrameStatus.Committed, f.Status);
            Assert.True(f.ChecksumValid);
        });
        Assert.Null(wal.ValidationStopReason);
    }

    [Fact]
    public void FixtureWithOldGeneration_OnlyCurrentFramesCommit()
    {
        // places_wal: one frame after a checkpoint reset, then four frames of the previous generation
        var wal = Parse(FixturePath("places_wal.db-wal"));

        Assert.True(wal.HeaderChecksumValid);
        Assert.Equal(1, wal.CommittedFrameCount);
        Assert.Equal(
            [WalFrameStatus.Committed, WalFrameStatus.OldGeneration, WalFrameStatus.OldGeneration,
             WalFrameStatus.OldGeneration, WalFrameStatus.OldGeneration],
            wal.Frames.Select(f => f.Status));
        // The first old frame's predecessor was overwritten, so it can't be checked; the rest chain.
        Assert.Equal([true, (bool?)null, true, true, true], wal.Frames.Select(f => f.ChecksumValid));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void CorruptFrame_SqliteStopsThere(int corruptFrame)
    {
        string db = CreateRealWal([3, 4, 5]);
        CorruptFrame(db + "-wal", corruptFrame);

        var wal = Parse(db + "-wal");

        Assert.Equal(corruptFrame - 1, wal.CommittedFrameCount);
        for (int i = 0; i < wal.Frames.Count; i++)
        {
            var expected = i <  corruptFrame - 1 ? WalFrameStatus.Committed
                         : i == corruptFrame - 1 ? WalFrameStatus.InvalidChecksum
                         :                         WalFrameStatus.AfterInvalid;
            Assert.Equal(expected, wal.Frames[i].Status);
        }
        Assert.False(wal.Frames[corruptFrame - 1].ChecksumValid);
        // Later frames still chain from the damaged frame's stored checksum: one frame's data was
        // altered, rather than the log being cut off or rewritten.
        Assert.All(wal.Frames.Skip(corruptFrame), f => Assert.True(f.ChecksumValid));
        Assert.Contains(EvidenceSession.DescribeWalProblems(wal), p => p.Contains($"frame {corruptFrame}'s checksum"));
    }

    [Fact]
    public void TransactionWithoutCommitFrame_IsUncommitted()
    {
        string db = CreateRealWal([3], bigTransactionRows: 200);
        string walPath = db + "-wal";
        var full = Parse(walPath);
        Assert.True(full.Frames.Count > 3, "the big transaction should span several frames");
        Assert.True(full.Frames[^1].Header.SizeOfDatabaseInPages > 0);

        // Drop the big transaction's commit frame, as if the copy was taken before it was written
        int pageSize = PageSize(walPath);
        var bytes = File.ReadAllBytes(walPath);
        File.WriteAllBytes(walPath, bytes[..(bytes.Length - (24 + pageSize))]);

        var wal = Parse(walPath);

        Assert.Equal(1, wal.CommittedFrameCount); // only row 3's transaction
        Assert.Equal(WalFrameStatus.Committed, wal.Frames[0].Status);
        Assert.All(wal.Frames.Skip(1), f => Assert.Equal(WalFrameStatus.Uncommitted, f.Status));
        Assert.Contains(EvidenceSession.DescribeWalProblems(wal), p => p.Contains("never committed"));
    }

    [Fact]
    public void InvalidHeaderChecksum_SqliteIgnoresEveryFrame()
    {
        string db = CreateRealWal([3, 4]);
        var bytes = File.ReadAllBytes(db + "-wal");
        bytes[12] ^= 0x01; // checkpoint sequence number, covered by the header checksum
        File.WriteAllBytes(db + "-wal", bytes);

        var wal = Parse(db + "-wal");

        Assert.False(wal.HeaderChecksumValid);
        Assert.Equal(0, wal.CommittedFrameCount);
        Assert.All(wal.Frames, f => Assert.Equal(WalFrameStatus.AfterInvalid, f.Status));
        Assert.Contains(EvidenceSession.DescribeWalProblems(wal), p => p.Contains("header checksum"));
    }

    [Fact]
    public void PageNumberZero_StopsReading()
    {
        // SQLite never writes page 0, so this is synthesised, with checksums computed to continue
        // the chain so that only the page number is wrong.
        const uint pageSize = 512;
        var header = new byte[32];
        BinaryPrimitives.WriteUInt32BigEndian(header,              0x377f0682);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4),    3007000);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8),    pageSize);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16),   0x11111111);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(20),   0x22222222);
        var (s0, s1) = WalChecksum.Compute(header.AsSpan(0, 24), bigEndian: false);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(24), s0);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(28), s1);

        var bytes = new List<byte>(header);
        foreach (var (page, dbSize) in new[] { (2u, 2u), (0u, 2u), (2u, 2u) })
        {
            var data = new byte[pageSize];
            (s0, s1) = WalChecksum.ComputeFrame(page, dbSize, data, bigEndian: false, s0, s1);
            var fh = new byte[24];
            BinaryPrimitives.WriteUInt32BigEndian(fh,             page);
            BinaryPrimitives.WriteUInt32BigEndian(fh.AsSpan(4),   dbSize);
            BinaryPrimitives.WriteUInt32BigEndian(fh.AsSpan(8),   0x11111111);
            BinaryPrimitives.WriteUInt32BigEndian(fh.AsSpan(12),  0x22222222);
            BinaryPrimitives.WriteUInt32BigEndian(fh.AsSpan(16),  s0);
            BinaryPrimitives.WriteUInt32BigEndian(fh.AsSpan(20),  s1);
            bytes.AddRange(fh);
            bytes.AddRange(data);
        }
        string path = Path.Combine(_dir, "page0.wal");
        File.WriteAllBytes(path, bytes.ToArray());

        var wal = Parse(path);

        Assert.True(wal.HeaderChecksumValid);
        Assert.Equal(
            [WalFrameStatus.Committed, WalFrameStatus.InvalidPageNumber, WalFrameStatus.AfterInvalid],
            wal.Frames.Select(f => f.Status));
        Assert.Equal(1, wal.CommittedFrameCount);
    }

    // ── Recovery uses the statuses ───────────────────────────────────────────

    [Fact]
    public void Recovery_RowsOnlyInIgnoredFrames_AreNotLive_AndCommittedRowsAreNotReportedDeleted()
    {
        // File: rows 1-2. WAL: row 3, row 4, row 5, one commit each. Frame 2 (row 4) is damaged,
        // so SQLite sees rows 1-3; rows 4-5 exist only in frames it ignores.
        string db = CreateRealWal([3, 4, 5]);
        CorruptFrame(db + "-wal", 2);

        using var session = EvidenceSession.Open(db);
        var (project, _) = ShadowProject.CreateTemporary(db, session.Database);
        using (project)
        {
            var result = session.SyncAndRecoverWal(project, session.Wal!);
            Assert.Null(result.SyncError);
            Assert.Null(result.RecoveryError);

            using var c = new SqliteConnection($"Data Source={project.ShadowDatabasePath};Mode=ReadOnly;Pooling=False");
            c.Open();
            Assert.Equal([1L, 2L, 3L], Ids(c, "SELECT id FROM t ORDER BY id"));
            Assert.Equal([4L, 5L], Ids(c,
                $"SELECT id FROM \"{ShadowDatabaseBuilder.RecoveredTablePrefix}t\" " +
                $"WHERE \"{ShadowDatabaseBuilder.RecoveryMethodColumn}\" = '{ShadowDatabaseBuilder.RecoveryMethodWalFrame}' ORDER BY id"));
        }
    }

    private static List<long> Ids(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        var ids = new List<long>();
        while (r.Read()) ids.Add(r.GetInt64(0));
        return ids;
    }
}
