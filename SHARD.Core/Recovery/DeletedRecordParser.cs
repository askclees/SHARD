using SHARD.Core.Decoding;
using SHARD.Core.Enums;
using SHARD.Core.Records;

namespace SHARD.Core.Recovery;

public static class DeletedRecordParser
{
    private const string OffsetOutOfRange  = "Offset is outside the bounds of the page data";
    private const string PayloadSizeZero   = "Payload size is zero and cannot be correct";
    private const string RecordLargerThanPage = "Payload size indicates record goes past end of page. recovery involving Overflow pages not supported at this time.";
    private const string PayloadHeaderMismatch = "Payload size does not match the size of all the fields combined";
    private const string ColumnNumberMismatch = "The number of columns in recovered record does not match the provided record structure";
    private const string ColumnTypeMismatch = "The column type does not match the type provided in the record structure";
    
    public static DeletedBTreeLeafCellResult RecoverBTreeLeafRecord(ReadOnlySpan<byte> data, 
        int offset, 
        TextEncoding encoding, 
        RecordStructure? recordStructure=null)
    {
        if (offset < 0 || offset >= data.Length)
            return new DeletedBTreeLeafCellResult(new List<string>() { OffsetOutOfRange });

        Varint payloadSize = Varint.ReadAt(data, offset);
        if (payloadSize.Value == 0)
        {
            return new DeletedBTreeLeafCellResult(new List<string>() { PayloadSizeZero });
        }
        int currentOffset = offset + payloadSize.Length;
        if (currentOffset >= data.Length || payloadSize.Value + offset > data.Length)
        {
            return new DeletedBTreeLeafCellResult(new List<string>() { RecordLargerThanPage });
        }
        Varint rowId = Varint.ReadAt(data, currentOffset);
        currentOffset += rowId.Length;
        if (currentOffset >= data.Length)
        {
            return new DeletedBTreeLeafCellResult(new List<string>() { RecordLargerThanPage });
        }
        Varint headerSize = Varint.ReadAt(data, currentOffset);
        //Need to decode header size, includes varint of size in length
        List<HeaderEntry> HeaderEntries = new();
        var headerOffset = headerSize.Length;
        while (headerOffset < headerSize.Value)
        {
            if (currentOffset + headerOffset >= data.Length)
                return new DeletedBTreeLeafCellResult(new List<string>() { RecordLargerThanPage });
            Varint temp = Varint.ReadAt(data, currentOffset + headerOffset);
            HeaderEntries.Add(new HeaderEntry(temp));
            headerOffset += temp.Length;
        }
        //verify size against values
        var recordLength = headerSize.Value;
        foreach (HeaderEntry entry in HeaderEntries)
        {
            recordLength += entry.ContentLength;
        }
        if (recordLength != payloadSize.Value)
        {
            return new DeletedBTreeLeafCellResult(new List<String>() { PayloadHeaderMismatch });
        }

        if (recordStructure != null)
        {
            if (HeaderEntries.Count != recordStructure.NumColumns)
            {
                return new DeletedBTreeLeafCellResult(new List<String>() { ColumnNumberMismatch });
            }

            for (int i = 0; i < HeaderEntries.Count; i++)
            {
                if (!recordStructure.AllowedKindsPerColumn[i].Contains(HeaderEntries[i].Kind))
                {
                    return new DeletedBTreeLeafCellResult(new List<String>() { ColumnTypeMismatch });
                }
            }
        }
        
        int cellSize = payloadSize.Length + rowId.Length + (int)payloadSize.Value;
        if (offset + cellSize > data.Length)
        {
            return new DeletedBTreeLeafCellResult(new List<string>() { RecordLargerThanPage });
        }
        return new DeletedBTreeLeafCellResult(
            new BTreeLeafCell(
                data[offset..(offset + cellSize)].ToArray(),
                payloadSize,
                encoding,
                offset));
    }

    /// <summary>
    /// Slides through <paramref name="data"/> byte-by-byte and collects every valid B-tree
    /// leaf cell whose structure satisfies <paramref name="recordStructure"/>.  Zero runs are
    /// skipped.  Used to carve records from freed pages whose raw bytes may still hold data
    /// from when the page was a live table page.
    /// </summary>
    public static IReadOnlyList<BTreeLeafCell> CarveRawBytes(
        byte[] data, TextEncoding encoding, RecordStructure? recordStructure = null)
    {
        var cells = new List<BTreeLeafCell>();
        int pos   = 0;
        int end   = data.Length;

        while (pos < end)
        {
            while (pos < end && data[pos] == 0x00) pos++;
            if (pos >= end) break;

            var result = RecoverBTreeLeafRecord(data, pos, encoding, recordStructure);
            if (result.IsValid)
            {
                cells.Add(result.Cell!);
                pos += result.Cell!.CellByteLengthOnPage;
            }
            else
            {
                pos++;
            }
        }

        return cells;
    }
}