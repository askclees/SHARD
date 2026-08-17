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
    
    /// <summary>Schema-agnostic result of parsing a B-tree leaf record at a given offset.</summary>
    private readonly struct ParsedRecordHeader
    {
        public Varint PayloadSize { get; }
        public Varint RowId { get; }
        public List<HeaderEntry> HeaderEntries { get; }
        public int CellSize { get; }

        public ParsedRecordHeader(Varint payloadSize, Varint rowId, List<HeaderEntry> headerEntries, int cellSize)
        {
            PayloadSize   = payloadSize;
            RowId         = rowId;
            HeaderEntries = headerEntries;
            CellSize      = cellSize;
        }
    }

    /// <summary>
    /// Parses the payload-size/rowid/header-size/serial-type varints of a candidate B-tree leaf
    /// record at <paramref name="offset"/>, independent of any particular table's schema. Verifies
    /// the header-declared size is internally consistent (<c>headerSize + Σ contentLengths ==
    /// payloadSize</c>) and that the record fits within <paramref name="data"/>, but does not
    /// validate column count or types against a <see cref="RecordStructure"/> — callers do that
    /// separately, which lets the same parse be validated against multiple candidate structures
    /// without re-decoding the varints for each one.
    /// </summary>
    private static bool TryParseRecordHeader(ReadOnlySpan<byte> data, int offset, out ParsedRecordHeader header, out string? rejectionReason)
    {
        header = default;

        if (offset < 0 || offset >= data.Length)
        {
            rejectionReason = OffsetOutOfRange;
            return false;
        }

        Varint payloadSize = Varint.ReadAt(data, offset);
        if (payloadSize.Value == 0)
        {
            rejectionReason = PayloadSizeZero;
            return false;
        }
        int currentOffset = offset + payloadSize.Length;
        if (currentOffset >= data.Length || payloadSize.Value + offset > data.Length)
        {
            rejectionReason = RecordLargerThanPage;
            return false;
        }
        Varint rowId = Varint.ReadAt(data, currentOffset);
        currentOffset += rowId.Length;
        if (currentOffset >= data.Length)
        {
            rejectionReason = RecordLargerThanPage;
            return false;
        }
        Varint headerSize = Varint.ReadAt(data, currentOffset);
        //Need to decode header size, includes varint of size in length
        List<HeaderEntry> HeaderEntries = new();
        var headerOffset = headerSize.Length;
        while (headerOffset < headerSize.Value)
        {
            if (currentOffset + headerOffset >= data.Length)
            {
                rejectionReason = RecordLargerThanPage;
                return false;
            }
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
            rejectionReason = PayloadHeaderMismatch;
            return false;
        }

        int cellSize = payloadSize.Length + rowId.Length + (int)payloadSize.Value;
        if (offset + cellSize > data.Length)
        {
            rejectionReason = RecordLargerThanPage;
            return false;
        }

        header = new ParsedRecordHeader(payloadSize, rowId, HeaderEntries, cellSize);
        rejectionReason = null;
        return true;
    }

    private static bool MatchesStructure(List<HeaderEntry> entries, RecordStructure rs)
    {
        if (entries.Count != rs.NumColumns) return false;
        for (int i = 0; i < entries.Count; i++)
        {
            if (!rs.AllowedKindsPerColumn[i].Contains(entries[i].Kind)) return false;
            var range = rs.AllowedContentLengthRangePerColumn[i];
            if (range is not null && entries[i].Kind is SerialTypeKind.Integer or SerialTypeKind.Float or SerialTypeKind.Text or SerialTypeKind.Blob
                && (entries[i].ContentLength < range.Value.Min || entries[i].ContentLength > range.Value.Max))
                return false;
        }
        return true;
    }

    public static DeletedBTreeLeafCellResult RecoverBTreeLeafRecord(ReadOnlySpan<byte> data,
        int offset,
        TextEncoding encoding,
        RecordStructure? recordStructure=null)
    {
        if (!TryParseRecordHeader(data, offset, out var header, out string? rejectionReason))
            return new DeletedBTreeLeafCellResult(new List<string>() { rejectionReason! });

        if (recordStructure != null)
        {
            if (header.HeaderEntries.Count != recordStructure.NumColumns)
            {
                return new DeletedBTreeLeafCellResult(new List<String>() { ColumnNumberMismatch });
            }

            if (!MatchesStructure(header.HeaderEntries, recordStructure))
            {
                return new DeletedBTreeLeafCellResult(new List<String>() { ColumnTypeMismatch });
            }
        }

        return new DeletedBTreeLeafCellResult(
            new BTreeLeafCell(
                data[offset..(offset + header.CellSize)].ToArray(),
                header.PayloadSize,
                encoding,
                offset));
    }

    /// <summary>
    /// Parses the record header at <paramref name="offset"/> once, then tests it against every
    /// candidate <see cref="RecordStructure"/> — used when the page's owning table is unknown and
    /// several tables' structures need to be tried against the same bytes. Accepts only if exactly
    /// one candidate matches; <paramref name="matchCount"/> tells the caller whether the offset
    /// didn't look like a record at all (0) or was ambiguous between candidates (&gt;1) so it can be
    /// reported separately rather than guessed.
    /// </summary>
    public static bool TryRecoverBTreeLeafRecordAnySchema(
        ReadOnlySpan<byte> data,
        int offset,
        TextEncoding encoding,
        IReadOnlyList<(string TableName, RecordStructure Structure)> candidates,
        out string? matchedTableName,
        out BTreeLeafCell? cell,
        out int matchCount)
    {
        matchedTableName = null;
        cell = null;
        matchCount = 0;

        if (!TryParseRecordHeader(data, offset, out var header, out _))
            return false;

        string? singleMatchTable = null;
        foreach (var (tableName, structure) in candidates)
        {
            if (structure.NumColumns != header.HeaderEntries.Count) continue;
            if (!MatchesStructure(header.HeaderEntries, structure)) continue;

            matchCount++;
            singleMatchTable = tableName;
        }

        if (matchCount != 1) return false;

        matchedTableName = singleMatchTable;
        cell = new BTreeLeafCell(
            data[offset..(offset + header.CellSize)].ToArray(),
            header.PayloadSize,
            encoding,
            offset);
        return true;
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

    /// <summary>
    /// Same sliding byte-by-byte/zero-skip scan as <see cref="CarveRawBytes"/>, but tests every
    /// candidate <see cref="RecordStructure"/> at each offset instead of one fixed one — used for
    /// pages that carry no hint about which table (if any) they used to belong to.
    /// <paramref name="ambiguousSkipped"/> counts offsets that looked record-shaped but matched more
    /// than one candidate, so callers can report ambiguity without silently guessing.
    /// </summary>
    public static IReadOnlyList<(string TableName, BTreeLeafCell Cell)> CarveRawBytesAnySchema(
        ReadOnlySpan<byte> data,
        TextEncoding encoding,
        IReadOnlyList<(string TableName, RecordStructure Structure)> candidates,
        out int ambiguousSkipped)
    {
        var cells = new List<(string TableName, BTreeLeafCell Cell)>();
        int pos = 0;
        int end = data.Length;
        ambiguousSkipped = 0;

        while (pos < end)
        {
            while (pos < end && data[pos] == 0x00) pos++;
            if (pos >= end) break;

            if (TryRecoverBTreeLeafRecordAnySchema(data, pos, encoding, candidates, out string? tableName, out BTreeLeafCell? cell, out int matchCount))
            {
                cells.Add((tableName!, cell!));
                pos += cell!.CellByteLengthOnPage;
            }
            else
            {
                if (matchCount > 1) ambiguousSkipped++;
                pos++;
            }
        }

        return cells;
    }
}