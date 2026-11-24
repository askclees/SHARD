using SHARD.Core.Decoding;
using SHARD.Core.Enums;
using SHARD.Core.Recovery;
using Xunit;

namespace SHARD.Core.Tests;

/// <summary>
/// Record headers that can't match their payload are rejected as soon as that's clear, rather than
/// after decoding the whole header. Carving tries a parse at every byte of unallocated space, where
/// a random byte read as the header size can claim most of the page.
/// </summary>
public class RecordHeaderRejectionTests
{
    private const string Mismatch = "Payload size does not match the size of all the fields combined";

    [Fact]
    public void HeaderLargerThanPayload_IsRejectedAsMismatch()
    {
        // Payload 5, rowid 1, header size 127: the header alone is bigger than the payload. Decoding
        // it would run off the end of the data first, so this checks the size test comes first.
        var data = new byte[20];
        data[0] = 5; data[1] = 1; data[2] = 0x7F;
        for (int i = 3; i < data.Length; i++) data[i] = 1;

        var result = DeletedRecordParser.RecoverBTreeLeafRecord(data, 0, TextEncoding.Utf8);

        Assert.False(result.IsValid);
        Assert.Equal([Mismatch], result.ValidationErrors);
    }

    [Fact]
    public void SerialTypeWithNegativeLength_IsRejected()
    {
        // Serial type 2^33 + 8 is an even "blob" whose length overflows an int to −2. With a 4-byte
        // blob after it, the lengths add up to the payload (7 − 2 + 4 = 9), so only rejecting the
        // negative length stops it being taken as a record.
        byte[] data = [9, 1, 7, 0xA0, 0x80, 0x80, 0x80, 0x08, 20, 0xAA, 0xBB, 0, 0, 0, 0, 0];

        var result = DeletedRecordParser.RecoverBTreeLeafRecord(data, 0, TextEncoding.Utf8);

        Assert.False(result.IsValid);
        Assert.Equal([Mismatch], result.ValidationErrors);
    }

    [Fact]
    public void ValidRecord_IsStillAccepted()
    {
        // Payload 3: header size 2, one 1-byte integer column holding 0x2A, then a trailing byte.
        byte[] data = [3, 7, 2, 1, 0x2A, 0xFF];

        var result = DeletedRecordParser.RecoverBTreeLeafRecord(data, 0, TextEncoding.Utf8);

        Assert.True(result.IsValid);
        Assert.Equal(5, result.Cell!.CellByteLengthOnPage);
    }

    [Theory]
    [InlineData(new byte[] { 0x05 }, 5L, 1)]
    [InlineData(new byte[] { 0x81, 0x00 }, 128L, 2)]
    // Truncated: the bytes after the end read as zero, so the varint ends one byte past the data.
    [InlineData(new byte[] { 0x81 }, 128L, 2)]
    public void Varint_DecodesWithoutReadingPastTheSpan(byte[] bytes, long value, int length)
    {
        var v = Varint.ReadAt(bytes, 0);
        Assert.Equal(value, v.Value);
        Assert.Equal(length, v.Length);
    }
}
