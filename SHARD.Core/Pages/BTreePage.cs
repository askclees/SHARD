namespace SHARD.Core.Pages;

/// <summary>
/// A SQLite B-Tree page (interior or leaf, table or index).
///
/// Page header layout (relative to <see cref="SqlitePage.HeaderOffset"/>):
///   +0  : 1 byte  — page type (see <see cref="PageType"/>)
///   +1  : 2 bytes — offset of first freeblock (0 = none)
///   +3  : 2 bytes — number of cells on this page
///   +5  : 2 bytes — start of cell content area (0 means 65536)
///   +7  : 1 byte  — fragmented free bytes
///   +8  : 4 bytes — rightmost child pointer (interior pages only)
///
/// The cell pointer array follows immediately after the header.
/// Each entry is a 2-byte big-endian offset into the page.
/// </summary>
public sealed class BTreePage : SqlitePage
{
    public override PageType PageType { get; }

    public bool IsInterior => PageType is PageType.BTreeInteriorTable
                                       or PageType.BTreeInteriorIndex;
    public bool IsLeaf     => !IsInterior;
    public bool IsTable    => PageType is PageType.BTreeLeafTable
                                       or PageType.BTreeInteriorTable;
    public bool IsIndex    => !IsTable;

    // ── Header fields ────────────────────────────────────────────────────────
    /// <summary>Byte offset of the first freeblock; 0 if none.</summary>
    public ushort FirstFreeblock { get; }

    /// <summary>Number of cells on this page.</summary>
    public ushort CellCount { get; }

    /// <summary>
    /// Byte offset where cell content starts (grows toward the page header).
    /// A raw value of 0 is interpreted as 65536.
    /// </summary>
    public ushort CellContentAreaStart { get; }

    /// <summary>Number of fragmented free bytes within the cell content area.</summary>
    public byte FragmentedFreeBytes { get; }

    /// <summary>Rightmost child page number. Set only for interior pages.</summary>
    public uint? RightmostPointer { get; }

    // ── Cell pointer array ───────────────────────────────────────────────────
    /// <summary>
    /// Raw byte offsets (into this page) of each cell, in the order
    /// they appear in the pointer array. Count == <see cref="CellCount"/>.
    /// </summary>
    public ushort[] CellPointers { get; }

    // ── Constructor ──────────────────────────────────────────────────────────
    public BTreePage(uint pageNumber, int pageSize, byte[] data, PageType type)
        : base(pageNumber, pageSize, data)
    {
        PageType     = type;
        FirstFreeblock       = default;
        CellCount            = default;
        CellContentAreaStart = default;
        FragmentedFreeBytes  = default;
        RightmostPointer     = null;
        CellPointers         = [];
        throw new NotImplementedException();
    }

    /// <summary>
    /// Return the raw bytes starting at a cell's offset.
    /// Callers parse the variable-length content themselves using varint helpers.
    /// </summary>
    public ReadOnlySpan<byte> GetCellData(int cellIndex) =>
        throw new NotImplementedException();
}
