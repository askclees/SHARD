using System.Collections.Generic;
using System.Text;
using Avalonia.Media;
using ReactiveUI;
using SHARD.Controls;
using SHARD.Core.Decoding;
using SHARD.Core.Enums;
using SHARD.Core.Pages;
using SHARD.Core.Records;

namespace SHARD.ViewModels;

/// <summary>ViewModel wrapping a single <see cref="SqlitePage"/> for display.</summary>
public sealed class PageViewModel : ViewModelBase
{
    public SqlitePage Page { get; }

    // ── List display ──────────────────────────────────────────────────────
    public uint   PageNumber => Page.PageNumber;
    public string TypeLabel  => Page.PageType.ToString();

    /// <summary>Colour swatch shown next to each page in the list.</summary>
    public IBrush TypeBrush => PageTypeBrushes.For(Page.PageType);

    // ── Detail panel ──────────────────────────────────────────────────────
    public string Summary => BuildSummary(Page);

    public byte[] PageBytes => Page.Data;

    public IReadOnlyList<HexHighlight> PageHighlights => BuildPageHighlights(Page);

    /// <summary>Raise PropertyChanged for PageHighlights so the hex view re-renders after annotations are added.</summary>
    public void RefreshHighlights() => this.RaisePropertyChanged(nameof(PageHighlights));

    // ── Cell pointers expander ────────────────────────────────────────────
    public bool HasCellPointers => Page is BTreePage bp && bp.CellPointers.Length > 0;

    public string CellPointerHeader => Page is BTreePage bpH
        ? $"Cell Pointers ({bpH.CellPointers.Length})"
        : "Cell Pointers";

    public IReadOnlyList<InfoRow> CellPointerRows { get; }

    // ── Per-cell expanders (table leaf pages only) ────────────────────────
    public IReadOnlyList<CellSectionViewModel> CellSections { get; }
    public IReadOnlyList<CellSectionViewModel> DeletedCellSections { get; }

    // ── Freeblock expanders (table leaf pages only) ───────────────────────
    public IReadOnlyList<FreeBlockSectionViewModel> FreeBlockSections { get; }
    public bool HasFreeBlocks => FreeBlockSections.Count > 0;

    // ── Unallocated region expanders (table leaf pages only) ──────────────
    public IReadOnlyList<UnallocatedRegionSectionViewModel> UnallocatedRegionSections { get; }
    public bool HasUnallocatedRegions => UnallocatedRegionSections.Count > 0;

    // ── Tab headers with counts ───────────────────────────────────────────
    public string CellsTabHeader         => CellSections.Count > 0        ? $"Cells ({CellSections.Count})"               : "Cells";
    public string DeletedCellsTabHeader  => DeletedCellSections.Count > 0 ? $"Potential Deleted ({DeletedCellSections.Count})" : "Potential Deleted";
    public string FreeBlocksTabHeader  => FreeBlockSections.Count > 0 ? $"Freeblocks ({FreeBlockSections.Count})"  : "Freeblocks";
    public string UnallocatedTabHeader => UnallocatedRegionSections.Count > 0
        ? $"Unallocated ({UnallocatedRegionSections.Count})"
        : "Unallocated";

    public PageViewModel(SqlitePage page)
    {
        Page = page;

        if (page is BTreePage bp)
        {
            var rows = new List<InfoRow>(bp.CellPointers.Length);
            for (int i = 0; i < bp.CellPointers.Length; i++)
                rows.Add(new InfoRow($"[{i}]", $"0x{bp.CellPointers[i]:X4}  ({bp.CellPointers[i]})"));
            CellPointerRows = rows;
        }
        else
        {
            CellPointerRows = [];
        }

        if (page is TableBTreeLeafPage tlp)
        {
            var sections = new List<CellSectionViewModel>(tlp.Cells.Count);
            for (int i = 0; i < tlp.Cells.Count; i++)
                sections.Add(new CellSectionViewModel(tlp.Cells[i], i, tlp.CellPointers[i]));
            CellSections = sections;

            var deletedSections = new List<CellSectionViewModel>(tlp.DeletedCells.Count);
            for (int i = 0; i < tlp.DeletedCells.Count; i++)
                deletedSections.Add(new CellSectionViewModel(tlp.DeletedCells[i], i, tlp.DeletedCells[i].PageOffset));
            DeletedCellSections = deletedSections;

            var fbSections = new List<FreeBlockSectionViewModel>(tlp.FreeBlocks.Count);
            for (int i = 0; i < tlp.FreeBlocks.Count; i++)
                fbSections.Add(new FreeBlockSectionViewModel(tlp.FreeBlocks[i], i, tlp.FreeblockCells));
            FreeBlockSections = fbSections;

            var urSections = new List<UnallocatedRegionSectionViewModel>(tlp.UnallocatedRegions.Count);
            for (int i = 0; i < tlp.UnallocatedRegions.Count; i++)
                urSections.Add(new UnallocatedRegionSectionViewModel(tlp.UnallocatedRegions[i], i));
            UnallocatedRegionSections = urSections;
        }
        else if (page is IndexBTreeLeafPage ilp)
        {
            var sections = new List<CellSectionViewModel>(ilp.Cells.Count);
            for (int i = 0; i < ilp.Cells.Count; i++)
                sections.Add(new CellSectionViewModel(ilp.Cells[i], i, ilp.CellPointers[i]));
            CellSections              = sections;
            DeletedCellSections       = [];
            FreeBlockSections         = [];
            UnallocatedRegionSections = [];
        }
        else
        {
            CellSections              = [];
            DeletedCellSections       = [];
            FreeBlockSections         = [];
            UnallocatedRegionSections = [];
        }
    }

