using Avalonia.Media;
using SHARD.Core.Enums;

namespace SHARD.ViewModels;

/// <summary>Lightweight entry for the Pages tab's list — page number and type only, no retained page bytes.</summary>
public sealed class PageListEntryViewModel
{
    public uint     PageNumber          { get; }
    public PageType PageType            { get; }
    public string?  TableName           { get; }
    public bool     HasDeletedPointers  { get; }
    public bool     HasDeletedRecords   { get; }
    public IReadOnlyList<(int Size, int NonZeroBytes)> UnallocatedRegions { get; }

    /// <summary>
    /// The same regions, each with the non-zero bytes recovery leaves unexplained; an unclaimed
    /// page counts as one region covering the whole page. Null until counted, which the
    /// "unexplained only" filter does on demand as it means running recovery on every page.
    /// </summary>
    public IReadOnlyList<(int Size, int NonZeroBytes)>? UnexplainedRegions { get; set; }

    public string TypeLabel => PageType.ToString();
    public IBrush TypeBrush => PageTypeBrushes.For(PageType);

    public PageListEntryViewModel(uint pageNumber, PageType pageType, string? tableName = null,
        IReadOnlyList<(int Size, int NonZeroBytes)>? unallocatedRegions = null,
        bool hasDeletedPointers = false,
        bool hasDeletedRecords = false)
    {
        PageNumber         = pageNumber;
        PageType           = pageType;
        TableName          = tableName;
        UnallocatedRegions = unallocatedRegions ?? [];
        HasDeletedPointers = hasDeletedPointers;
        HasDeletedRecords  = hasDeletedRecords;
    }
}
