using SHARD.Core.Enums;
using SHARD.Core.Pages;

namespace SHARD.Core.WAL;

public class WalFrame
{
    public WalFrameHeader Header { get; }
    public byte[] PageData { get; }
    public SqlitePage Page { get; }

    /// <summary>How SQLite treats this frame; set by <see cref="WalFile"/> once every frame is read.</summary>
    public WalFrameStatus Status { get; internal set; }

    /// <summary>
    /// Whether this frame's checksum continues the chain from the frame before it (or, for the
    /// first frame, from the header). Null when there's nothing to check it against: an
    /// old-generation frame whose predecessor belongs to a different generation, since the frame
    /// it was originally chained to has been overwritten.
    /// </summary>
    public bool? ChecksumValid { get; internal set; }

    public WalFrame(ReadOnlySpan<byte> data, uint pageSize, TextEncoding encoding, int reservedBytes,
        VerificationData checksums, PageLayout? layout = null)
    {
        if (data.Length < 24 + pageSize)
        {
            throw new InvalidDataException("Data is smaller than required header size and minimum page size");
        }
        Header = new WalFrameHeader(data[0..24], checksums);
        PageData = data[24..(int)(pageSize + 24)].ToArray();
        Page = SqlitePage.FromBytes(Header.PageNumber, (int)pageSize, PageData, encoding, reservedBytes, layout);
    }

}