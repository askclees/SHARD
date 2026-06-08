using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;
using SHARD.Controls;

namespace SHARD.ViewModels;

public sealed class SearchPageGroupViewModel
{
    public uint   PageNumber { get; }
    public string Header     { get; }
    public byte[] PageBytes  { get; }

    public IReadOnlyList<SearchHitViewModel>  Hits       { get; }
    public IReadOnlyList<HexHighlight>        Highlights { get; }

    public SearchPageGroupViewModel(uint pageNumber, byte[] pageBytes, IReadOnlyList<SearchHitViewModel> hits)
    {
        PageNumber = pageNumber;
        PageBytes  = pageBytes;
        Hits       = hits;
        Header     = $"Page {pageNumber}  —  {hits.Count} hit{(hits.Count == 1 ? "" : "s")}";

        var colour = Color.FromRgb(255, 215, 0); // gold
        Highlights = hits
            .Select(h => new HexHighlight(h.Offset, Math.Max(1, h.Length), colour, $"0x{h.Offset:X4}"))
            .ToList();
    }
}
