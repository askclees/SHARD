using SHARD.Core.Enums;
using SHARD.Core.Pages;

namespace SHARD.Core.WAL;

public class WalFrame
{
    public WalFrameHeader Header { get; }
    public byte[] PageData { get; }
    public SqlitePage Page { get; }

    public WalFrame(ReadOnlySpan<byte> data, uint pageSize, TextEncoding encoding, int reservedBytes)
    {
        if (data.Length < 24 + pageSize)
        {
            throw new InvalidDataException("Data is smaller than required header size and minimum page size");
        }
        Header = new WalFrameHeader(data[0..24]);
        PageData = data[24..(int)(pageSize + 24)].ToArray();
        Page = SqlitePage.FromBytes(Header.PageNumber, (int)pageSize, PageData, encoding, reservedBytes);
    }

}