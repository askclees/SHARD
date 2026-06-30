using System.Collections.Generic;
using System.Text;
using Avalonia.Media;
using SHARD.Controls;
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

    // ── Cell pointers expander ────────────────────────────────────────────
    public bool HasCellPointers => Page is BTreePage bp && bp.CellPointers.Length > 0;

    public string CellPointerHeader => Page is BTreePage bpH
        ? $"Cell Pointers ({bpH.CellPointers.Length})"
        : "Cell Pointers";

    public IReadOnlyList<InfoRow> CellPointerRows { get; }

    // ── Per-cell expanders (table leaf pages only) ────────────────────────
    public IReadOnlyList<CellSectionViewModel> CellSections { get; }

    // ── Freeblock expanders (table leaf pages only) ───────────────────────
    public IReadOnlyList<FreeBlockSectionViewModel> FreeBlockSections { get; }
    public bool HasFreeBlocks => FreeBlockSections.Count > 0;

    // ── Unallocated region expanders (table leaf pages only) ──────────────
    public IReadOnlyList<UnallocatedRegionSectionViewModel> UnallocatedRegionSections { get; }
    public bool HasUnallocatedRegions => UnallocatedRegionSections.Count > 0;

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

            var fbSections = new List<FreeBlockSectionViewModel>(tlp.FreeBlocks.Count);
            for (int i = 0; i < tlp.FreeBlocks.Count; i++)
                fbSections.Add(new FreeBlockSectionViewModel(tlp.FreeBlocks[i], i));
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
            CellSections = sections;
            FreeBlockSections         = [];
            UnallocatedRegionSections = [];
        }
        else
        {
            CellSections              = [];
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
        if (page is not BTreePage bp) return [];

        var list = new List<HexHighlight>();
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

        if (page is TableBTreeLeafPage tlp)
        {
            for (int j = 0; j < tlp.Cells.Count; j++)
            {
                var cell      = tlp.Cells[j];
                int cellStart = tlp.CellPointers[j];
                int dataStart = cellStart
                                + cell.SizeOfPayload.Length
                                + cell.RowId.Length
                                + (int)cell.HeaderSize.Value;

                int fieldOffset = dataStart;
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
                int dataStart = cellStart
                                + cell.SizeOfPayload.Length
                                + (int)cell.HeaderSize.Value;

                int fieldOffset = dataStart;
                for (int i = 0; i < cell.HeaderEntries.Count; i++)
                {
                    int len = cell.HeaderEntries[i].ContentLength;
                    if (len > 0)
                        list.Add(new HexHighlight(fieldOffset, len, ColumnColour(i), $"Cell {j} · Col {i}"));
                    fieldOffset += len;
                }
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
            for (int i = 0; i < tlpFb.UnallocatedRegions.Count; i++)
            {
                var region = tlpFb.UnallocatedRegions[i];
                if (region.Size > 0)
                    list.Add(new HexHighlight(region.Offset, region.Size, unallocColour, $"Unallocated Region {i}"));
            }
        }

        return list;
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
