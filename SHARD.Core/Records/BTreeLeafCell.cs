using SHARD.Core.Decoding;

namespace SHARD.Core.Records;

public class BTreeLeafCell
{
    public Varint SizeOfPayload { get; }
    public Varint RowId { get; }
    public Varint HeaderSize { get; }
    public List<HeaderEntry> HeaderEntries { get; } = new();

    public int OverflowPage = 0;

    public BTreeLeafCell(byte[] data, Varint PayloadSize)
    {
        int offset = 0;
        SizeOfPayload = new Varint(data.AsSpan(offset..9));
        // Check payload sizes match
        if (!SizeOfPayload.Equals(PayloadSize))
        {
            throw new InvalidDataException("Size Of Payload does not matched passed version");
        }
        offset += SizeOfPayload.Length;
        RowId = new Varint(data.AsSpan(offset..(offset + 9)));
        offset += RowId.Length;
        HeaderSize = new Varint(data.AsSpan(offset..(offset + 9)));
        //Need to decode header size, includes varint of size in length
        var headerOffset = HeaderSize.Length;
        while (headerOffset < HeaderSize.Value)
        {
            Varint temp = new Varint(data.AsSpan(offset + headerOffset, Math.Min(9, data.Length -offset -headerOffset)));
            HeaderEntries.Add(new HeaderEntry(temp));
            headerOffset += temp.Length;
        }

    }

    
}