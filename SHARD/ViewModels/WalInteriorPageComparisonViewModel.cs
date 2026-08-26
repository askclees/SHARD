using SHARD.Core.Comparison;

namespace SHARD.ViewModels;

public sealed class WalInteriorPageComparisonViewModel : IWalPageComparisonViewModel
{
    public string ComparedAgainst { get; }
    public bool HasAnyChanges { get; }
    public bool HasAdded { get; }
    public bool HasRemoved { get; }
    public bool HasUpdated { get; }
    public bool HasRightPointerChange { get; }
    public string AddedHeader { get; }
    public string RemovedHeader { get; }
    public string UpdatedHeader { get; }
    public string? RightPointerChangeText { get; }
    public IReadOnlyList<InfoRow> AddedRows { get; }
    public IReadOnlyList<InfoRow> RemovedRows { get; }
    public IReadOnlyList<InfoRow> UpdatedRows { get; }

    public WalInteriorPageComparisonViewModel(TableBTreeInteriorPageComparison comparison, string comparedAgainst)
    {
        ComparedAgainst = comparedAgainst;

        AddedRows = comparison.AddedRecords
            .Select(c => new InfoRow("Added child page", $"{c.PageNumber}  (key {c.RecordId})"))
            .ToList();
        RemovedRows = comparison.RemovedRecords
            .Select(c => new InfoRow("Removed child page", $"{c.PageNumber}  (key {c.RecordId})"))
            .ToList();
        UpdatedRows = comparison.UpdatedRecords
            .Select(c => new InfoRow($"Child page {c.PageNumber}", $"key {c.PreviousRecordId}  →  {c.NewRecordId}"))
            .ToList();

        HasAdded   = AddedRows.Count > 0;
        HasRemoved = RemovedRows.Count > 0;
        HasUpdated = UpdatedRows.Count > 0;

        HasRightPointerChange = comparison.PreviousRightPointer.HasValue;
        RightPointerChangeText = HasRightPointerChange
            ? $"{comparison.PreviousRightPointer}  →  {comparison.NewRightPointer}"
            : null;

        HasAnyChanges = HasAdded || HasRemoved || HasUpdated || HasRightPointerChange;

        AddedHeader   = $"Added child pointers ({AddedRows.Count})";
        RemovedHeader = $"Removed child pointers ({RemovedRows.Count})";
        UpdatedHeader = $"Key range changes ({UpdatedRows.Count})";
    }
}