    private static string BuildSummary(SqlitePage page)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Page       : {page.PageNumber}");
        sb.AppendLine($"Type       : {page.PageType}");
        sb.AppendLine($"Size       : {page.PageSize} bytes");

        if (page is UnknownPage unknown)
        {
            if (unknown.DeclaredTypeByte != PageType.Unknown)
                sb.AppendLine($"Type byte  : 0x{(byte)unknown.DeclaredTypeByte:X2} ({unknown.DeclaredTypeByte}) — parse failed");
            if (unknown.ParseError is not null)
                sb.AppendLine($"Parse error: {unknown.ParseError.GetType().Name}: {unknown.ParseError.Message}");
            return sb.ToString();
        }

        if (page is not BTreePage bp) return sb.ToString();

        sb.AppendLine();
        sb.AppendLine($"First Freeblock  : {bp.FirstFreeblock}");
        sb.AppendLine($"Cell Count       : {bp.CellCount}");
        uint cca = bp.CellContentAreaStart == 0 ? 65536u : bp.CellContentAreaStart;
        sb.AppendLine($"Cell Content At  : {cca} (0x{cca:X4})");
        sb.AppendLine($"Fragmented Bytes : {bp.FragmentedFreeBytes}");
        if (bp is BTreeInteriorPage ip)
            sb.AppendLine($"Rightmost Ptr    : {ip.RightmostPointer}");

        if (page is TableBTreeLeafPage tlp)
            sb.AppendLine($"Freeblocks       : {tlp.FreeBlocks.Count}");

