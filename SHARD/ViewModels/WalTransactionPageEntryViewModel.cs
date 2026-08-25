namespace SHARD.ViewModels;

/// <summary>One page's comparison within a whole-transaction view — the same shape a single
/// frame's Changes tab shows, just repeated per page and labelled with its page number/table.</summary>
public sealed class WalTransactionPageEntryViewModel(uint pageNumber, string? tableName, WalPageComparisonViewModel? comparison)
{
    public uint PageNumber { get; } = pageNumber;
    public string TableName { get; } = tableName ?? string.Empty;
    public bool HasTable { get; } = !string.IsNullOrEmpty(tableName);
    public WalPageComparisonViewModel? Comparison { get; } = comparison;
    public bool HasComparison => Comparison is not null;

    public string Header => HasTable ? $"Page {PageNumber} ({TableName})" : $"Page {PageNumber}";
}
