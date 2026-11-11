using System.Buffers.Binary;
using Microsoft.Data.Sqlite;
using SHARD.Core.Enums;
using SHARD.Core.Journal;

namespace SHARD.Core.Tests;

public class RollbackJournalParsingTests
{
    private const int PageSize   = 512;
    private const int SectorSize = 512;

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static readonly byte[] Magic = [0xd9, 0xd5, 0x05, 0xf9, 0x20, 0xa1, 0x63, 0xd7];

    /// <summary>A journal header padded out to a full sector, as SQLite writes it.</summary>
    private static byte[] MakeHeader(
        uint pageCount = 1,
        uint nonce = 0x11111111,
        int dbSize = 10,
        int sectorSize = SectorSize,
        int pageSize = PageSize,
        int paddedTo = SectorSize)
    {
        var bytes = new byte[paddedTo];
        Magic.CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8),  pageCount);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(12), nonce);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16),  dbSize);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20),  sectorSize);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(24),  pageSize);
        return bytes;
    }

    /// <summary>Page content whose sampled bytes are non-zero, so the checksum depends on the data.</summary>
    private static byte[] MakePageData(byte fill, int pageSize = PageSize)
    {
        var data = new byte[pageSize];
        Array.Fill(data, fill);
        return data;
    }

    /// <summary>A page record: page number, page data, checksum.</summary>
    private static byte[] MakeRecord(uint pageNumber, uint nonce, byte fill = 0x2A,
        int pageSize = PageSize, bool corruptChecksum = false)
    {
        byte[] pageData = MakePageData(fill, pageSize);
        uint checksum = RollbackJournalPageRecord.ComputeChecksum(pageData, nonce);
        if (corruptChecksum) checksum ^= 0xFFFFFFFF;

        var bytes = new byte[pageSize + 8];
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0), pageNumber);
        pageData.CopyTo(bytes, 4);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(pageSize + 4), checksum);
        return bytes;
    }

    /// <summary>Zero bytes taking <paramref name="length"/> up to the next sector boundary.</summary>
    private static byte[] PadToSector(int length, int sectorSize = SectorSize) =>
        new byte[(sectorSize - length % sectorSize) % sectorSize];

    private static string WriteJournal(params byte[][] parts)
    {
        string path = Path.Combine(Path.GetTempPath(), $"shard_journal_{Guid.NewGuid():N}.db-journal");
        File.WriteAllBytes(path, parts.SelectMany(p => p).ToArray());
        return path;
    }

    private static RollbackJournalFile Parse(string path) =>
        new(path, TextEncoding.Utf8, 0);

    private static IEnumerable<RollbackJournalPageRecord> AllRecords(RollbackJournalFile journal) =>
        journal.Segments.SelectMany(s => s.PageRecords);

    // ── Checksum ─────────────────────────────────────────────────────────────

    [Fact]
    public void ComputeChecksum_AddsEvery200thByteCountingDownAndSkipsOffsetZero()
    {
        // 512-byte page samples offsets 312 and 112 only
        var data = new byte[512];
        data[312] = 5;
        data[112] = 7;
        data[0]   = 100;   // never sampled
        data[313] = 100;   // not on a sample point

        Assert.Equal(1000u + 5 + 7, RollbackJournalPageRecord.ComputeChecksum(data, 1000));
    }

    [Fact]
    public void ComputeChecksum_WrapsAsUnsigned()
    {
        var data = new byte[512];
        data[312] = 2;
        Assert.Equal(1u, RollbackJournalPageRecord.ComputeChecksum(data, uint.MaxValue));
    }

    // ── Header ───────────────────────────────────────────────────────────────

    [Fact]
    public void Header_ParsesAllFields()
    {
        var header = new RollbackJournalHeader(MakeHeader(
            pageCount: 7, nonce: 0xDEADBEEF, dbSize: 42, sectorSize: 4096, pageSize: 1024, paddedTo: 28));

        Assert.Equal(7u,          header.PageCount);
        Assert.Equal(0xDEADBEEFu, header.Nonce);
        Assert.Equal(42,          header.DatabaseSize);
        Assert.Equal(4096,        header.SizeOfDiskSector);
        Assert.Equal(1024,        header.SizeOfJournalPages);
    }

    [Fact]
    public void Header_ReadsPageCountAsUnsigned()
    {
        var header = new RollbackJournalHeader(MakeHeader(pageCount: 0xFFFFFFFF, paddedTo: 28));
        Assert.Equal(uint.MaxValue, header.PageCount);
    }

    [Fact]
    public void Header_ThrowsOnInvalidMagic()
    {
        byte[] data = MakeHeader(paddedTo: 28);
        data[0] = 0;
        Assert.Throws<InvalidDataException>(() => new RollbackJournalHeader(data));
    }

    [Fact]
    public void Header_ThrowsWhenTooShort()
    {
        Assert.Throws<InvalidDataException>(() => new RollbackJournalHeader(MakeHeader(paddedTo: 28)[..27]));
    }

    // ── Single segment ───────────────────────────────────────────────────────

    [Fact]
    public void File_ParsesValidRecordsInOneSegment()
    {
        const uint nonce = 0x12345678;
        string path = WriteJournal(
            MakeHeader(pageCount: 2, nonce: nonce),
            MakeRecord(2, nonce),
            MakeRecord(3, nonce));
        try
        {
            var journal = Parse(path);

            var segment = Assert.Single(journal.Segments);
            Assert.Equal(0, segment.SegmentStart);
            Assert.Equal(2, segment.PageRecords.Count);
            Assert.Equal([2u, 3u], segment.PageRecords.Select(r => r.PageNumber));
            Assert.All(segment.PageRecords, r =>
            {
                Assert.Equal(JournalRecordStatus.Valid, r.Status);
                Assert.True(r.IsChecksumValid);
                Assert.Equal(nonce, r.ImpliedNonce);
            });
            Assert.Equal(SectorSize,                segment.PageRecords[0].FileOffset);
            Assert.Equal(SectorSize + PageSize + 8, segment.PageRecords[1].FileOffset);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void File_RecordWithBadChecksumIsStaleAndStopsPlayback()
    {
        const uint nonce = 0x0BADF00D;
        string path = WriteJournal(
            MakeHeader(pageCount: 3, nonce: nonce),
            MakeRecord(2, nonce),
            MakeRecord(3, nonce, corruptChecksum: true),
            MakeRecord(4, nonce));
        try
        {
            var statuses = AllRecords(Parse(path)).Select(r => r.Status).ToList();

            Assert.Equal(
                [JournalRecordStatus.Valid, JournalRecordStatus.Stale, JournalRecordStatus.AfterPlaybackStopped],
                statuses);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void File_PageNumberZeroIsInvalid()
    {
        const uint nonce = 1;
        string path = WriteJournal(MakeHeader(pageCount: 1, nonce: nonce), MakeRecord(0, nonce));
        try
        {
            Assert.Equal(JournalRecordStatus.InvalidPageNumber, AllRecords(Parse(path)).Single().Status);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void File_LockBytePageIsInvalid()
    {
        const uint nonce = 1;
        uint lockBytePage = 0x40000000 / PageSize + 1;
        string path = WriteJournal(MakeHeader(pageCount: 1, nonce: nonce), MakeRecord(lockBytePage, nonce));
        try
        {
            Assert.Equal(JournalRecordStatus.InvalidPageNumber, AllRecords(Parse(path)).Single().Status);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(0xFFFFFFFFu)]   // count never written (no-sync / safe-append)
    [InlineData(0u)]            // count never filled in
    public void File_UncountedSegmentReadsEveryWholeRecord(uint pageCount)
    {
        const uint nonce = 7;
        string path = WriteJournal(
            MakeHeader(pageCount: pageCount, nonce: nonce),
            MakeRecord(2, nonce),
            MakeRecord(3, nonce),
            MakeRecord(4, nonce),
            new byte[100]);   // partial record at the end
        try
        {
            var records = AllRecords(Parse(path)).ToList();

            Assert.Equal(3, records.Count);
            Assert.All(records, r => Assert.Equal(JournalRecordStatus.Valid, r.Status));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void File_PageCountLargerThanFileIsLimitedToWholeRecords()
    {
        const uint nonce = 7;
        string path = WriteJournal(
            MakeHeader(pageCount: 0x80000000, nonce: nonce),
            MakeRecord(2, nonce),
            MakeRecord(3, nonce));
        try
        {
            Assert.Equal(2, AllRecords(Parse(path)).Count());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void File_TruncatedRecordIsNotParsed()
    {
        const uint nonce = 7;
        byte[] second = MakeRecord(3, nonce);
        string path = WriteJournal(
            MakeHeader(pageCount: 2, nonce: nonce),
            MakeRecord(2, nonce),
            second[..200]);
        try
        {
            var record = Assert.Single(AllRecords(Parse(path)));
            Assert.Equal(2u, record.PageNumber);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void File_SetsStopReasonWhenNoHeaderFollowsTheLastSegment()
    {
        const uint nonce = 7;
        byte[] header = MakeHeader(pageCount: 1, nonce: nonce);
        byte[] record = MakeRecord(2, nonce);
        string path = WriteJournal(header, record, PadToSector(header.Length + record.Length),
            new byte[SectorSize]);   // a sector of slack where a header would be
        try
        {
            var journal = Parse(path);

            Assert.Single(journal.Segments);
            Assert.Contains($"offset {3 * SectorSize}", journal.StopReason);
        }
        finally { File.Delete(path); }
    }

    // ── Multiple segments ────────────────────────────────────────────────────

    [Fact]
    public void File_ParsesEachSegmentWithItsOwnNonce()
    {
        const uint nonce1 = 0xAAAA0001, nonce2 = 0xBBBB0002;
        byte[] header1 = MakeHeader(pageCount: 1, nonce: nonce1);
        byte[] record1 = MakeRecord(2, nonce1);
        string path = WriteJournal(
            header1, record1, PadToSector(header1.Length + record1.Length),
            MakeHeader(pageCount: 1, nonce: nonce2),
            MakeRecord(3, nonce2));
        try
        {
            var journal = Parse(path);

            Assert.Equal(2, journal.Segments.Count);
            Assert.Equal(0,              journal.Segments[0].SegmentStart);
            Assert.Equal(3 * SectorSize, journal.Segments[1].SegmentStart);
            Assert.Equal(nonce1, journal.Segments[0].Header.Nonce);
            Assert.Equal(nonce2, journal.Segments[1].Header.Nonce);
            Assert.All(AllRecords(journal), r => Assert.Equal(JournalRecordStatus.Valid, r.Status));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void File_FailureInOneSegmentStopsPlaybackForLaterSegments()
    {
        const uint nonce1 = 1, nonce2 = 2;
        byte[] header1 = MakeHeader(pageCount: 1, nonce: nonce1);
        byte[] record1 = MakeRecord(2, nonce1, corruptChecksum: true);
        string path = WriteJournal(
            header1, record1, PadToSector(header1.Length + record1.Length),
            MakeHeader(pageCount: 1, nonce: nonce2),
            MakeRecord(3, nonce2));
        try
        {
            var statuses = AllRecords(Parse(path)).Select(r => r.Status).ToList();

            Assert.Equal([JournalRecordStatus.Stale, JournalRecordStatus.AfterPlaybackStopped], statuses);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void File_LaterHeaderSizeFieldsAreIgnored()
    {
        const uint nonce1 = 1, nonce2 = 2;
        byte[] header1 = MakeHeader(pageCount: 1, nonce: nonce1);
        byte[] record1 = MakeRecord(2, nonce1);
        string path = WriteJournal(
            header1, record1, PadToSector(header1.Length + record1.Length),
            MakeHeader(pageCount: 1, nonce: nonce2, sectorSize: 1000, pageSize: 7),
            MakeRecord(3, nonce2));
        try
        {
            var journal = Parse(path);

            Assert.Equal(2, journal.Segments.Count);
            Assert.Equal(JournalRecordStatus.Valid, journal.Segments[1].PageRecords.Single().Status);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void File_StaleRecordsFromTheSameTransactionShareAnImpliedNonce()
    {
        const uint current = 0x10, earlier = 0xCAFEBABE;
        string path = WriteJournal(
            MakeHeader(pageCount: 0xFFFFFFFF, nonce: current),
            MakeRecord(2, current),
            MakeRecord(5, earlier, fill: 0x11),
            MakeRecord(6, earlier, fill: 0x22));
        try
        {
            var stale = AllRecords(Parse(path)).Where(r => r.Status == JournalRecordStatus.Stale).ToList();

            Assert.Equal(2, stale.Count);
            Assert.All(stale, r => Assert.Equal(earlier, r.ImpliedNonce));
        }
        finally { File.Delete(path); }
    }

    // ── Invalid files ────────────────────────────────────────────────────────

    [Fact]
    public void File_ThrowsWhenTooShortForAHeader()
    {
        string path = WriteJournal(new byte[27]);
        try { Assert.Throws<InvalidDataException>(() => Parse(path)); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void File_ThrowsWhenFirstHeaderIsZeroed()
    {
        // what a PERSIST-mode journal looks like after a commit
        const uint nonce = 1;
        string path = WriteJournal(new byte[SectorSize], MakeRecord(2, nonce));
        try { Assert.Throws<InvalidDataException>(() => Parse(path)); }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(SectorSize, 256)]     // page size below 512
    [InlineData(SectorSize, 1000)]    // page size not a power of two
    [InlineData(16, PageSize)]        // sector size below 32
    [InlineData(131072, PageSize)]    // sector size above 65536
    public void File_ThrowsOnInvalidSizesInFirstHeader(int sectorSize, int pageSize)
    {
        string path = WriteJournal(MakeHeader(sectorSize: sectorSize, pageSize: pageSize));
        try { Assert.Throws<InvalidDataException>(() => Parse(path)); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void File_AcceptsSmallSectorSize()
    {
        const uint nonce = 1;
        string path = WriteJournal(MakeHeader(pageCount: 1, nonce: nonce, sectorSize: 32, paddedTo: 32),
            MakeRecord(2, nonce));
        try
        {
            var record = AllRecords(Parse(path)).Single();
            Assert.Equal(32, record.FileOffset);
            Assert.Equal(JournalRecordStatus.Valid, record.Status);
        }
        finally { File.Delete(path); }
    }

    // ── Real SQLite journal ──────────────────────────────────────────────────

    [Fact]
    public void File_ParsesJournalWrittenBySqlite()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"shard_journal_{Guid.NewGuid():N}.db");
        string copyPath = dbPath + "-journal-copy";
        try
        {
            using (var conn = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    PRAGMA page_size=4096;
                    PRAGMA journal_mode=DELETE;
                    CREATE TABLE t (id INTEGER PRIMARY KEY, value TEXT);
                    """;
                cmd.ExecuteNonQuery();

                using (var fill = conn.BeginTransaction())
                {
                    cmd.Transaction = fill;
                    cmd.CommandText = "INSERT INTO t (value) VALUES (printf('%.300c', 'x'))";
                    for (int i = 0; i < 200; i++) cmd.ExecuteNonQuery();
                    fill.Commit();
                }

                // a tiny cache forces SQLite to spill mid-transaction, which syncs the journal
                // (giving it valid checksums) and starts a new segment for each spill
                cmd.Transaction = null;
                cmd.CommandText = "PRAGMA cache_size=1";
                cmd.ExecuteNonQuery();
                using var update = conn.BeginTransaction();
                cmd.Transaction = update;
                cmd.CommandText = "UPDATE t SET value = 'changed'";
                cmd.ExecuteNonQuery();

                File.Copy(dbPath + "-journal", copyPath);
                update.Rollback();
            }

            var journal = Parse(copyPath);
            var records = AllRecords(journal).ToList();

            Assert.True(journal.Segments.Count > 1);
            Assert.NotEmpty(records);
            Assert.All(journal.Segments, s => Assert.Equal(4096, s.Header.SizeOfJournalPages));
            Assert.All(records, r => Assert.Equal(JournalRecordStatus.Valid, r.Status));
            Assert.Equal(records.Count, records.Select(r => r.PageNumber).Distinct().Count());
        }
        finally
        {
            foreach (string p in new[] { dbPath, dbPath + "-journal", copyPath })
                if (File.Exists(p)) File.Delete(p);
        }
    }
}
