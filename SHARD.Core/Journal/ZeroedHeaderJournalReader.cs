using System.Buffers.Binary;
using SHARD.Core.Enums;

namespace SHARD.Core.Journal;

/// <summary>
/// Reads a rollback journal whose header SQLite zeroed when its transaction committed
/// (journal_mode=PERSIST zeroes the first 28 bytes and leaves the records in place). Opt-in and
/// experimental: it is only used when the caller asks for it, and every failure — including an
/// unexpected exception — is returned as an error rather than thrown, so a problem here can't
/// affect anything else.
/// <para>The header's values are inferred:</para>
/// <list type="bullet">
/// <item>Page size: from the database, since a journal's page size must match it.</item>
/// <item>Sector size (where the records start, as the header is padded to one sector): the
/// journal is one header sector followed by whole records of page size + 8 bytes, so it is
/// <c>length mod (page size + 8)</c>. If that isn't a valid sector size, or no valid record starts
/// there (a multi-segment, truncated or carved file), each power of two from 512 to 65536 is tried.</item>
/// <item>Record count: whole records are read until one no longer looks like a journal record.</item>
/// <item>Nonce: a record's checksum is the nonce plus a sum of sampled page bytes, so each record
/// implies the nonce it was written with. Records of one transaction share it, which both
/// validates them and splits the file into transactions: the last transaction's records, then
/// any left over from an older, larger one (PERSIST mode never truncates the file).</item>
/// </list>
/// A record is accepted if its page number fits the database and either its data looks like a
/// SQLite page (a B-tree page, or page 1's header) or it shares the previous record's nonce.
/// </summary>
public static class ZeroedHeaderJournalReader
{
    private const int HeaderLength = 28;
    private static readonly byte[] Page1Magic = "SQLite format 3\0"u8.ToArray();

    public static RollbackJournalFile? TryRead(
        string path, int pageSize, uint databasePageCount, TextEncoding encoding, int reservedBytes, out string? error)
    {
        try
        {
            return Read(path, pageSize, databasePageCount, encoding, reservedBytes, out error);
        }
        catch (Exception ex)
        {
            error = $"reading the journal with an inferred header failed: {ex.Message}";
            return null;
        }
    }

    private static RollbackJournalFile? Read(
        string path, int pageSize, uint databasePageCount, TextEncoding encoding, int reservedBytes, out string? error)
    {
        byte[] data;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            data = new byte[stream.Length];
            stream.ReadExactly(data);
        }

        if (data.Length < HeaderLength || data.AsSpan(0, HeaderLength).ContainsAnyExcept((byte)0))
        {
            error = "the header isn't zeroed, so this isn't a PERSIST-mode journal";
            return null;
        }

        int stride = pageSize + 8;
        uint lockBytePage = (uint)(0x40000000 / pageSize) + 1;
        // Older transactions can reference pages past the database's current end (it may have shrunk)
        uint maxPage = Math.Max(databasePageCount * 2, databasePageCount + 1024);

        bool PlausiblePageNumber(uint pn) => pn >= 1 && pn != lockBytePage && pn <= maxPage;

        bool LooksLikePage(uint pn, ReadOnlySpan<byte> page) => pn == 1
            ? page.StartsWith(Page1Magic)
            : page[0] is 0x02 or 0x05 or 0x0A or 0x0D;

        bool ValidRecordAt(long offset)
        {
            if (offset + stride > data.Length) return false;
            uint pn = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan((int)offset, 4));
            return PlausiblePageNumber(pn) && LooksLikePage(pn, data.AsSpan((int)offset + 4, pageSize));
        }

        // ── Sector size ──
        int fromLength = data.Length % stride;
        int? sectorSize = null;
        string how = "";
        if (IsSectorSize(fromLength) && ValidRecordAt(fromLength))
        {
            sectorSize = fromLength;
            how = "from the journal's length";
        }
        else
        {
            for (int candidate = 512; candidate <= 65536; candidate <<= 1)
            {
                if (candidate == fromLength || !ValidRecordAt(candidate)) continue;
                sectorSize = candidate;
                how = "by finding the first valid record (the length didn't give it)";
                break;
            }
        }
        if (sectorSize is not { } sector)
        {
            error = "no journal record found at any sector boundary";
            return null;
        }

        // ── Records, grouped into transactions by nonce ──
        var groups = new List<(uint Nonce, List<RollbackJournalPageRecord> Records, long Start)>();
        long offsetNow = sector;
        string? stopReason = null;
        while (offsetNow + stride <= data.Length)
        {
            var span = data.AsSpan((int)offsetNow, stride);
            uint pn = BinaryPrimitives.ReadUInt32BigEndian(span);
            var pageBytes = span.Slice(4, pageSize);
            uint checksum = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(4 + pageSize, 4));
            uint nonce = checksum - RollbackJournalPageRecord.ComputeChecksum(pageBytes, 0);

            bool sharesNonce = groups.Count > 0 && groups[^1].Nonce == nonce;
            if (!PlausiblePageNumber(pn) || !(LooksLikePage(pn, pageBytes) || sharesNonce))
            {
                stopReason = $"the record at offset {offsetNow:N0} doesn't look like a journal record";
                break;
            }

            var record = new RollbackJournalPageRecord(span, (uint)pageSize, encoding, reservedBytes, nonce, offsetNow)
            {
                Status = JournalRecordStatus.ZeroedHeader,
            };
            if (!sharesNonce) groups.Add((nonce, [], offsetNow));
            groups[^1].Records.Add(record);
            offsetNow += stride;
        }
        if (stopReason is null && offsetNow < data.Length)
            stopReason = $"{data.Length - offsetNow:N0} bytes at offset {offsetNow:N0} are less than a whole record";

        int recordCount = groups.Sum(g => g.Records.Count);
        if (recordCount == 0)
        {
            error = "the header is zeroed but no records follow it";
            return null;
        }

        var segments = groups.Select(g => new RollbackJournalSegment(
                new RollbackJournalHeader((uint)g.Records.Count, g.Nonce, 0, sector, pageSize),
                g.Records, g.Start, g.Start + (long)g.Records.Count * stride))
            .ToList();

        string inference =
            $"header zeroed (journal_mode=PERSIST): page size {pageSize:N0} from the database; " +
            $"sector size {sector:N0} {how}; {recordCount:N0} record{(recordCount == 1 ? "" : "s")} in " +
            $"{groups.Count} transaction{(groups.Count == 1 ? "" : "s")}, grouped by checksum nonce" +
            (groups.Count > 1 ? " (the first is the last to commit; later ones are left over from older, larger transactions)" : "");

        error = null;
        return new RollbackJournalFile(segments, inference, stopReason);
    }

    private static bool IsSectorSize(int n) => n >= 512 && n <= 65536 && (n & (n - 1)) == 0;
}
