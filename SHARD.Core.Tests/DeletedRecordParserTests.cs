using SHARD.Core.Enums;
using SHARD.Core.Recovery;
using SHARD.Core.Records;
using Xunit;

namespace SHARD.Core.Tests;

/// <summary>
/// Hand-crafted-byte tests for the multi-schema ("any schema") carving path used when a page's
/// owning table is unknown. No SQLite files involved — records are encoded directly so the
/// uniqueness/ambiguity policy can be exercised deterministically at the byte level.
/// </summary>
public class DeletedRecordParserTests
{
    // ── Byte-level record encoding helpers ──────────────────────────────────────
    // All values are kept small (<128) so every varint used here is a single byte.

    private static byte SerialTypeForIntWidth(int width) => width switch
    {
        1 => 1, 2 => 2, 3 => 3, 4 => 4, 6 => 5, 8 => 6,
        _ => throw new ArgumentOutOfRangeException(nameof(width)),
    };

    private static byte[] IntContent(long value, int width)
    {
        var bytes = new byte[width];
        for (int i = width - 1; i >= 0; i--)
        {
            bytes[i] = (byte)(value & 0xFF);
            value >>= 8;
        }
        return bytes;
    }

    private static (byte SerialType, byte[] Content) IntColumn(long value, int width) =>
        (SerialTypeForIntWidth(width), IntContent(value, width));

