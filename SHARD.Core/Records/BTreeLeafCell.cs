using System.Buffers.Binary;
using SHARD.Core.Decoding;
using SHARD.Core.Enums;

namespace SHARD.Core.Records;

public class BTreeLeafCell
{
    public Varint SizeOfPayload { get; }
    public Varint RowId { get; }
    public Varint HeaderSize { get; }
    public List<HeaderEntry> HeaderEntries { get; } = new();
    public List<SqliteValue?> FieldVaues { get; } = new();

    public int OverflowPage = 0;

    public BTreeLeafCell(byte[] data, Varint payloadSize)
    {
        int offset = 0;
        SizeOfPayload = new Varint(data.AsSpan(offset..9));
        // Check payload sizes match
        if (!SizeOfPayload.Equals(payloadSize))
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
        //decode values
        foreach (HeaderEntry entry in HeaderEntries)
        {
            switch (entry.Kind)
            {
                case SerialTypeKind.Null:
                    FieldVaues.Add(null);
                    break;
                case SerialTypeKind.Int0:
                    FieldVaues.Add(new SqliteValue(0L,0));
                    break;
                case SerialTypeKind.Int1:
                    FieldVaues.Add(new SqliteValue(1L,0));
                    break;
                case SerialTypeKind.Integer:
                    FieldVaues.Add(GetIntegerValue(data[recordOffset..(recordOffset+entry.ContentLength) ]));
                    break;
                default:
                    FieldVaues.Add(null);
                    break;
            }
            recordOffset += entry.ContentLength;
        }
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