using Avalonia.Media;
using SHARD.Core.Enums;

namespace SHARD.ViewModels;

/// <summary>Lightweight entry for the Pages tab's list — page number and type only, no retained page bytes.</summary>
public sealed class PageListEntryViewModel
{
    public uint     PageNumber  { get; }
    public PageType PageType    { get; }
    public string?  TableName   { get; }
    public IReadOnlyList<(int Size, int NonZeroBytes)> UnallocatedRegions { get; }

    public string TypeLabel => PageType.ToString();
    public IBrush TypeBrush => PageTypeBrushes.For(PageType);

    public PageListEntryViewModel(uint pageNumber, PageType pageType, string? tableName = null,
        IReadOnlyList<(int Size, int NonZeroBytes)>? unallocatedRegions = null)
    {
        PageNumber         = pageNumber;
        PageType           = pageType;
        TableName          = tableName;
        UnallocatedRegions = unallocatedRegions ?? [];
    }
}
