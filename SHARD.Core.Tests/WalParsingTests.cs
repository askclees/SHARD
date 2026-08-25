using System.Buffers.Binary;
using Microsoft.Data.Sqlite;
using SHARD.Core.Enums;
using SHARD.Core.WAL;

namespace SHARD.Core.Tests;

public class WalParsingTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static byte[] MakeWalHeader(
        uint magic = 0x377f0682,
        uint version = 3007000,
        uint pageSize = 4096,
        uint checkpointSeq = 0,
        uint salt1 = 1,
        uint salt2 = 2,
        uint checksum1 = 3,
        uint checksum2 = 4)
    {
        var bytes = new byte[32];
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0),  magic);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4),  version);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8),  pageSize);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(12), checkpointSeq);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16), salt1);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20), salt2);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(24), checksum1);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(28), checksum2);
        return bytes;
    }

    private static byte[] MakeFrameHeader(
        uint pageNumber = 1,
        uint dbSizeInPages = 0,
        uint salt1 = 1,
        uint salt2 = 2,
        uint checksum1 = 3,
        uint checksum2 = 4)
    {
        var bytes = new byte[24];
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0),  pageNumber);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4),  dbSizeInPages);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8),  salt1);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(12), salt2);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16), checksum1);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20), checksum2);
        return bytes;
    }

    private static VerificationData MakeVerificationData(uint salt1 = 1, uint salt2 = 2,
        uint checksum1 = 3, uint checksum2 = 4) =>
        new(salt1, salt2, checksum1, checksum2);

    private static string CreateWalDatabase()
    {
        string path = Path.Combine(Path.GetTempPath(), $"shard_wal_{Guid.NewGuid():N}.db");
        using var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL";
        cmd.ExecuteScalar();
        cmd.CommandText = "PRAGMA wal_autocheckpoint=0";
        cmd.ExecuteNonQuery();
        cmd.CommandText = "CREATE TABLE t (id INTEGER PRIMARY KEY, value TEXT)";
        cmd.ExecuteNonQuery();
        cmd.CommandText = "INSERT INTO t VALUES (1, 'hello')";
        cmd.ExecuteNonQuery();
        return path;
    }

    private static void DeleteWalDatabase(string dbPath)
    {
        foreach (string suffix in new[] { "", "-wal", "-shm" })
        {
            string p = dbPath + suffix;
            if (File.Exists(p)) File.Delete(p);
        }
    }

    /// <summary>
    /// Writes a synthetic WAL file with exact control over each frame's page number and
    /// database-size (commit-boundary) header field — page content is irrelevant to the
    /// transaction-grouping logic under test, so every frame's page data is left zeroed.
    /// </summary>
    private static string WriteSyntheticWalFile(params (uint PageNumber, uint DbSizeInPages)[] frames)
    {
        const uint pageSize = 4096;
        string path = Path.Combine(Path.GetTempPath(), $"shard_wal_synth_{Guid.NewGuid():N}.wal");

        var bytes = new List<byte>(MakeWalHeader(pageSize: pageSize));
        foreach (var (pageNumber, dbSizeInPages) in frames)
        {
            bytes.AddRange(MakeFrameHeader(pageNumber: pageNumber, dbSizeInPages: dbSizeInPages));
            bytes.AddRange(new byte[pageSize]);
        }

        File.WriteAllBytes(path, bytes.ToArray());
        return path;
    }

    // ── WalHeader unit tests ─────────────────────────────────────────────────

    [Fact]
    public void WalHeader_ParsesAllFields()
    {
        var bytes = MakeWalHeader(magic: 0x377f0682, version: 3007000, pageSize: 4096,
            checkpointSeq: 5, salt1: 11, salt2: 22, checksum1: 33, checksum2: 44);

        var header = new WalHeader(bytes);

        Assert.Equal(0x377f0682u, header.MagicNumber);
        Assert.Equal(3007000u,    header.FileFormatVersion);
        Assert.Equal(4096u,       header.DatabasePageSize);
        Assert.Equal(5u,          header.CheckpointSequenceNumber);
        Assert.Equal(11u,         header.VerificationData.Salt1);
        Assert.Equal(22u,         header.VerificationData.Salt2);
        Assert.Equal(33u,         header.VerificationData.Checksum1);
        Assert.Equal(44u,         header.VerificationData.Checksum2);
    }

    [Fact]
    public void WalHeader_AcceptsAlternativeMagicNumber()
    {
        var bytes = MakeWalHeader(magic: 0x377f0683);
        var header = new WalHeader(bytes);
        Assert.Equal(0x377f0683u, header.MagicNumber);
    }

    [Fact]
    public void WalHeader_ThrowsOnInvalidMagicNumber()
    {
        var bytes = MakeWalHeader(magic: 0xDEADBEEF);
        Assert.Throws<InvalidDataException>(() => new WalHeader(bytes));
    }

    [Fact]
    public void WalHeader_ThrowsWhenTooShort()
    {
        Assert.Throws<InvalidDataException>(() => new WalHeader(new byte[31]));
    }

    [Fact]
    public void WalHeader_ThrowsWhenTooLong()
    {
        Assert.Throws<InvalidDataException>(() => new WalHeader(new byte[33]));
    }

    // ── WalFrameHeader unit tests ─────────────────────────────────────────────

    [Fact]
    public void WalFrameHeader_ParsesAllFields()
    {
        var bytes = MakeFrameHeader(pageNumber: 3, dbSizeInPages: 5,
            salt1: 10, salt2: 20, checksum1: 30, checksum2: 40);
        var walChecksums = MakeVerificationData(salt1: 10, salt2: 20);

        var header = new WalFrameHeader(bytes, walChecksums);

        Assert.Equal(3u,  header.PageNumber);
        Assert.Equal(5u,  header.SizeOfDatabaseInPages);
        Assert.Equal(10u, header.VerificationData.Salt1);
        Assert.Equal(20u, header.VerificationData.Salt2);
        Assert.Equal(30u, header.VerificationData.Checksum1);
        Assert.Equal(40u, header.VerificationData.Checksum2);
    }

    [Fact]
    public void WalFrameHeader_IsCurrentWhenSaltsMatch()
    {
        var bytes = MakeFrameHeader(salt1: 42, salt2: 99, checksum1: 1, checksum2: 2);
        // WAL header salts match frame salts — different checksums should not affect IsCurrent
        var walChecksums = MakeVerificationData(salt1: 42, salt2: 99, checksum1: 999, checksum2: 888);

        var header = new WalFrameHeader(bytes, walChecksums);

        Assert.True(header.IsCurrent);
    }

    [Fact]
    public void WalFrameHeader_IsNotCurrentWhenSaltsDiffer()
    {
        var bytes = MakeFrameHeader(salt1: 1, salt2: 2);
        var walChecksums = MakeVerificationData(salt1: 99, salt2: 100);

        var header = new WalFrameHeader(bytes, walChecksums);

        Assert.False(header.IsCurrent);
    }

    [Fact]
    public void WalFrameHeader_ThrowsWhenTooShort()
    {
        var checksums = MakeVerificationData();
        Assert.Throws<InvalidDataException>(() => new WalFrameHeader(new byte[23], checksums));
    }

    [Fact]
    public void WalFrameHeader_ThrowsWhenTooLong()
    {
        var checksums = MakeVerificationData();
        Assert.Throws<InvalidDataException>(() => new WalFrameHeader(new byte[25], checksums));
    }

    // ── WalFrame unit tests ───────────────────────────────────────────────────

    [Fact]
    public void WalFrame_ParsesHeaderAndPageData()
    {
        uint pageSize = 4096;
        var data = new byte[24 + pageSize];
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0), 7); // page number = 7
        data[24] = 0xAB;
        data[24 + (int)pageSize - 1] = 0xCD;
        var checksums = MakeVerificationData();

        var frame = new WalFrame(data, pageSize, TextEncoding.Utf8, 0, checksums);

        Assert.Equal(7u, frame.Header.PageNumber);
        Assert.Equal((int)pageSize, frame.PageData.Length);
        Assert.Equal(0xAB, frame.PageData[0]);
        Assert.Equal(0xCD, frame.PageData[(int)pageSize - 1]);
    }

    [Fact]
    public void WalFrame_ThrowsWhenDataTooSmall()
    {
        uint pageSize = 4096;
        var checksums = MakeVerificationData();
        Assert.Throws<InvalidDataException>(() =>
            new WalFrame(new byte[24 + pageSize - 1], pageSize, TextEncoding.Utf8, 0, checksums));
    }

    // ── WalFile integration tests ─────────────────────────────────────────────

    [Fact]
    public void WalFile_ThrowsOnMissingFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"shard_wal_{Guid.NewGuid():N}_missing.db-wal");
        Assert.Throws<FileNotFoundException>(() => new WalFile(path, TextEncoding.Utf8, 0));
    }

    [Fact]
    public void WalFile_ParsesHeaderFromRealWalFile()
    {
        string dbPath = CreateWalDatabase();
        string walPath = dbPath + "-wal";
        try
        {
            Assert.True(File.Exists(walPath), "WAL file was not created");

            var wal = new WalFile(walPath, TextEncoding.Utf8, 0);

            Assert.True(wal.Header.MagicNumber is 0x377f0682u or 0x377f0683u);
            Assert.Equal(4096u, wal.Header.DatabasePageSize);
        }
        finally { DeleteWalDatabase(dbPath); }
    }

    [Fact]
    public void WalFile_ReadsFramesFromRealWalFile()
    {
        string dbPath = CreateWalDatabase();
        string walPath = dbPath + "-wal";
        try
        {
            Assert.True(File.Exists(walPath), "WAL file was not created");

            var wal = new WalFile(walPath, TextEncoding.Utf8, 0);

            Assert.NotEmpty(wal.Frames);
            foreach (var frame in wal.Frames)
            {
                Assert.True(frame.Header.PageNumber > 0);
                Assert.Equal((int)wal.Header.DatabasePageSize, frame.PageData.Length);
            }
        }
        finally { DeleteWalDatabase(dbPath); }
    }

    [Fact]
    public void WalFile_AllFramesFromRealWalAreCurrentByDefault()
    {
        string dbPath = CreateWalDatabase();
        string walPath = dbPath + "-wal";
        try
        {
            Assert.True(File.Exists(walPath), "WAL file was not created");

            var wal = new WalFile(walPath, TextEncoding.Utf8, 0);

            Assert.All(wal.Frames, f => Assert.True(f.Header.IsCurrent));
        }
        finally { DeleteWalDatabase(dbPath); }
    }

    [Fact]
    public void WalFile_ThrowsOnPartialFrame()
    {
        string walPath = Path.Combine(Path.GetTempPath(), $"shard_wal_{Guid.NewGuid():N}.wal");
        try
        {
            // Valid header followed by a partial frame (not enough bytes for a full frame)
            byte[] header = MakeWalHeader(pageSize: 4096);
            byte[] partial = new byte[10];
            File.WriteAllBytes(walPath, [..header, ..partial]);

            Assert.Throws<InvalidDataException>(() => new WalFile(walPath, TextEncoding.Utf8, 0));
        }
        finally { if (File.Exists(walPath)) File.Delete(walPath); }
    }

    // ── Transaction grouping ──────────────────────────────────────────────────
    //
    // Synthetic layout used by the tests below (index: page, commit?):
    //   0: page 2          1: page 5          2: page 2  COMMIT   <- transaction 1 (3 frames)
    //   3: page 9          4: page 9  COMMIT                      <- transaction 2 (2 frames)
    //   5: page 3 (no terminating commit — truncated/uncommitted) <- transaction 3 (1 frame)

    private static string WriteThreeTransactionWal() => WriteSyntheticWalFile(
        (PageNumber: 2u, DbSizeInPages: 0u),
        (PageNumber: 5u, DbSizeInPages: 0u),
        (PageNumber: 2u, DbSizeInPages: 10u),
        (PageNumber: 9u, DbSizeInPages: 0u),
        (PageNumber: 9u, DbSizeInPages: 20u),
        (PageNumber: 3u, DbSizeInPages: 0u));

    [Fact]
    public void GetTransactionStartIndex_ReturnsZero_ForFramesInFirstTransaction()
    {
        string walPath = WriteThreeTransactionWal();
        try
        {
            var wal = new WalFile(walPath, TextEncoding.Utf8, 0);
            Assert.Equal(0, wal.GetTransactionStartIndex(wal.Frames[0]));
            Assert.Equal(0, wal.GetTransactionStartIndex(wal.Frames[1]));
            Assert.Equal(0, wal.GetTransactionStartIndex(wal.Frames[2])); // the commit frame itself
        }
        finally { File.Delete(walPath); }
    }

    [Fact]
    public void GetTransactionStartIndex_ReturnsIndexAfterPreviousCommit_ForLaterTransactions()
    {
        string walPath = WriteThreeTransactionWal();
        try
        {
            var wal = new WalFile(walPath, TextEncoding.Utf8, 0);
            Assert.Equal(3, wal.GetTransactionStartIndex(wal.Frames[3]));
            Assert.Equal(3, wal.GetTransactionStartIndex(wal.Frames[4]));
            Assert.Equal(5, wal.GetTransactionStartIndex(wal.Frames[5]));
        }
        finally { File.Delete(walPath); }
    }

    [Fact]
    public void GetTransactionFrames_ReturnsAllFramesUpToAndIncludingTheCommitFrame()
    {
        string walPath = WriteThreeTransactionWal();
        try
        {
            var wal = new WalFile(walPath, TextEncoding.Utf8, 0);

            var fromFirstFrame = wal.GetTransactionFrames(wal.Frames[0]);
            Assert.Equal(3, fromFirstFrame.Count);
            Assert.Same(wal.Frames[0], fromFirstFrame[0]);
            Assert.Same(wal.Frames[2], fromFirstFrame[2]);

            // Selecting any frame within the transaction returns the same full set.
            var fromMiddleFrame = wal.GetTransactionFrames(wal.Frames[1]);
            Assert.Equal(fromFirstFrame, fromMiddleFrame);

            var secondTransaction = wal.GetTransactionFrames(wal.Frames[3]);
            Assert.Equal(2, secondTransaction.Count);
            Assert.Same(wal.Frames[3], secondTransaction[0]);
            Assert.Same(wal.Frames[4], secondTransaction[1]);
        }
        finally { File.Delete(walPath); }
    }

    [Fact]
    public void GetTransactionFrames_StopsAtEndOfFile_WhenTruncatedWithoutACommitFrame()
    {
        string walPath = WriteThreeTransactionWal();
        try
        {
            var wal = new WalFile(walPath, TextEncoding.Utf8, 0);

            var thirdTransaction = wal.GetTransactionFrames(wal.Frames[5]);
            Assert.Single(thirdTransaction);
            Assert.Same(wal.Frames[5], thirdTransaction[0]);
        }
        finally { File.Delete(walPath); }
    }

    [Fact]
    public void GetLastFrameForPage_FindsTheMostRecentWriteBeforeTheGivenIndex()
    {
        string walPath = WriteThreeTransactionWal();
        try
        {
            var wal = new WalFile(walPath, TextEncoding.Utf8, 0);

            // Page 2 is written at indices 0 and 2. Searching before index 2 finds index 0;
            // searching before index 3 (i.e. including index 2) finds index 2.
            Assert.Same(wal.Frames[0], wal.GetLastFrameForPage(2, beforeIndex: 2));
            Assert.Same(wal.Frames[2], wal.GetLastFrameForPage(2, beforeIndex: 3));
            Assert.Null(wal.GetLastFrameForPage(2, beforeIndex: 0));
        }
        finally { File.Delete(walPath); }
    }

    [Fact]
    public void GetPreviousFrame_FindsAnEarlierOccurrenceOfTheSamePage_EvenWithinTheSameTransaction()
    {
        string walPath = WriteThreeTransactionWal();
        try
        {
            var wal = new WalFile(walPath, TextEncoding.Utf8, 0);

            // Frame 2 is page 2's second write, inside the same transaction as frame 0.
            Assert.Same(wal.Frames[0], wal.GetPreviousFrame(wal.Frames[2]));
            Assert.Null(wal.GetPreviousFrame(wal.Frames[0]));
        }
        finally { File.Delete(walPath); }
    }
}
