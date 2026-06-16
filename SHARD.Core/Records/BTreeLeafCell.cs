using System.Buffers.Binary;
using System.Text;
using SHARD.Core.Decoding;
using SHARD.Core.Enums;

namespace SHARD.Core.Records;

public class BTreeLeafCell
{
    public Varint SizeOfPayload { get; }
    public Varint RowId { get; }
    public Varint HeaderSize { get; }
    public List<HeaderEntry> HeaderEntries { get; } = new();
    public List<SqliteValue?> FieldValues { get; } = new();

    public uint OverflowPage = 0;

    private readonly byte[] _localData;
    private readonly TextEncoding _encoding;

    /// <summary>
    /// Number of payload bytes still missing from the overflow page chain. 0 once resolved
    /// (or if the record never overflowed).
    /// </summary>
    public int OverflowBytesNeeded =>
        OverflowPage == 0 ? 0 : (int)SizeOfPayload.Value - (_localData.Length - SizeOfPayload.Length - RowId.Length);

    /// <summary>
    /// Total bytes this cell actually occupies on its page: the local payload data plus,
    /// if the record overflows, the trailing 4-byte overflow page pointer. Unlike
    /// <see cref="SizeOfPayload"/>, this never extends past the page — use it for highlighting
    /// the cell's bytes on the page, not the full logical payload length.
    /// </summary>
    public int CellByteLengthOnPage => _localData.Length + (OverflowPage != 0 ? 4 : 0);

    public BTreeLeafCell(byte[] data, Varint payloadSize, TextEncoding encoding, uint overflowPage = 0)
    {
        OverflowPage = overflowPage;
        _localData = data;
        _encoding = encoding;
        int offset = 0;
        SizeOfPayload = Varint.ReadAt(data, offset);
        // Check payload sizes match
        if (!SizeOfPayload.Equals(payloadSize))
        {
            throw new InvalidDataException("Size Of Payload does not matched passed version");
        }
        offset += SizeOfPayload.Length;
        RowId = Varint.ReadAt(data, offset);
        offset += RowId.Length;
        HeaderSize = Varint.ReadAt(data, offset);
        //Need to decode header size, includes varint of size in length
        var headerOffset = HeaderSize.Length;
        while (headerOffset < HeaderSize.Value)
        {
            Varint temp = Varint.ReadAt(data, offset + headerOffset);
            HeaderEntries.Add(new HeaderEntry(temp));
            headerOffset += temp.Length;
        }
        //verify size against values
        var recordLength = HeaderSize.Value;
        foreach (HeaderEntry entry in HeaderEntries)
        {
            recordLength += entry.ContentLength;
        }
        if (recordLength != payloadSize.Value)
        {
            throw new InvalidDataException("Payload does not match size of fields");
        }

        var recordOffset = headerOffset + offset;
        FieldValues.AddRange(DecodeFieldValues(data, recordOffset));
    }

    /// <summary>
    /// Re-decodes field values once the full record bytes (local + overflow chain) are
    /// available, replacing any nulls left by fields that previously spilled past the
    /// locally-stored portion of the payload.
    /// </summary>
    public void ResolveOverflow(byte[] overflowBytes)
    {
        if (OverflowPage == 0) return;

        var fullData = new byte[_localData.Length + overflowBytes.Length];
        _localData.CopyTo(fullData, 0);
        overflowBytes.CopyTo(fullData, _localData.Length);

        var recordOffset = SizeOfPayload.Length + RowId.Length + (int)HeaderSize.Value;
        FieldValues.Clear();
        FieldValues.AddRange(DecodeFieldValues(fullData, recordOffset));
    }

    private List<SqliteValue?> DecodeFieldValues(byte[] data, int recordOffset)
    {
        var values = new List<SqliteValue?>();
        //decode values
        foreach (HeaderEntry entry in HeaderEntries)
        {
            switch (entry.Kind)
            {
                case SerialTypeKind.Null:
                    values.Add(null);
                    break;
                case SerialTypeKind.Int0:
                    values.Add(new SqliteValue(0L,0));
                    break;
                case SerialTypeKind.Int1:
                    values.Add(new SqliteValue(1L,0));
                    break;
                case SerialTypeKind.Integer:
                    if (recordOffset + entry.ContentLength > data.Length)
                    {
                        // Field content spills into the overflow page chain (OverflowPage), not yet followed.
                        values.Add(null);
                        break;
                    }
                    values.Add(GetIntegerValue(data[recordOffset..(recordOffset+entry.ContentLength) ]));
                    break;
                case SerialTypeKind.Text:
                    if (recordOffset + entry.ContentLength > data.Length)
                    {
                        // Field content spills into the overflow page chain (OverflowPage), not yet followed.
                        values.Add(null);
                        break;
                    }
                    ReadOnlySpan<byte> stringByteData = data[recordOffset..(recordOffset + entry.ContentLength)];
                    string stringData = GetTextValue(stringByteData, _encoding);
                    values.Add(new SqliteValue(stringData, entry.ContentLength));
                    break;
                default:
                    values.Add(null);
                    break;
            }
            recordOffset += entry.ContentLength;
        }
        return values;
    }

    private static SqliteValue GetIntegerValue(ReadOnlySpan<byte> data)
    {
        switch (data.Length)
        {
            case 1:
                return new SqliteValue((sbyte)data[0], data.Length);
            case 2:
                return new SqliteValue(BinaryPrimitives.ReadInt16BigEndian(data), data.Length);
            case 3:
                return new SqliteValue(ConvertNonStandardLengthInt(3, data), data.Length);
            case 4:
                return new SqliteValue(BinaryPrimitives.ReadInt32BigEndian(data), data.Length);
            case 6:
                return new SqliteValue(ConvertNonStandardLengthLong(6, data), data.Length);
            case 8:
                return new SqliteValue(BinaryPrimitives.ReadInt64BigEndian(data), data.Length);
            default:
                throw new InvalidDataException("Unknown length of data");
        }
    }

    private static string GetTextValue(ReadOnlySpan<byte> data, TextEncoding encoding)
    {
        switch (encoding)
        {
            case TextEncoding.Utf8:
                return Encoding.UTF8.GetString(data);
            case TextEncoding.Utf16Be:
                return Encoding.BigEndianUnicode.GetString(data);
            case TextEncoding.Utf16Le:
                return Encoding.Unicode.GetString(data);
            default:
                throw new InvalidDataException("Invlaid Text Type provided");
        }
    }
    
    private static int ConvertNonStandardLengthInt(int size, ReadOnlySpan<byte> data)
    {
        if (data.Length != size)
        {
            throw new InvalidDataException("Sizes do not match");
        }
        int value = 0;
        for (var i = 0; i < size; i++)
        {
            value = value | data[i];
            if (i != size - 1)
            {
                value = value << 8;
            }
        }
        int signBit = size * 8 - 1;
        if (((value >> signBit) & 1) != 0)
            value |= unchecked((int)(~((1 << (size * 8)) - 1)));
        return value;
    }
    
    private static long ConvertNonStandardLengthLong(int size, ReadOnlySpan<byte> data)
    {
        if (data.Length != size)
        {
            throw new InvalidDataException("Sizes do not match");
        }
        long value = 0;
        for (var i = 0; i < size; i++)
        {
            value = value | data[i];
            if (i != size - 1)
            {
                value = value << 8;
            }
        }
        if (size < 8)
        {
            int signBit = size * 8 - 1;
            if (((value >> signBit) & 1) != 0)
                value |= ~((1L << (size * 8)) - 1);
        }
        return value;
    }    
    

    
}