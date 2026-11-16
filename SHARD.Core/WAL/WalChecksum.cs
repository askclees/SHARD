using System.Buffers.Binary;

namespace SHARD.Core.WAL;

/// <summary>
/// SQLite's WAL checksum (walChecksumBytes in wal.c): a running pair of 32-bit sums over the data
/// read as 32-bit words, two at a time. Words are big-endian when the WAL magic number is
/// 0x377f0683 and little-endian when it is 0x377f0682. Arithmetic wraps as uint.
/// </summary>
public static class WalChecksum
{
    /// <summary>
    /// Continues the checksum from (<paramref name="s0"/>, <paramref name="s1"/>) over
    /// <paramref name="data"/>, whose length must be a multiple of 8.
    /// </summary>
    public static (uint S0, uint S1) Compute(ReadOnlySpan<byte> data, bool bigEndian, uint s0 = 0, uint s1 = 0)
    {
        if (data.Length % 8 != 0)
            throw new ArgumentException("WAL checksum data length must be a multiple of 8.", nameof(data));

        for (int i = 0; i < data.Length; i += 8)
        {
            uint x0 = bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(data[i..])       : BinaryPrimitives.ReadUInt32LittleEndian(data[i..]);
            uint x1 = bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(data[(i + 4)..]) : BinaryPrimitives.ReadUInt32LittleEndian(data[(i + 4)..]);
            s0 += x0 + s1;
            s1 += x1 + s0;
        }
        return (s0, s1);
    }

    /// <summary>
    /// A frame's checksum: continues from the previous frame's (or, for the first frame, the
    /// header's) checksum over the first 8 bytes of the frame header and then the page data.
    /// </summary>
    public static (uint S0, uint S1) ComputeFrame(
        uint pageNumber, uint sizeOfDatabaseInPages, ReadOnlySpan<byte> pageData, bool bigEndian, uint s0, uint s1)
    {
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(header,       pageNumber);
        BinaryPrimitives.WriteUInt32BigEndian(header[4..],  sizeOfDatabaseInPages);
        (s0, s1) = Compute(header, bigEndian, s0, s1);
        return Compute(pageData, bigEndian, s0, s1);
    }
}

/// <summary>How SQLite treats a WAL frame when it opens the WAL (walIndexRecover in wal.c).</summary>
public enum WalFrameStatus
{
    /// <summary>Valid, and at or before the last commit frame of the valid run — SQLite reads it.</summary>
    Committed,

    /// <summary>Valid, but no commit frame follows it within the valid run (a transaction that never
    /// committed, or was cut off) — SQLite ignores it.</summary>
    Uncommitted,

    /// <summary>Salts match the header but the checksum doesn't continue the chain — SQLite stops
    /// reading the WAL here, ignoring this frame and everything after it.</summary>
    InvalidChecksum,

    /// <summary>Page number 0, which SQLite never writes — SQLite stops reading the WAL here.</summary>
    InvalidPageNumber,

    /// <summary>Salts match the header, but the frame comes after the point where SQLite stopped
    /// reading (or the header checksum itself is invalid, so SQLite ignores the whole WAL).</summary>
    AfterInvalid,

    /// <summary>Salts differ from the header: left over from before the WAL was last reset (after a
    /// checkpoint) and not yet overwritten. SQLite ignores it; its content is earlier history.</summary>
    OldGeneration,
}
