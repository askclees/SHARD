using SHARD.Core.Decoding;
using SHARD.Core.Enums;
using SHARD.Core.Records;

namespace SHARD.Core.Recovery;

/// <summary>
/// Recovers deleted records from SQLite page freeblocks.
///
/// SQLite overwrites the first 4 bytes of a freed cell with the freeblock
/// linked-list header (2-byte next-pointer + 2-byte size).  A cell starts with
/// a payload-size varint (P bytes) followed by a rowid varint (R bytes); k = P+R
/// is at most 4 bytes for the records we target (P ≤ 2, R ≤ 2).  Three templates
/// are tried in order of confidence:
///
///   k=4  All of P+R overwritten.  Record header is fully intact from byte 4.
///   k=3  P+R+headerSize overwritten.  All column-type varints intact from byte 4;
///        headerSize is inferred from their total varint length.
///   k=2  P+R+headerSize+colType₀ overwritten.  Columns 1..N-1 intact from byte 4;
///        the missing column-0 type is inferred from the freeblock size.
///
/// In all cases the rowid is recorded as -1 (it falls in the overwritten region).
/// After the first record, any remaining freeblock space is scanned for intact
/// subsequent records via <see cref="DeletedRecordParser"/>.
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
        int fbStart = (int)freeblock.PageOffset;
        int fbSize  = (int)freeblock.BlockSize;
        int fbEnd   = fbStart + fbSize;
        if (fbEnd > pageData.Length) yield break;

        // Derive payload bounds from live cells if available; otherwise accept anything.
        long loPayload, hiPayload;
        if (liveCells.Count > 0)
        {
            long minP = liveCells.Min(c => c.SizeOfPayload.Value);
            long maxP = liveCells.Max(c => c.SizeOfPayload.Value);
            long tol  = Math.Max(64L, maxP - minP + 64L);
            loPayload = Math.Max(1L, minP - tol);
            hiPayload = maxP + tol;
        }
        else
        {
            loPayload = 1;
            hiPayload = pageData.Length;
        }

        // Try templates in confidence order.
        BTreeLeafCell? first =
            TryK4(pageData, fbStart, loPayload, hiPayload, encoding, recordStructure) ??
            TryK3(pageData, fbStart, loPayload, hiPayload, encoding, recordStructure) ??
            TryK2(pageData, fbStart, fbSize, loPayload, hiPayload, liveCells, encoding, recordStructure);

        if (first is null) yield break;
        yield return first;

        // Subsequent records inside the same freeblock.
        //
        // Normally subsequent records are fully intact. However, when SQLite merges
        // two adjacent freed cells into one freeblock it does NOT clear the inner
        // cell's own old freeblock header — so the inner cell's first 4 bytes are
        // also overwritten. We detect this by checking whether the 2-byte size field
        // at [runStart+2] equals the number of bytes remaining to fbEnd; if so we
        // apply the same k-template recovery we used for the first record.
        int nextOffset = fbStart + first.CellByteLengthOnPage;
        while (nextOffset < fbEnd - 4)
        {
            int runStart = nextOffset;
            while (nextOffset < fbEnd && pageData[nextOffset] == 0x00)
                nextOffset++;
            if (nextOffset >= fbEnd) break;

            // Try as a fully-intact record first (the common case).
            var result = DeletedRecordParser.RecoverBTreeLeafRecord(pageData, nextOffset, encoding, recordStructure);
            if (result.IsValid)
            {
                long ps = result.Cell!.SizeOfPayload.Value;
                if (ps < loPayload || ps > hiPayload) break;
                yield return result.Cell;
                nextOffset += result.Cell.CellByteLengthOnPage;
                continue;
            }

            // Intact parse failed. Check if runStart holds an inner freeblock header
            // whose size field encodes exactly the bytes remaining to fbEnd.
            if (runStart + 4 > fbEnd) break;
            int innerSize = (pageData[runStart + 2] << 8) | pageData[runStart + 3];
            if (innerSize != fbEnd - runStart) break;

            BTreeLeafCell? inner =
                TryK4(pageData, runStart, loPayload, hiPayload, encoding, recordStructure) ??
                TryK3(pageData, runStart, loPayload, hiPayload, encoding, recordStructure) ??
                TryK2(pageData, runStart, innerSize, loPayload, hiPayload, liveCells, encoding, recordStructure);
            if (inner is null) break;

            yield return inner;
            nextOffset = runStart + inner.CellByteLengthOnPage;
        }
    }

    // ── k=4: bytes 0-3 are P+R, record header is intact at byte 4 ───────────────

    private static BTreeLeafCell? TryK4(
        byte[] pageData, int fbStart, long loPayload, long hiPayload,
        TextEncoding encoding, RecordStructure recordStructure)
    {
        int headerStart = fbStart + 4;
        if (headerStart >= pageData.Length) return null;

        if (!TryParseHeaderEntries(pageData, headerStart, out var entries, out long headerSizeValue))
            return null;
        if (!ValidateEntries(entries!, recordStructure)) return null;

        long payloadValue = headerSizeValue + entries!.Sum(e => (long)e.ContentLength);
        if (payloadValue < loPayload || payloadValue > hiPayload) return null;

        // P=2, R=2 → CellByteLengthOnPage = 4 + payloadValue (correct for k=4).
        return BuildCell(pageData, fbStart, 2, 2, payloadValue, entries!,
                         headerSizeValue, encoding,
                         dataOffset: headerStart + (int)headerSizeValue);
    }

    // ── k=3: bytes 0-3 are P+R+headerSize, all column types intact at byte 4 ────
    //
    // headerSize is inferred as 1 (its own varint) + sum of column-type varint lengths.

    private static BTreeLeafCell? TryK3(
        byte[] pageData, int fbStart, long loPayload, long hiPayload,
        TextEncoding encoding, RecordStructure recordStructure)
    {
        int N = recordStructure.NumColumns;
        int typeOffset = fbStart + 4;

        var entries = new List<HeaderEntry>(N);
        for (int i = 0; i < N; i++)
        {
            if (typeOffset >= pageData.Length) return null;
            var tv    = Varint.ReadAt(pageData, typeOffset);
            var entry = new HeaderEntry(tv);
            if (!recordStructure.AllowedKindsPerColumn[i].Contains(entry.Kind)) return null;
            entries.Add(entry);
            typeOffset += tv.Length;
        }

        int  intactTypeBytes  = entries.Sum(e => e.RawValue.Length);
        long headerSizeValue  = 1L + intactTypeBytes;
        long payloadValue     = headerSizeValue + entries.Sum(e => (long)e.ContentLength);

        if (payloadValue < loPayload || payloadValue > hiPayload) return null;

        var hsVarint = new Varint(headerSizeValue, headerSizeValue < 128 ? 1 : 2);
        // P=2, R=1 → CellByteLengthOnPage = 3 + payloadValue.
        return BuildCell(pageData, fbStart, 2, 1, payloadValue, entries,
                         headerSizeValue, encoding, dataOffset: typeOffset, hsVarint);
    }

    // ── k=2: bytes 0-3 are P+R+headerSize+colType₀, cols 1..N-1 intact at byte 4 ─
    //
    // col-0's serial type is inferred from the mode of live cells' header entries —
    // rows in the same table almost always store column 0 with the same varint width.
    // If no live cells are available the freeblock size is used as a fallback
    // constraint (which only works reliably when one cell occupies the whole block).

    private static BTreeLeafCell? TryK2(
        byte[] pageData, int fbStart, int fbSize, long loPayload, long hiPayload,
        IReadOnlyList<BTreeLeafCell> liveCells,
        TextEncoding encoding, RecordStructure recordStructure)
    {
        int N = recordStructure.NumColumns;
        if (N < 2) return null;

        int typeOffset = fbStart + 4;
        var intactEntries = new List<HeaderEntry>(N - 1);
        for (int i = 1; i < N; i++)
        {
            if (typeOffset >= pageData.Length) return null;
            var tv    = Varint.ReadAt(pageData, typeOffset);
            var entry = new HeaderEntry(tv);
            if (!recordStructure.AllowedKindsPerColumn[i].Contains(entry.Kind)) return null;
            intactEntries.Add(entry);
            typeOffset += tv.Length;
        }

        int  intactTypeBytes    = intactEntries.Sum(e => e.RawValue.Length);
        long intactContentTotal = intactEntries.Sum(e => (long)e.ContentLength);
        int  dataOffset         = typeOffset;

        // Determine col-0's serial type.
        HeaderEntry col0;
        if (liveCells.Count > 0 && liveCells.All(c => c.HeaderEntries.Count > 0))
        {
            // Use the modal serial type of col-0 across all live cells.
            long modeSerial = Mode(liveCells.Select(c => (int)c.HeaderEntries[0].RawValue.Value));
            var candidate   = new HeaderEntry(new Varint(modeSerial, modeSerial < 128 ? 1 : 2));
            if (!recordStructure.AllowedKindsPerColumn[0].Contains(candidate.Kind)) return null;
            col0 = candidate;
        }
        else
        {
            // Fallback: derive from freeblock size (assumes the whole block is one cell).
            long expectedPayloadFb = fbSize - 2;
            if (expectedPayloadFb < loPayload || expectedPayloadFb > hiPayload) return null;
            long assumedHeaderSize = 2L + intactTypeBytes;
            long lostContentLen    = expectedPayloadFb - assumedHeaderSize - intactContentTotal;
            if (lostContentLen < 0) return null;
            var inferred = InferHeaderEntry((int)lostContentLen, recordStructure.AllowedKindsPerColumn[0]);
            if (inferred is null) return null;
            long actualHeaderSize = 1L + inferred.Value.RawValue.Length + intactTypeBytes;
            if (actualHeaderSize + lostContentLen + intactContentTotal != expectedPayloadFb) return null;
            col0 = inferred.Value;
        }

        long headerSizeValue = 1L + col0.RawValue.Length + intactTypeBytes;
        long payloadValue    = headerSizeValue + col0.ContentLength + intactContentTotal;

        if (payloadValue < loPayload || payloadValue > hiPayload) return null;
        if (2L + payloadValue > fbSize) return null;  // cell must fit inside the freeblock

        var allEntries = new List<HeaderEntry>(N) { col0 };
        allEntries.AddRange(intactEntries);

        var hsVarint = new Varint(headerSizeValue, headerSizeValue < 128 ? 1 : 2);
        // P=1, R=1 → CellByteLengthOnPage = 2 + payloadValue.
        return BuildCell(pageData, fbStart, 1, 1, payloadValue, allEntries,
                         headerSizeValue, encoding, dataOffset, hsVarint);
    }

    // ── Shared helpers ────────────────────────────────────────────────────────────

    private static BTreeLeafCell? BuildCell(
        byte[] pageData, int fbStart, int pLen, int rLen,
        long payloadValue, List<HeaderEntry> entries, long headerSizeValue,
        TextEncoding encoding, int dataOffset, Varint? headerSizeVarint = null)
    {
        int totalContent = entries.Sum(e => e.ContentLength);
        if (dataOffset + totalContent > pageData.Length) return null;

        var psVarint = new Varint(payloadValue, pLen);
        var ridVarint = new Varint(-1L, rLen);
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

    private static long Mode(IEnumerable<int> values) =>
        values.GroupBy(v => v).MaxBy(g => g.Count())!.Key;

    private static HeaderEntry? InferHeaderEntry(int contentLength, SerialTypeKind[] allowedKinds)
    {
        if (contentLength == 0)
        {
            if (allowedKinds.Contains(SerialTypeKind.Null))  return new HeaderEntry(new Varint(0L, 1));
            if (allowedKinds.Contains(SerialTypeKind.Int0))  return new HeaderEntry(new Varint(8L, 1));
            if (allowedKinds.Contains(SerialTypeKind.Int1))  return new HeaderEntry(new Varint(9L, 1));
        }

        // Integer serial types 1-6 → content lengths 1,2,3,4,6,8.
        long intSerial = contentLength switch { 1 => 1, 2 => 2, 3 => 3, 4 => 4, 6 => 5, 8 => 6, _ => -1 };
        if (intSerial > 0 && allowedKinds.Contains(SerialTypeKind.Integer))
            return new HeaderEntry(new Varint(intSerial, 1));

        if (contentLength == 8 && allowedKinds.Contains(SerialTypeKind.Float))
            return new HeaderEntry(new Varint(7L, 1));

        if (allowedKinds.Contains(SerialTypeKind.Text))
        {
            long serial = 13L + 2L * contentLength;
            return new HeaderEntry(new Varint(serial, serial < 128 ? 1 : 2));
        }

        if (allowedKinds.Contains(SerialTypeKind.Blob))
        {
            long serial = 12L + 2L * contentLength;
            return new HeaderEntry(new Varint(serial, serial < 128 ? 1 : 2));
        }

        return null;
    }
}
