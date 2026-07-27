using SHARD.Core.Decoding;
using SHARD.Core.Enums;
using SHARD.Core.Records;

namespace SHARD.Core.Recovery;

/// <summary>
/// Recovers deleted records from SQLite page freeblocks.
///
/// When a cell is freed, SQLite overwrites its first 4 bytes with the freeblock
/// linked-list header (2-byte next-pointer + 2-byte size).  The first record in
/// a freeblock therefore has its payload-size and rowid varints (and possibly
/// the start of its record header) partially or fully lost.
///
/// Recovery strategy:
///   1. Derive the typical payload-size varint length (P) and rowid varint length
///      (R) from live cells on the same page — deleted records almost always share
///      the same lengths as their neighbours.
///   2. k = P + R tells us where the intact header starts inside the freeblock.
///      If k ≥ 4 the record header is fully intact; if k &lt; 4 we infer the missing
///      column types algebraically using the freeblock size as a constraint.
///   3. The rowid is set to -1 in all cases because it lies within the first k
///      bytes (nearly always inside the overwritten region).
///   4. After the first record, any remaining space in the freeblock contains
///      fully-intact former records that are parsed with the standard
///      <see cref="DeletedRecordParser"/>.
/// </summary>
public static class FreeblockRecordParser
{
    public static IEnumerable<BTreeLeafCell> RecoverFromFreeblock(
        byte[] pageData,
        PageFreeBlock freeblock,
        IReadOnlyList<BTreeLeafCell> liveCells,
        TextEncoding encoding,
        RecordStructure recordStructure)
    {
        if (liveCells.Count == 0) yield break;

        int fbStart = (int)freeblock.PageOffset;
        int fbSize  = (int)freeblock.BlockSize;
        int fbEnd   = fbStart + fbSize;
        if (fbEnd > pageData.Length) yield break;

        int typicalP = Mode(liveCells.Select(c => c.SizeOfPayload.Length));
        int typicalR = Mode(liveCells.Select(c => c.RowId.Length));
        int k = typicalP + typicalR;

        long minPayload = liveCells.Min(c => c.SizeOfPayload.Value);
        long maxPayload = liveCells.Max(c => c.SizeOfPayload.Value);
        long tolerance  = Math.Max(64L, maxPayload - minPayload + 64L);
        long loPayload  = Math.Max(1L, minPayload - tolerance);
        long hiPayload  = maxPayload + tolerance;

        // ── First record: first k bytes overwritten by freeblock header ──────────
        BTreeLeafCell? first = k >= 4
            ? TryIntactHeader(pageData, fbStart, k, typicalP, typicalR, loPayload, hiPayload, encoding, recordStructure)
            : TryPartialHeader(pageData, fbStart, fbSize, k, typicalP, typicalR, loPayload, hiPayload, encoding, recordStructure);

        if (first is null) yield break;
        yield return first;

        // ── Subsequent records: fully intact bytes inside the same freeblock ─────
        int nextOffset = fbStart + first.CellByteLengthOnPage;
        while (nextOffset < fbEnd - 4)
        {
            while (nextOffset < fbEnd && pageData[nextOffset] == 0x00)
                nextOffset++;
            if (nextOffset >= fbEnd) break;

            var result = DeletedRecordParser.RecoverBTreeLeafRecord(pageData, nextOffset, encoding, recordStructure);
            if (!result.IsValid) break;

            long ps = result.Cell!.SizeOfPayload.Value;
            if (ps < loPayload || ps > hiPayload) break;

            yield return result.Cell;
            nextOffset += result.Cell.CellByteLengthOnPage;
        }
    }

    // ── k ≥ 4: header starts at byte k, fully intact ─────────────────────────────

