using System.Buffers.Binary;
using SHARD.Core.Enums;
using SHARD.Core.Pages;

namespace SHARD.Core.Journal;

public class RollbackJournalPageRecord
{
    public uint PageNumber { get; set; }
    public SqlitePage Page { get; set; }
    public uint Checksum { get; set; }
    //checksum calculated from the page data using the header nonce
    public uint ComputedChecksum { get; }
    public bool IsChecksumValid => Checksum == ComputedChecksum;
    //nonce the record's checksum was written with - records from the same transaction share one
    public uint ImpliedNonce { get; }
    public long FileOffset { get; }
    public JournalRecordStatus Status { get; internal set; }

    public RollbackJournalPageRecord(ReadOnlySpan<byte> data, uint pageSize, TextEncoding encoding, int reservedBytes, uint nonce, long fileOffset)
    {
        if (data.Length != pageSize + 8)
        {
            throw new InvalidDataException("Byte array should be pagesize + 8 bytes");
        }
        FileOffset = fileOffset;
        PageNumber = BinaryPrimitives.ReadUInt32BigEndian(data[0..4]);
        int endOfData = (int)pageSize + 4;
        ReadOnlySpan<byte> pageData = data[4..endOfData];
        Page = SqlitePage.FromBytes(PageNumber, (int)pageSize, pageData.ToArray(), encoding, reservedBytes);
        Checksum = BinaryPrimitives.ReadUInt32BigEndian(data[endOfData..(endOfData + 4)]);
        ComputedChecksum = ComputeChecksum(pageData, nonce);
        ImpliedNonce = Checksum - (ComputedChecksum - nonce);
    }

    /// <summary>
    /// SQLite's journal record checksum (pager_cksum): the nonce plus every 200th byte of the
    /// page, counting down from pageSize - 200 and excluding offset 0. Wraps as a uint.
    /// </summary>
    public static uint ComputeChecksum(ReadOnlySpan<byte> pageData, uint nonce)
    {
        uint checksum = nonce;
        for (int i = pageData.Length - 200; i > 0; i -= 200)
            checksum += pageData[i];
        return checksum;
    }
}
