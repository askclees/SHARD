using System.Buffers.Binary;
using SHARD.Core.Enums;

namespace SHARD.Core.Pages;

/// <summary>
/// Abstract base for all SQLite B-Tree pages (interior and leaf, table and index).
///
/// Page header layout (relative to <see cref="SqlitePage.HeaderOffset"/>):
///   +0  : 1 byte  — page type flag
///   +1  : 2 bytes — offset of first freeblock (0 = none)
///   +3  : 2 bytes — number of cells on this page
///   +5  : 2 bytes — start of cell content area (0 means 65536)
///   +7  : 1 byte  — fragmented free bytes
///   +8  : 4 bytes — rightmost child pointer (interior pages only)
///
/// The cell pointer array follows immediately after the header.
/// Each entry is a 2-byte big-endian offset into the page.
/// </summary>
public abstract class BTreePage : SqlitePage
{
    public abstract override PageType PageType { get; }

    public bool IsInterior => this is BTreeInteriorPage;
    public bool IsLeaf     => this is BTreeLeafPage;
    public bool IsTable    => PageType is PageType.BTreeLeafTable or PageType.BTreeInteriorTable;
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

    // ── Cell pointer array ───────────────────────────────────────────────────
    /// <summary>
    /// Raw byte offsets (into this page) of each cell, in the order
    /// they appear in the pointer array. Count == <see cref="CellCount"/>.
    /// </summary>
    public ushort[] CellPointers { get; }

    public List<ushort> DeletedCellPointers { get; } = new();

    // ── Constructor ──────────────────────────────────────────────────────────
    /// <param name="cellPointerStart">
    /// Byte offset within <paramref name="data"/> where the cell pointer array begins.
    /// Pass <c>HeaderOffset + 8</c> for leaf pages, <c>HeaderOffset + 12</c> for interior pages.
    /// </param>
    protected BTreePage(uint pageNumber, int pageSize, byte[] data, int cellPointerStart)
        : base(pageNumber, pageSize, data)
    {
        if (data.Length != pageSize)
            throw new InvalidDataException("Data must be equal to pagesize");

        int h = HeaderOffset;
        FirstFreeblock       = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(h + 1, 2));
        CellCount            = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(h + 3, 2));
        CellContentAreaStart = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(h + 5, 2));
        FragmentedFreeBytes  = data[h + 7];

        CellPointers = new ushort[CellCount];
        for (int i = 0; i < CellCount; i++)
            CellPointers[i] = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(cellPointerStart + i * 2, 2));

        bool foundZeroBytes = false;
        int pointer = cellPointerStart + (CellCount * 2);
        int cellEnd = CellContentAreaStart == 0 ? 65536 : CellContentAreaStart;
        while (!foundZeroBytes && pointer < cellEnd)
        {
            ushort deletedPointer = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pointer, 2));
            if (deletedPointer == 0)
            {
                foundZeroBytes = true;
            }
            else
            {
                if (!CellPointers.Contains(deletedPointer))
                    DeletedCellPointers.Add(deletedPointer);
            }
            pointer += 2;
        }
    }
}
