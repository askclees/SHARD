using System.Buffers.Binary;
using System.Text;
using SHARD.Core.Decoding;
using SHARD.Core.Enums;

namespace SHARD.Core.Records;

/// <summary>
/// A parsed cell from an index B-tree leaf page.
/// Format: <c>payload_size (varint) | record (header + body)</c>.
/// Unlike table leaf cells there is no interleaved rowid varint — the rowid is encoded
/// as the last field in the record payload itself (for non-unique indexes).
/// </summary>
public sealed class IndexBTreeLeafCell
{
    public Varint SizeOfPayload { get; }
    public Varint HeaderSize { get; }
    public List<HeaderEntry> HeaderEntries { get; } = new();
    public List<SqliteValue?> FieldValues { get; } = new();
    public uint OverflowPage { get; }

    private readonly byte[] _localData;
    private readonly TextEncoding _encoding;

    public int OverflowBytesNeeded =>
        OverflowPage == 0 ? 0 : (int)SizeOfPayload.Value - (_localData.Length - SizeOfPayload.Length);

    public int CellByteLengthOnPage => _localData.Length + (OverflowPage != 0 ? 4 : 0);

    public IndexBTreeLeafCell(byte[] data, Varint payloadSize, TextEncoding encoding, uint overflowPage = 0)
    {
        OverflowPage = overflowPage;
        _localData = data;
        _encoding = encoding;

        int offset = 0;
        SizeOfPayload = Varint.ReadAt(data, offset);
        offset += SizeOfPayload.Length;

        HeaderSize = Varint.ReadAt(data, offset);
        var headerOffset = HeaderSize.Length;
        while (headerOffset < HeaderSize.Value)
        {
            var temp = Varint.ReadAt(data, offset + headerOffset);
            HeaderEntries.Add(new HeaderEntry(temp));
            headerOffset += temp.Length;
        }

        var recordOffset = offset + (int)HeaderSize.Value;
        FieldValues.AddRange(DecodeFieldValues(data, recordOffset));
    }

    public void ResolveOverflow(byte[] overflowBytes)
    {
        if (OverflowPage == 0) return;

        var fullData = new byte[_localData.Length + overflowBytes.Length];
        _localData.CopyTo(fullData, 0);
        overflowBytes.CopyTo(fullData, _localData.Length);

        var recordOffset = SizeOfPayload.Length + (int)HeaderSize.Value;
        FieldValues.Clear();
        FieldValues.AddRange(DecodeFieldValues(fullData, recordOffset));
    }

    private List<SqliteValue?> DecodeFieldValues(byte[] data, int recordOffset)
    {
        var values = new List<SqliteValue?>();
        foreach (var entry in HeaderEntries)
        {
            switch (entry.Kind)
            {
                case SerialTypeKind.Null:
                    values.Add(null);
                    break;
                case SerialTypeKind.Int0:
                    values.Add(new SqliteValue(0L, 0));
                    break;
                case SerialTypeKind.Int1:
                    values.Add(new SqliteValue(1L, 0));
                    break;
                case SerialTypeKind.Integer:
                    if (recordOffset + entry.ContentLength > data.Length)
                    {
                        values.Add(null);
                        break;
                    }
                    values.Add(GetIntegerValue(data[recordOffset..(recordOffset + entry.ContentLength)]));
                    break;
                case SerialTypeKind.Float:
                    if (recordOffset + 8 > data.Length)
                    {
                        values.Add(null);
                        break;
                    }
                    values.Add(new SqliteValue(BinaryPrimitives.ReadDoubleBigEndian(data.AsSpan(recordOffset, 8)), 8));
                    break;
                case SerialTypeKind.Text:
                    if (recordOffset + entry.ContentLength > data.Length)
                    {
                        values.Add(null);
                        break;
                    }
                    values.Add(new SqliteValue(GetTextValue(data.AsSpan(recordOffset, entry.ContentLength), _encoding), entry.ContentLength));
                    break;
                case SerialTypeKind.Blob:
                    if (recordOffset + entry.ContentLength > data.Length)
                    {
                        values.Add(null);
                        break;
                    }
                    values.Add(new SqliteValue(data[recordOffset..(recordOffset + entry.ContentLength)], entry.ContentLength));
                    break;
                default:
                    values.Add(null);
                    break;
            }
            recordOffset += entry.ContentLength;
        }
        return values;
    }

    private static SqliteValue GetIntegerValue(ReadOnlySpan<byte> data) => data.Length switch
    {
        1 => new SqliteValue((sbyte)data[0], 1),
        2 => new SqliteValue(BinaryPrimitives.ReadInt16BigEndian(data), 2),
        3 => new SqliteValue(Read3ByteInt(data), 3),
        4 => new SqliteValue(BinaryPrimitives.ReadInt32BigEndian(data), 4),
        6 => new SqliteValue(ReadNByteInt(6, data), 6),
        8 => new SqliteValue(BinaryPrimitives.ReadInt64BigEndian(data), 8),
        _ => throw new InvalidDataException($"Unexpected integer length: {data.Length}")
    };

    private static long Read3ByteInt(ReadOnlySpan<byte> data)
    {
        int v = (data[0] << 16) | (data[1] << 8) | data[2];
        if ((v & 0x800000) != 0) v |= unchecked((int)0xFF000000);
        return v;
    }

    private static long ReadNByteInt(int size, ReadOnlySpan<byte> data)
    {
        long v = 0;
        for (int i = 0; i < size; i++) v = (v << 8) | data[i];
        int signBit = size * 8 - 1;
        if (((v >> signBit) & 1) != 0) v |= ~((1L << (size * 8)) - 1);
        return v;
    }

    private static string GetTextValue(ReadOnlySpan<byte> data, TextEncoding encoding) => encoding switch
    {
        TextEncoding.Utf8    => Encoding.UTF8.GetString(data),
        TextEncoding.Utf16Be => Encoding.BigEndianUnicode.GetString(data),
        TextEncoding.Utf16Le => Encoding.Unicode.GetString(data),
        _ => throw new InvalidDataException($"Unknown text encoding: {encoding}")
    };
}
