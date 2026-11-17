using SHARD.Core.Pages;
using SHARD.Core.Records;

namespace SHARD.Core.Analysis;

/// <summary>A half-open byte range <c>[Start, End)</c> within a page.</summary>
public readonly record struct ByteRange(int Start, int End)
{
    public int Length => End - Start;

    public static ByteRange Of(BTreeLeafCell cell) => new(cell.PageOffset, cell.PageOffset + cell.CellByteLengthOnPage);
}

/// <summary>
/// How many of a page area's non-zero bytes something accounts for. <see cref="NonZeroBytes"/>
/// counts every non-zero byte in the area; <see cref="UnexplainedNonZeroBytes"/> counts only those
/// that no recovered cell or SQLite structure covers.
/// </summary>
public readonly record struct ResidualCount(int NonZeroBytes, int UnexplainedNonZeroBytes)
{
    public int ExplainedNonZeroBytes => NonZeroBytes - UnexplainedNonZeroBytes;

    public static ResidualCount operator +(ResidualCount a, ResidualCount b) =>
        new(a.NonZeroBytes + b.NonZeroBytes, a.UnexplainedNonZeroBytes + b.UnexplainedNonZeroBytes);
}

/// <summary>
/// Counts the non-zero bytes left after recovery: the ones no recovered cell and no SQLite
/// structure accounts for. A page whose count is 0 has nothing left that recovery hasn't explained;
/// a page with a high count has data recovery couldn't read (a format it doesn't handle, damaged
/// or partly overwritten records, or a record from a table it wasn't given).
/// </summary>
public static class ResidualBytes
{
    /// <summary>
    /// Counts the non-zero bytes in <c>[start, end)</c> of <paramref name="data"/>, and how many of
    /// them fall outside every range in <paramref name="explained"/>. Ranges may overlap or extend
    /// past the area; each byte is counted once.
    /// </summary>
    public static ResidualCount Count(ReadOnlySpan<byte> data, int start, int end, IEnumerable<ByteRange> explained)
    {
        start = Math.Clamp(start, 0, data.Length);
        end   = Math.Clamp(end, start, data.Length);
        var covered = new bool[end - start];
        foreach (var range in explained)
        {
            int from = Math.Max(range.Start, start);
            int to   = Math.Min(range.End, end);
            for (int i = from; i < to; i++) covered[i - start] = true;
        }

        int nonZero = 0, unexplained = 0;
        for (int i = start; i < end; i++)
        {
            if (data[i] == 0) continue;
            nonZero++;
            if (!covered[i - start]) unexplained++;
        }
        return new ResidualCount(nonZero, unexplained);
    }

    /// <summary>
    /// The parts of <paramref name="page"/> that hold SQLite's own bookkeeping rather than record
    /// data, so are explained without any recovery:
    /// <list type="bullet">
    /// <item>the reserved bytes at the end of the page;</item>
    /// <item>on page 1, the 100-byte database header;</item>
    /// <item>on a B-tree page, its page header, cell-pointer array and the stale pointer slots after
    /// it (see <see cref="BTreePage.StaleCellPointerArrayEnd"/>), and each freeblock's 4-byte
    /// header;</item>
    /// <item>on a freelist trunk page, its header and leaf page list.</item>
    /// </list>
    /// </summary>
    public static IEnumerable<ByteRange> StructureRanges(SqlitePage page, int reservedBytes)
    {
        int size = page.Data.Length;
        if (reservedBytes > 0 && reservedBytes < size)
            yield return new ByteRange(size - reservedBytes, size);

        if (page.HeaderOffset > 0)
            yield return new ByteRange(0, page.HeaderOffset);

        switch (page)
        {
            case BTreePage bp:
                yield return new ByteRange(page.HeaderOffset, bp.StaleCellPointerArrayEnd);
                if (page is TableBTreeLeafPage tlp)
                    foreach (var fb in tlp.FreeBlocks)
                        yield return new ByteRange((int)fb.PageOffset, (int)fb.PageOffset + 4);
                break;
            case FreelistTrunkPage trunk:
                yield return new ByteRange(0, 8 + trunk.LeafPageNumbers.Length * 4);
                break;
        }
    }

    /// <summary>
    /// Counts the whole of <paramref name="page"/> (less its reserved bytes, which are counted as
    /// explained), explaining <paramref name="cells"/>, the page's own structure, and any surviving
    /// cell pointers to <paramref name="cells"/> (see <see cref="PointersTo"/>). Used for pages
    /// carved as a whole, such as those the orphan-page carver scans.
    /// </summary>
    public static ResidualCount ForWholePage(SqlitePage page, int reservedBytes, IEnumerable<BTreeLeafCell> cells)
    {
        var cellList = cells as IReadOnlyCollection<BTreeLeafCell> ?? cells.ToList();
        return Count(page.Data, 0, page.Data.Length,
            StructureRanges(page, reservedBytes)
                .Concat(cellList.Select(ByteRange.Of))
                .Concat(PointersTo(page.Data, cellList)));
    }

    /// <summary>
    /// The 2-byte slots before the first of <paramref name="cells"/> whose big-endian value is the
    /// offset of one of them: the cell-pointer array of the B-tree page the cells were on, which
    /// survives when the page is freed or loses its header (a freelist trunk overwrites only the
    /// first 8 bytes and its leaf list). Slots are at even offsets, as a pointer array starts at
    /// offset 8 or 12.
    /// </summary>
    public static IEnumerable<ByteRange> PointersTo(byte[] data, IReadOnlyCollection<BTreeLeafCell> cells)
    {
        if (cells.Count == 0) yield break;
        var offsets = cells.Select(c => c.PageOffset).ToHashSet();
        int firstCell = offsets.Min();
        for (int i = 0; i + 1 < firstCell && i + 1 < data.Length; i += 2)
        {
            int value = (data[i] << 8) | data[i + 1];
            if (offsets.Contains(value))
                yield return new ByteRange(i, i + 2);
        }
    }
}
