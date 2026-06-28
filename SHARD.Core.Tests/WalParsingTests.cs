using System.Buffers.Binary;
using Microsoft.Data.Sqlite;
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
        Assert.Equal(11u,         header.Salt1);
        Assert.Equal(22u,         header.Salt2);
        Assert.Equal(33u,         header.Checksum1);
        Assert.Equal(44u,         header.Checksum2);
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

        var header = new WalFrameHeader(bytes);

        Assert.Equal(3u,  header.PageNumber);
        Assert.Equal(5u,  header.SizeOfDatabaseInPages);
        Assert.Equal(10u, header.Salt1);
        Assert.Equal(20u, header.Salt2);
        Assert.Equal(30u, header.Checksum1);
        Assert.Equal(40u, header.Checksum2);
    }

    [Fact]
    public void WalFrameHeader_ThrowsWhenTooShort()
    {
        Assert.Throws<InvalidDataException>(() => new WalFrameHeader(new byte[23]));
    }

    [Fact]
    public void WalFrameHeader_ThrowsWhenTooLong()
    {
        Assert.Throws<InvalidDataException>(() => new WalFrameHeader(new byte[25]));
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

        var frame = new WalFrame(data, pageSize);

        Assert.Equal(7u, frame.Header.PageNumber);
        Assert.Equal((int)pageSize, frame.PageData.Length);
        Assert.Equal(0xAB, frame.PageData[0]);
        Assert.Equal(0xCD, frame.PageData[(int)pageSize - 1]);
    }

    [Fact]
    public void WalFrame_ThrowsWhenDataTooSmall()
    {
        uint pageSize = 4096;
        Assert.Throws<InvalidDataException>(() => new WalFrame(new byte[24 + pageSize - 1], pageSize));
    }

    // ── WalFile integration tests ─────────────────────────────────────────────

    [Fact]
    public void WalFile_ThrowsOnMissingFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"shard_wal_{Guid.NewGuid():N}_missing.db-wal");
        Assert.Throws<FileNotFoundException>(() => new WalFile(path));
    }

    [Fact]
    public void WalFile_ParsesHeaderFromRealWalFile()
    {
        string dbPath = CreateWalDatabase();
        string walPath = dbPath + "-wal";
        try
        {
            Assert.True(File.Exists(walPath), "WAL file was not created");

            var wal = new WalFile(walPath);

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

            var wal = new WalFile(walPath);

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
    public void WalFile_ThrowsOnPartialFrame()
    {
        string walPath = Path.Combine(Path.GetTempPath(), $"shard_wal_{Guid.NewGuid():N}.wal");
        try
        {
            // Valid header followed by a partial frame (not enough bytes for a full frame)
            byte[] header = MakeWalHeader(pageSize: 4096);
            byte[] partial = new byte[10];
            File.WriteAllBytes(walPath, [..header, ..partial]);

            Assert.Throws<InvalidDataException>(() => new WalFile(walPath));
        }
        finally { if (File.Exists(walPath)) File.Delete(walPath); }
    }
}