        return sb.ToString();
    }

    private static IReadOnlyList<HexHighlight> BuildPageHighlights(SqlitePage page)
    {
        var list = new List<HexHighlight>();

        // Cells carved from freed pages — emit before early return so they show on non-BTree pages.
        foreach (var cell in page.CarvedRecoveredCells)
            AddDeletedCellHighlights(list, cell, "Recovered",
                Color.FromRgb( 60, 180, 100),
                Color.FromRgb( 40, 150,  70),
                Color.FromRgb( 40, 160, 180));

        if (page is not BTreePage bp) return list;

        int h = page.HeaderOffset;

        list.Add(new(h + 0, 1, Color.FromRgb( 86, 156, 214), "Page Type"));
        list.Add(new(h + 1, 2, Color.FromRgb( 78, 201, 176), "First Freeblock"));
        list.Add(new(h + 3, 2, Color.FromRgb(220, 220, 170), "Cell Count"));
        list.Add(new(h + 5, 2, Color.FromRgb(206, 145, 120), "Cell Content Area"));
        list.Add(new(h + 7, 1, Color.FromRgb(155, 155, 155), "Fragmented Bytes"));

        int cellPtrStart = h + 8;
        if (bp.IsInterior)
        {
            list.Add(new(h + 8, 4, Color.FromRgb(205, 92, 92), "Rightmost Pointer"));
            cellPtrStart = h + 12;
        }

        for (int i = 0; i < bp.CellPointers.Length; i++)
            list.Add(new(cellPtrStart + i * 2, 2, Color.FromRgb(106, 153, 85), $"Cell Pointer {i}"));

        var payloadSizeColour = Color.FromRgb(180,  80,  80);
        var rowIdColour       = Color.FromRgb(218, 165,  32);
        var headerColourCell  = Color.FromRgb( 70, 170, 210);

        if (page is TableBTreeLeafPage tlp)
        {
            for (int j = 0; j < tlp.Cells.Count; j++)
            {
                var cell      = tlp.Cells[j];
                int cellStart = tlp.CellPointers[j];

                list.Add(new HexHighlight(cellStart, cell.SizeOfPayload.Length,
                    payloadSizeColour, $"Row {cell.RowId.Value} · Payload Size"));

                int rowIdStart = cellStart + cell.SizeOfPayload.Length;
                list.Add(new HexHighlight(rowIdStart, cell.RowId.Length,
                    rowIdColour, $"Row {cell.RowId.Value} · Row ID"));

                int headerStart = rowIdStart + cell.RowId.Length;
                list.Add(new HexHighlight(headerStart, (int)cell.HeaderSize.Value,
                    headerColourCell, $"Row {cell.RowId.Value} · Record Header"));

                int fieldOffset = headerStart + (int)cell.HeaderSize.Value;
                for (int i = 0; i < cell.HeaderEntries.Count; i++)
                {
                    int len = cell.HeaderEntries[i].ContentLength;
                    if (len > 0)
                        list.Add(new HexHighlight(fieldOffset, len, ColumnColour(i), $"Row {cell.RowId.Value} · Col {i}"));
                    fieldOffset += len;
                }
            }
        }
        else if (page is IndexBTreeLeafPage ilp)
        {
            for (int j = 0; j < ilp.Cells.Count; j++)
            {
                var cell      = ilp.Cells[j];
                int cellStart = ilp.CellPointers[j];

                list.Add(new HexHighlight(cellStart, cell.SizeOfPayload.Length,
                    payloadSizeColour, $"Cell {j} · Payload Size"));

                int headerStart = cellStart + cell.SizeOfPayload.Length;
                list.Add(new HexHighlight(headerStart, (int)cell.HeaderSize.Value,
                    headerColourCell, $"Cell {j} · Record Header"));

                int fieldOffset = headerStart + (int)cell.HeaderSize.Value;
                for (int i = 0; i < cell.HeaderEntries.Count; i++)
                {
                    int len = cell.HeaderEntries[i].ContentLength;
                    if (len > 0)
                        list.Add(new HexHighlight(fieldOffset, len, ColumnColour(i), $"Cell {j} · Col {i}"));
                    fieldOffset += len;
                }
            }
        }

        if (page is TableBTreeInteriorPage tip)
        {
            var childPtrColour   = Color.FromRgb(205,  92,  92); // same red as rightmost pointer
            var dividerKeyColour = Color.FromRgb(218, 165,  32); // same gold as leaf rowid
            for (int i = 0; i < tip.Cells.Count; i++)
            {
                int cellOff   = tip.CellPointers[i];
                uint childPage = tip.Cells[i].PageNumber;
                list.Add(new HexHighlight(cellOff, 4, childPtrColour, $"Cell {i} · Child Page {childPage}"));
                var divider = Varint.ReadAt(tip.Data, cellOff + 4);
                list.Add(new HexHighlight(cellOff + 4, divider.Length, dividerKeyColour, $"Cell {i} · Divider Key {divider.Value}"));
            }
        }
        else if (page is IndexBTreeInteriorPage iip)
        {
            var childPtrColour = Color.FromRgb(205, 92, 92);
            for (int i = 0; i < iip.CellPointers.Length; i++)
            {
                int cellOff    = iip.CellPointers[i];
                uint childPage = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(iip.Data.AsSpan(cellOff, 4));
                list.Add(new HexHighlight(cellOff, 4, childPtrColour, $"Cell {i} · Child Page {childPage}"));
            }
        }

        if (page is TableBTreeLeafPage tlpFb)
        {
            var headerColour  = Color.FromRgb(180,  80, 200);
            var contentColour = Color.FromRgb(210, 150, 230);
            foreach (var fb in tlpFb.FreeBlocks)
            {
                list.Add(new HexHighlight((int)fb.PageOffset,     4,                        headerColour,  $"Freeblock header"));
                int contentLen = (int)fb.BlockSize - 4;
                if (contentLen > 0)
                    list.Add(new HexHighlight((int)fb.PageOffset + 4, contentLen, contentColour, $"Freeblock content"));
            }

            var unallocColour = Color.FromRgb(255, 165, 0);
            var recoveredRanges = tlpFb.DeletedCells
                .Concat(tlpFb.CarvedCells)
                .Concat(tlpFb.FreeblockCells)
                .Concat(tlpFb.AnnotatedCells)
                .Select(c => (Start: c.PageOffset, End: c.PageOffset + c.CellByteLengthOnPage))
                .OrderBy(r => r.Start)
                .ToList();

            for (int i = 0; i < tlpFb.UnallocatedRegions.Count; i++)
            {
                var region = tlpFb.UnallocatedRegions[i];
                if (region.Size <= 0) continue;

                int pos = region.Offset;
                int regionEnd = region.Offset + region.Size;
                foreach (var (rStart, rEnd) in recoveredRanges)
                {
                    if (rStart >= regionEnd) break;
                    if (rEnd <= pos) continue;
                    if (pos < rStart)
                        list.Add(new HexHighlight(pos, rStart - pos, unallocColour, $"Unallocated Region {i}"));
                    pos = Math.Max(pos, rEnd);
                }
                if (pos < regionEnd)
                    list.Add(new HexHighlight(pos, regionEnd - pos, unallocColour, $"Unallocated Region {i}"));
            }

            // Deleted cells (via removed cell pointers) — layered on top of unallocated
            foreach (var cell in tlpFb.DeletedCells)
                AddDeletedCellHighlights(list, cell, "Deleted",
                    Color.FromRgb(210, 60,  60),
                    Color.FromRgb(180, 110, 20),
                    Color.FromRgb( 50, 130, 160));

            // Carved cells (found by scanning unallocated space)
            foreach (var cell in tlpFb.CarvedCells)
                AddDeletedCellHighlights(list, cell, "Carved",
                    Color.FromRgb( 60, 180, 100),
                    Color.FromRgb( 40, 150,  70),
                    Color.FromRgb( 40, 160, 180));

            // Freeblock cells (recovered from freeblocks) — highlight full range only
            foreach (var cell in tlpFb.FreeblockCells)
            {
                string rowLabel = cell.RowId.Value >= 0
                    ? $"Freeblock · Row {cell.RowId.Value}"
                    : "Freeblock · Row unknown";
                list.Add(new HexHighlight(cell.PageOffset, cell.CellByteLengthOnPage,
                    Color.FromRgb(60, 140, 220), rowLabel));
            }

            // Manually annotated cells — purple, component breakdown
            foreach (var cell in tlpFb.AnnotatedCells)
                AddDeletedCellHighlights(list, cell, "Annotated",
                    Color.FromRgb(180,  90, 240),
                    Color.FromRgb(140,  60, 210),
                    Color.FromRgb(210, 150, 255));
        }

        return list;
    }

    private static void AddDeletedCellHighlights(List<HexHighlight> list, BTreeLeafCell cell,
        string kind, Color payloadColour, Color rowIdColour, Color headerColour)
    {
        string prefix = $"{kind} · Row {cell.RowId.Value}";
        int pos = cell.PageOffset;

        list.Add(new HexHighlight(pos, cell.SizeOfPayload.Length, payloadColour, $"{prefix} · Payload Size"));
        pos += cell.SizeOfPayload.Length;

        list.Add(new HexHighlight(pos, cell.RowId.Length, rowIdColour, $"{prefix} · Row ID"));
        pos += cell.RowId.Length;

        list.Add(new HexHighlight(pos, (int)cell.HeaderSize.Value, headerColour, $"{prefix} · Record Header"));
        pos += (int)cell.HeaderSize.Value;

        for (int i = 0; i < cell.HeaderEntries.Count; i++)
        {
            int len = cell.HeaderEntries[i].ContentLength;
            if (len > 0)
                list.Add(new HexHighlight(pos, len, ColumnColour(i), $"{prefix} · Col {i}"));
            pos += len;
        }
    }

    private static Color ColumnColour(int columnIndex)
    {
        double hue = columnIndex * 137.508 % 360.0;
        const double s = 0.65;
        const double l = 0.55;

        double c = (1.0 - Math.Abs(2.0 * l - 1.0)) * s;
        double x = c * (1.0 - Math.Abs(hue / 60.0 % 2.0 - 1.0));
        double m = l - c / 2.0;

        double r, g, b;
        if      (hue < 60)  { r = c; g = x; b = 0; }
        else if (hue < 120) { r = x; g = c; b = 0; }
        else if (hue < 180) { r = 0; g = c; b = x; }
        else if (hue < 240) { r = 0; g = x; b = c; }
        else if (hue < 300) { r = x; g = 0; b = c; }
        else                { r = c; g = 0; b = x; }

        return Color.FromRgb(
            (byte)((r + m) * 255),
            (byte)((g + m) * 255),
            (byte)((b + m) * 255));
    }
}
