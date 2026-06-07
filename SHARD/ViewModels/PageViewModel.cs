using System.Collections.Generic;
using System.Text;
using Avalonia.Media;
using SHARD.Controls;
using SHARD.Core.Enums;
using SHARD.Core.Pages;

namespace SHARD.ViewModels;

/// <summary>ViewModel wrapping a single <see cref="SqlitePage"/> for display.</summary>
public sealed class PageViewModel : ViewModelBase
{
    public SqlitePage Page { get; }

    // ── List display ──────────────────────────────────────────────────────
    public uint   PageNumber => Page.PageNumber;
    public string TypeLabel  => Page.PageType.ToString();

    /// <summary>Colour swatch shown next to each page in the list.</summary>
    public IBrush TypeBrush => Page.PageType switch
    {
        PageType.BTreeLeafTable     => new SolidColorBrush(Color.Parse("#4A9ECA")),
        PageType.BTreeInteriorTable => new SolidColorBrush(Color.Parse("#2D7DB3")),
        PageType.BTreeLeafIndex     => new SolidColorBrush(Color.Parse("#4CAF82")),
        PageType.BTreeInteriorIndex => new SolidColorBrush(Color.Parse("#2E8B57")),
        PageType.FreelistTrunk      => new SolidColorBrush(Color.Parse("#E05C5C")),
        PageType.FreelistLeaf       => new SolidColorBrush(Color.Parse("#C0392B")),
        PageType.Overflow           => new SolidColorBrush(Color.Parse("#E0924A")),
        _                           => new SolidColorBrush(Color.Parse("#888888")),
    };

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
                sections.Add(new CellSectionViewModel(tlp.Cells[i], i));
            CellSections = sections;
        }
        else
        {
            CellSections = [];
        }
    }

    private static string BuildSummary(SqlitePage page)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Page       : {page.PageNumber}");
        sb.AppendLine($"Type       : {page.PageType}");
        sb.AppendLine($"Size       : {page.PageSize} bytes");

        if (page is not BTreePage bp) return sb.ToString();

        sb.AppendLine();
        sb.AppendLine($"First Freeblock  : {bp.FirstFreeblock}");
        sb.AppendLine($"Cell Count       : {bp.CellCount}");
        uint cca = bp.CellContentAreaStart == 0 ? 65536u : bp.CellContentAreaStart;
        sb.AppendLine($"Cell Content At  : {cca} (0x{cca:X4})");
        sb.AppendLine($"Fragmented Bytes : {bp.FragmentedFreeBytes}");
        if (bp is BTreeInteriorPage ip)
            sb.AppendLine($"Rightmost Ptr    : {ip.RightmostPointer}");

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

        return list;
    }
}
