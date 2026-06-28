namespace SHARD.Core.WAL;

public class WalFrame
{
    public WalFrameHeader Header { get; }
    public byte[] PageData { get; }

    public WalFrame(ReadOnlySpan<byte> data, uint pageSize)
    {
        //check data is equal to 24 + minimum page size (512)
        if (data.Length < 24 + pageSize)
        {
            throw new InvalidDataException("Data is smaller than required header size and minimum page size");
        }
        Header = new WalFrameHeader(data[0..24]);
        PageData = data[24..(int)(pageSize + 24)].ToArray();
    }
    
}