    private static BTreeLeafCell? TryIntactHeader(
        byte[] pageData, int fbStart, int k, int typicalP, int typicalR,
        long loPayload, long hiPayload,
        TextEncoding encoding, RecordStructure recordStructure)
    {
        int headerStart = fbStart + k;
        if (headerStart >= pageData.Length) return null;

        if (!TryParseHeaderEntries(pageData, headerStart, out var entries, out long headerSizeValue))
            return null;
        if (!ValidateEntries(entries!, recordStructure)) return null;

        var validEntries = entries!;
        long payloadValue = headerSizeValue + validEntries.Sum(e => (long)e.ContentLength);
        if (payloadValue < loPayload || payloadValue > hiPayload) return null;

        return BuildCell(pageData, fbStart, typicalP, typicalR, payloadValue,
                         validEntries, headerSizeValue, encoding, dataOffset: headerStart + (int)headerSizeValue);
    }

    // ── k < 4: first (4-k) bytes of the header are inside the overwritten region ─
    //
    // Assumes a 1-byte header-size varint and 1-byte column-type varints for
    // any column whose type byte falls within bytes 0-3.  The freeblock size
    // provides the total-payload constraint that lets us derive the missing type.

    private static BTreeLeafCell? TryPartialHeader(
        byte[] pageData, int fbStart, int fbSize, int k, int typicalP, int typicalR,
        long loPayload, long hiPayload,
        TextEncoding encoding, RecordStructure recordStructure)
    {
        long expectedPayload = fbSize - k;
        if (expectedPayload < loPayload || expectedPayload > hiPayload) return null;

        int N = recordStructure.NumColumns;

        // With 1-byte header-size at byte k: column types begin at byte k+1.
        // Bytes 0-3 are overwritten, so intact column types start at byte 4.
        // lostColTypes = number of column-type bytes that fell inside bytes 0-3.
        int lostColTypes = Math.Max(0, 4 - k - 1); // -1 accounts for the 1-byte header-size varint

        if (lostColTypes > 1) return null; // more than one lost column type is too ambiguous

        // Parse intact column types from byte 4 onward.
        int typeReadOffset = fbStart + 4;
        var intactEntries  = new List<HeaderEntry>(N - lostColTypes);
        for (int i = lostColTypes; i < N; i++)
        {
            if (typeReadOffset >= pageData.Length) return null;
            var tv    = Varint.ReadAt(pageData, typeReadOffset);
            var entry = new HeaderEntry(tv);
            if (!recordStructure.AllowedKindsPerColumn[i].Contains(entry.Kind)) return null;
            intactEntries.Add(entry);
            typeReadOffset += tv.Length;
        }
        int dataOffset = typeReadOffset; // field data follows immediately after the last intact type varint

        long intactContentTotal = intactEntries.Sum(e => (long)e.ContentLength);
        int  intactTypeBytes    = intactEntries.Sum(e => e.RawValue.Length);

        // header_size.Value (assumed 1-byte varint) = 1 (itself) + lostColTypes (assumed 1-byte each) + intactTypeBytes
        long assumedHeaderSize = 1L + lostColTypes + intactTypeBytes;

        var lostEntries = new List<HeaderEntry>(lostColTypes);
        if (lostColTypes == 1)
        {
            int lostContentLen = (int)(expectedPayload - assumedHeaderSize - intactContentTotal);
            if (lostContentLen < 0) return null;

            var inferred = InferHeaderEntry(lostContentLen, recordStructure.AllowedKindsPerColumn[0]);
            if (inferred is null) return null;
            lostEntries.Add(inferred.Value);

            // Recompute actual header size in case the inferred type varint is not 1 byte.
            long actualHeaderSize = 1L + inferred.Value.RawValue.Length + intactTypeBytes;
            if (actualHeaderSize + lostContentLen + intactContentTotal != expectedPayload) return null;
            assumedHeaderSize = actualHeaderSize;
        }

        var allEntries = new List<HeaderEntry>(lostEntries.Count + intactEntries.Count);
        allEntries.AddRange(lostEntries);
        allEntries.AddRange(intactEntries);
        if (allEntries.Count != N) return null;

        var hsVarint = new Varint(assumedHeaderSize, assumedHeaderSize < 128 ? 1 : 2);
        return BuildCell(pageData, fbStart, typicalP, typicalR, expectedPayload,
                         allEntries, assumedHeaderSize, encoding, dataOffset, hsVarint);
    }

    // ── Shared helpers ────────────────────────────────────────────────────────────