    private static (byte SerialType, byte[] Content) TextColumn(string text)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        return ((byte)(13 + 2 * bytes.Length), bytes);
    }

    /// <summary>Encodes a single B-tree leaf cell (payload-size + rowid + header + content), all single-byte varints.</summary>
    private static byte[] EncodeRecord(long rowId, params (byte SerialType, byte[] Content)[] columns)
    {
        var headerBody = columns.Select(c => c.SerialType).ToList();
        int headerSize = 1 + headerBody.Count; // header-size varint byte itself + one byte per serial type
        var payload = new List<byte> { (byte)headerSize };
        payload.AddRange(headerBody);
        foreach (var (_, content) in columns) payload.AddRange(content);

        var cell = new List<byte> { (byte)payload.Count, (byte)rowId };
        cell.AddRange(payload);
        return cell.ToArray();
    }

    private static RecordStructure Structure(params (SerialTypeKind[] Kinds, (int Min, int Max)? Range)[] columns)
    {
        var rs = new RecordStructure();
        foreach (var (kinds, range) in columns)
        {
            rs.AllowedKindsPerColumn.Add(kinds);
            rs.AllowedContentLengthRangePerColumn.Add(range);
        }
        return rs;
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public void TryRecoverAnySchema_UniqueMatch_IsAccepted()
    {
        byte[] record = EncodeRecord(1, IntColumn(42, 4), TextColumn("hi"));

        var candidates = new List<(string, RecordStructure)>
        {
            ("TwoColumnTable", Structure(([SerialTypeKind.Integer], null), ([SerialTypeKind.Text], null))),
            ("OneColumnTable", Structure(([SerialTypeKind.Integer], null))),
        };

        bool found = DeletedRecordParser.TryRecoverBTreeLeafRecordAnySchema(
            record, 0, TextEncoding.Utf8, candidates, out string? tableName, out BTreeLeafCell? cell, out int matchCount);

        Assert.True(found);
        Assert.Equal(1, matchCount);
        Assert.Equal("TwoColumnTable", tableName);
        Assert.NotNull(cell);
        Assert.Equal(1, cell!.RowId.Value);
    }

    [Fact]
    public void TryRecoverAnySchema_IdenticalShapeCandidates_IsRejectedAsAmbiguous()
    {
        byte[] record = EncodeRecord(1, IntColumn(42, 4));

        var candidates = new List<(string, RecordStructure)>
        {
            ("TableA", Structure(([SerialTypeKind.Integer], null))),
            ("TableB", Structure(([SerialTypeKind.Integer], null))),
        };

        bool found = DeletedRecordParser.TryRecoverBTreeLeafRecordAnySchema(
            record, 0, TextEncoding.Utf8, candidates, out string? tableName, out BTreeLeafCell? cell, out int matchCount);

        Assert.False(found);
        Assert.Equal(2, matchCount);
        Assert.Null(tableName);
        Assert.Null(cell);
    }

    [Fact]
    public void TryRecoverAnySchema_ContentLengthRangeNarrowing_DisambiguatesSameKindCandidates()
    {
        // Both candidates accept plain Integer, but only the 4-byte-width candidate's range should
        // uniquely match a record whose integer column is encoded in exactly 4 bytes.
        byte[] record = EncodeRecord(1, IntColumn(1000, 4));

        var candidates = new List<(string, RecordStructure)>
        {
            ("NarrowTo1Byte", Structure(([SerialTypeKind.Integer], (1, 1)))),
            ("NarrowTo4Byte", Structure(([SerialTypeKind.Integer], (4, 4)))),
        };

        bool found = DeletedRecordParser.TryRecoverBTreeLeafRecordAnySchema(
            record, 0, TextEncoding.Utf8, candidates, out string? tableName, out _, out int matchCount);

        Assert.True(found);
        Assert.Equal(1, matchCount);
        Assert.Equal("NarrowTo4Byte", tableName);
    }

    [Fact]
    public void TryRecoverAnySchema_ContentLengthRangeAppliesToTextColumns()
    {
        // A short text value should only match the candidate whose range covers its length.
        byte[] record = EncodeRecord(1, TextColumn("hi"));

        var candidates = new List<(string, RecordStructure)>
        {
            ("ShortText", Structure(([SerialTypeKind.Text], (0, 5)))),
            ("LongText", Structure(([SerialTypeKind.Text], (20, 50)))),
        };

        bool found = DeletedRecordParser.TryRecoverBTreeLeafRecordAnySchema(
            record, 0, TextEncoding.Utf8, candidates, out string? tableName, out _, out int matchCount);

        Assert.True(found);
        Assert.Equal(1, matchCount);
        Assert.Equal("ShortText", tableName);
    }

    [Fact]
    public void TryRecoverAnySchema_NoCandidateMatches_ReturnsZeroMatchCount()
    {
        byte[] record = EncodeRecord(1, IntColumn(1, 1), IntColumn(2, 1));

        var candidates = new List<(string, RecordStructure)>
        {
            ("OneColumnTable", Structure(([SerialTypeKind.Integer], null))),
            ("TextOnlyTable", Structure(([SerialTypeKind.Text], null))),
        };

        bool found = DeletedRecordParser.TryRecoverBTreeLeafRecordAnySchema(
            record, 0, TextEncoding.Utf8, candidates, out _, out _, out int matchCount);

        Assert.False(found);
        Assert.Equal(0, matchCount);
    }

    [Fact]
    public void CarveRawBytesAnySchema_SplitsBackToBackRecordsAcrossDistinctSchemas()
    {
        byte[] recordA = EncodeRecord(1, IntColumn(7, 1));
        byte[] recordB = EncodeRecord(2, TextColumn("hey"));
        byte[] buffer = recordA.Concat(new byte[3]).Concat(recordB).ToArray(); // zero padding between

        var candidates = new List<(string, RecordStructure)>
        {
            ("IntTable", Structure(([SerialTypeKind.Integer], null))),
            ("TextTable", Structure(([SerialTypeKind.Text], null))),
        };

        var results = DeletedRecordParser.CarveRawBytesAnySchema(buffer, TextEncoding.Utf8, candidates, out int ambiguousSkipped);

        Assert.Equal(0, ambiguousSkipped);
        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.TableName == "IntTable" && r.Cell.RowId.Value == 1);
        Assert.Contains(results, r => r.TableName == "TextTable" && r.Cell.RowId.Value == 2);
    }
}
