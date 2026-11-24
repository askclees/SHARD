namespace SHARD.Core;

/// <summary>
/// Which page numbers SQLite reserves for something other than a B-tree, freelist or overflow
/// page. Neither kind can be recognised from its bytes, only from its number and the database
/// header:
/// <list type="bullet">
/// <item>The <b>lock-byte page</b> holds file offset 1,073,741,824 (SQLite's PENDING_BYTE), which
/// is used only for file locks. SQLite never writes to it; it exists only in databases larger than
/// 1 GiB.</item>
/// <item><b>Pointer-map pages</b> exist only in auto-vacuum databases (header offset 52 non-zero).
/// Each holds a 5-byte entry (type, parent page) for each of the <see cref="PointerMapEntriesPerPage"/>
/// pages after it. The first is page 2 and each one is followed by the pages it covers, so they
/// fall every <see cref="PointerMapEntriesPerPage"/> + 1 pages, skipping the lock-byte page.</item>
/// </list>
/// The rules mirror SQLite's <c>PENDING_BYTE_PAGE</c>, <c>ptrmapPageno</c> and <c>PTRMAP_ISPAGE</c>
/// (btree.c).
/// </summary>
public sealed record PageLayout(int PageSize, int ReservedBytes, bool AutoVacuum)
{
    private const long PendingByte = 0x40000000;

    public static PageLayout From(DatabaseHeader header) =>
        new(header.PageSize, header.ReservedBytesPerPage, header.LargestRootBTreePage != 0);

    /// <summary>Bytes of each page SQLite uses: the page size less the reserved bytes at its end.</summary>
    public int UsableSize => PageSize - ReservedBytes;

    /// <summary>The page holding the lock bytes.</summary>
    public uint LockBytePageNumber => (uint)(PendingByte / PageSize) + 1;

    /// <summary>How many pages one pointer-map page covers (one 5-byte entry each).</summary>
    public int PointerMapEntriesPerPage => UsableSize / 5;

    public bool IsLockBytePage(uint pageNumber) => pageNumber == LockBytePageNumber;

    /// <summary>
    /// The pointer-map page holding <paramref name="pageNumber"/>'s entry (or, for a pointer-map
    /// page, the page itself), or 0 for page 1, which has none. Defined whether or not the
    /// database is auto-vacuum; check <see cref="AutoVacuum"/> before relying on it.
    /// </summary>
    public uint PointerMapPageFor(uint pageNumber)
    {
        if (pageNumber < 2) return 0;
        uint pagesPerMapPage = (uint)PointerMapEntriesPerPage + 1;
        uint mapPage = (pageNumber - 2) / pagesPerMapPage * pagesPerMapPage + 2;
        if (mapPage == LockBytePageNumber) mapPage++;
        return mapPage;
    }

    public bool IsPointerMapPage(uint pageNumber) =>
        AutoVacuum && pageNumber >= 2 && PointerMapPageFor(pageNumber) == pageNumber;
}