    private static BTreeLeafCell? BuildCell(
        byte[] pageData, int fbStart, int typicalP, int typicalR,
        long payloadValue, List<HeaderEntry> entries, long headerSizeValue,
        TextEncoding encoding, int dataOffset, Varint? headerSizeVarint = null)
    {
        int totalContent = entries.Sum(e => e.ContentLength);
        if (dataOffset + totalContent > pageData.Length) return null;

        var psVarint = new Varint(payloadValue, typicalP);
        var ridVarint = new Varint(-1L, typicalR);
        var hsVarint  = headerSizeVarint ?? new Varint(headerSizeValue, headerSizeValue < 128 ? 1 : 2);

        try
        {
            return new BTreeLeafCell(entries, pageData, dataOffset, psVarint, ridVarint, hsVarint, encoding, fbStart);
        }
        catch
        {
            return null;
        }
    }

    private static bool TryParseHeaderEntries(byte[] pageData, int absOffset,
        out List<HeaderEntry>? entries, out long headerSizeValue)
    {
        entries = null;
        headerSizeValue = 0;
        if (absOffset >= pageData.Length) return false;

        var hsV = Varint.ReadAt(pageData, absOffset);
        headerSizeValue = hsV.Value;
        entries = new List<HeaderEntry>();
        int off = hsV.Length;
        while (off < headerSizeValue)
        {
            if (absOffset + off >= pageData.Length) return false;
            var tv = Varint.ReadAt(pageData, absOffset + off);
            entries.Add(new HeaderEntry(tv));
            off += tv.Length;
        }
        return true;
    }

    private static bool ValidateEntries(List<HeaderEntry> entries, RecordStructure rs)
    {
        if (entries.Count != rs.NumColumns) return false;
        for (int i = 0; i < entries.Count; i++)
            if (!rs.AllowedKindsPerColumn[i].Contains(entries[i].Kind)) return false;
        return true;
    }

    /// <summary>
    /// Given a known content length for a lost column, determine the best-matching
    /// <see cref="HeaderEntry"/> consistent with the column's allowed kinds.
    /// Returns null if no allowed kind is consistent with that length.
    /// </summary>
    private static HeaderEntry? InferHeaderEntry(int contentLength, SerialTypeKind[] allowedKinds)
    {
        if (contentLength == 0)
        {
            if (allowedKinds.Contains(SerialTypeKind.Null))
                return new HeaderEntry(new Varint(0L, 1));
            if (allowedKinds.Contains(SerialTypeKind.Int0))
                return new HeaderEntry(new Varint(8L, 1));
            if (allowedKinds.Contains(SerialTypeKind.Int1))
                return new HeaderEntry(new Varint(9L, 1));
        }

        // Integer serial types 1-6 map to content lengths 1,2,3,4,6,8 respectively.
        long intSerial = contentLength switch { 1 => 1, 2 => 2, 3 => 3, 4 => 4, 6 => 5, 8 => 6, _ => -1 };
        if (intSerial > 0 && allowedKinds.Contains(SerialTypeKind.Integer))
            return new HeaderEntry(new Varint(intSerial, 1));

        // Float also uses 8 bytes (serial type 7).
        if (contentLength == 8 && allowedKinds.Contains(SerialTypeKind.Float))
            return new HeaderEntry(new Varint(7L, 1));

        // Text: serial type = 13 + 2*length (odd, ≥ 13).
        if (allowedKinds.Contains(SerialTypeKind.Text))
        {
            long serial = 13L + 2L * contentLength;
            return new HeaderEntry(new Varint(serial, serial < 128 ? 1 : 2));
        }

        // Blob: serial type = 12 + 2*length (even, ≥ 12).
        if (allowedKinds.Contains(SerialTypeKind.Blob))
        {
            long serial = 12L + 2L * contentLength;
            return new HeaderEntry(new Varint(serial, serial < 128 ? 1 : 2));
        }

        return null;
    }

    private static int Mode(IEnumerable<int> values) =>
        values.GroupBy(v => v).MaxBy(g => g.Count())!.Key;
}
