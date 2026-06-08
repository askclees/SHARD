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
        //verify size against values
        var recordLength = HeaderSize.Value;
        foreach (HeaderEntry entry in HeaderEntries)
        {
            recordLength += entry.ContentLength;
        }
        if (recordLength != PayloadSize.Value)
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
                    FieldVaues.Add(new SqliteValue(0L));
                    break;
                case SerialTypeKind.Int1:
                    FieldVaues.Add(new SqliteValue(1L));
                    break;
                default:
                    FieldVaues.Add(null);
                    break;
                case SerialTypeKind.Integer:
                    switch (entry.ContentLength)
                    {
                        case 1:
                            FieldVaues.Add(new SqliteValue((sbyte)data[recordOffset]));
                            break;
                        case 2:
                            FieldVaues.Add(new SqliteValue(
                                BinaryPrimitives.ReadInt16BigEndian(data[recordOffset..(recordOffset + 2)].AsSpan())));
                            break;
                        case 3:
                            FieldVaues.Add(new SqliteValue(ConvertNonStandardLengthInt(3, data[recordOffset..(recordOffset+3)].AsSpan())));
                            break;
                        case 4:
                            FieldVaues.Add(new SqliteValue(
                                BinaryPrimitives.ReadInt32BigEndian(data[recordOffset..(recordOffset + 4)])));
                            break;
                        case 6:
                            FieldVaues.Add(new SqliteValue(ConvertNonStandardLengthLong(6, data[recordOffset..(recordOffset+6)].AsSpan())));
                            break;
                        case 8:
                            FieldVaues.Add(new SqliteValue(
                                BinaryPrimitives.ReadInt64BigEndian(data[recordOffset..(recordOffset + 8)])));
                            break;

                    }

                    break;
            }

            recordOffset += entry.ContentLength;
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