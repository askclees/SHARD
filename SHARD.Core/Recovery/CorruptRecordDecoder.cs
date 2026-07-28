using SHARD.Core.Decoding;
using SHARD.Core.Enums;
using SHARD.Core.Records;
using SHARD.Core.Schema;

namespace SHARD.Core.Recovery;

public static class CorruptRecordDecoder
{
    public record DecodeResult(BTreeLeafCell? Cell, IReadOnlyList<string> Errors)
    {
        public bool IsValid => Cell is not null;
    }

    /// <summary>
    /// Reconstructs a BTreeLeafCell from a corrupt/incomplete record.
    /// <paramref name="anchorOffset"/> is the page offset of the serial-type varint for
    /// <paramref name="anchorColumnIndex"/>. For columns before the anchor the caller supplies
    /// the expected data byte length in <paramref name="preAnchorLengths"/>; serial types are
    /// synthesised from the schema affinity. For columns at and after the anchor, serial types
    /// are read directly from the page bytes.
    /// </summary>
    public static DecodeResult Decode(
        byte[] pageData,
        int anchorOffset,
        int anchorColumnIndex,
        IReadOnlyList<int> preAnchorLengths,
        long rowId,
        TableSchema schema,
        TextEncoding encoding)
    {
        var errors = new List<string>();
        int columnCount = schema.Columns.Count;

        if (anchorOffset < 0 || anchorOffset >= pageData.Length)
            return new DecodeResult(null, ["Anchor offset is outside the page."]);

        if (anchorColumnIndex < 0 || anchorColumnIndex >= columnCount)
            return new DecodeResult(null, ["Anchor column index is out of range."]);

        if (preAnchorLengths.Count != anchorColumnIndex)
            return new DecodeResult(null, ["Pre-anchor length list must have exactly one entry per pre-anchor column."]);

        // Read serial type varints from anchorOffset onward (anchor column and later)
        var postAnchorHeaders = new List<HeaderEntry>();
        var postAnchorVarintLengths = new List<int>();
        int pos = anchorOffset;
        for (int i = anchorColumnIndex; i < columnCount; i++)
        {
            if (pos >= pageData.Length)
            {
                errors.Add($"Ran out of page bytes reading serial type for column {i} ({schema.Columns[i].Name}).");
                break;
            }
            var sv = Varint.ReadAt(pageData, pos);
            postAnchorHeaders.Add(new HeaderEntry(sv));
            postAnchorVarintLengths.Add(sv.Length);
            pos += sv.Length;
        }

        int dataStart = pos; // data section begins immediately after all read serial type varints

        // Synthesise header entries for pre-anchor columns
        var preAnchorHeaders = new List<HeaderEntry>();
        var preAnchorVarintLengths = new List<int>();
        for (int i = 0; i < anchorColumnIndex; i++)
        {
            int byteLen = preAnchorLengths[i];
            var (serialType, warning) = SynthesiseSerialType(schema.Columns[i].Affinity, byteLen);
            if (warning is not null) errors.Add($"Column {i} ({schema.Columns[i].Name}): {warning}");
            var sv = new Varint(serialType, VarintLength(serialType));
            preAnchorHeaders.Add(new HeaderEntry(sv));
            preAnchorVarintLengths.Add(VarintLength(serialType));
        }

        // Merge into ordered header list
        var allHeaders = new List<HeaderEntry>(preAnchorHeaders.Count + postAnchorHeaders.Count);
        allHeaders.AddRange(preAnchorHeaders);
        allHeaders.AddRange(postAnchorHeaders);

        // Compute header_size value (includes the varint bytes for header_size itself)
        int preSectionLen  = preAnchorVarintLengths.Sum();
        int postSectionLen = postAnchorVarintLengths.Sum();
        // Start with 1-byte assumption for the header_size varint
        int headerSizeValue  = 1 + preSectionLen + postSectionLen;
        int headerSizeVLen   = VarintLength(headerSizeValue);
        // Re-evaluate in case the varint for header_size itself is multi-byte
        headerSizeValue = headerSizeVLen + preSectionLen + postSectionLen;
        headerSizeVLen  = VarintLength(headerSizeValue);
        var headerSizeVarint = new Varint(headerSizeValue, headerSizeVLen);

        // Compute payload size = header_size_value + sum(content lengths)
        long totalContent = allHeaders.Sum(h => (long)h.ContentLength);
        long payloadValue = headerSizeValue + totalContent;
        var payloadVarint  = new Varint(payloadValue, VarintLength(payloadValue));

        var rowIdVarint = new Varint(rowId, rowId >= 0 ? VarintLength(rowId) : 1);

        if (dataStart + totalContent > pageData.Length)
            errors.Add($"Decoded data section extends to byte {dataStart + totalContent} but page is {pageData.Length} bytes; trailing fields may be null.");

        try
        {
            var cell = new BTreeLeafCell(
                allHeaders,
                pageData,
                dataStart,
                payloadVarint,
                rowIdVarint,
                headerSizeVarint,
                encoding,
                anchorOffset);

            return new DecodeResult(cell, errors);
        }
        catch (Exception ex)
        {
            errors.Add($"Failed to construct cell: {ex.Message}");
            return new DecodeResult(null, errors);
        }
    }

    private static (long SerialType, string? Warning) SynthesiseSerialType(TypeAffinity affinity, int byteLength)
    {
        if (byteLength == 0) return (0, null); // NULL

        return affinity switch
        {
            TypeAffinity.Text => (2L * byteLength + 13, null),
            TypeAffinity.Real => byteLength == 8
                ? (7L, null)
                : (7L, "REAL columns are always 8 bytes; treating as 8-byte float."),
            TypeAffinity.Integer => byteLength switch
            {
                1 => (1L, null),
                2 => (2L, null),
                3 => (3L, null),
                4 => (4L, null),
                6 => (5L, null),
                8 => (6L, null),
                _ => ((long)byteLength, $"Length {byteLength} is not a standard SQLite integer size (1/2/3/4/6/8 bytes); serial type set to {byteLength}."),
            },
            _ => (2L * byteLength + 12, null), // BLOB / Numeric / unknown
        };
    }

    private static int VarintLength(long value)
    {
        if (value < 0) return 1;
        if (value < 128L)              return 1;
        if (value < 16_384L)           return 2;
        if (value < 2_097_152L)        return 3;
        if (value < 268_435_456L)      return 4;
        if (value < 34_359_738_368L)   return 5;
        if (value < 4_398_046_511_104L) return 6;
        if (value < 562_949_953_421_312L)  return 7;
        if (value < 72_057_594_037_927_936L) return 8;
        return 9;
    }
}
