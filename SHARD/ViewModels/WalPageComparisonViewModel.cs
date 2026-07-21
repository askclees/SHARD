using SHARD.Core.Comparison;
using SHARD.Core.Records;

namespace SHARD.ViewModels;

public sealed class UpdatedRecordSectionViewModel
{
    public string Header { get; }
    public IReadOnlyList<InfoRow> Rows { get; }

    public UpdatedRecordSectionViewModel(BTreeLeafCellComparison comparison)
    {
        int n = comparison.Changes.Count;
        Header = $"Row {comparison.RecordId}  —  {n} field change{(n == 1 ? "" : "s")}";
        Rows = comparison.Changes
            .Select(c => new InfoRow($"Column {c.FieldIndex}", $"{FormatValue(c.PreviousValue)}  →  {FormatValue(c.NewValue)}"))
            .ToList();
    }

    private static string FormatValue(SqliteValue? v) => v switch
    {
        null                                      => "NULL",
        { StorageClass: SqliteStorageClass.Null } => "NULL",
        { StorageClass: SqliteStorageClass.Blob } => $"BLOB ({v.DataLength} bytes)",
        _                                         => v.Value?.ToString() ?? "NULL"
    };
}

public sealed class WalPageComparisonViewModel
{
    public string ComparedAgainst { get; }
    public bool HasAnyChanges { get; }
    public bool HasAdded { get; }
    public bool HasRemoved { get; }
    public bool HasUpdated { get; }
    public string AddedHeader { get; }
    public string RemovedHeader { get; }
    public string UpdatedHeader { get; }
    public IReadOnlyList<InfoRow> AddedRows { get; }
    public IReadOnlyList<InfoRow> RemovedRows { get; }
    public IReadOnlyList<UpdatedRecordSectionViewModel> UpdatedSections { get; }

    public WalPageComparisonViewModel(TableBTreeLeafPageComparison comparison, string comparedAgainst)
    {
        ComparedAgainst = comparedAgainst;

        AddedRows = comparison.AddedRecords
            .Select(r => new InfoRow("Row", r.RowId.Value.ToString()))
            .ToList();
        RemovedRows = comparison.RemovedRecords
            .Select(r => new InfoRow("Row", r.RowId.Value.ToString()))
            .ToList();
        UpdatedSections = comparison.UpdatedRecords
            .Select(r => new UpdatedRecordSectionViewModel(r))
            .ToList();

        HasAdded   = AddedRows.Count > 0;
        HasRemoved = RemovedRows.Count > 0;
        HasUpdated = UpdatedSections.Count > 0;
        HasAnyChanges = HasAdded || HasRemoved || HasUpdated;

        AddedHeader   = $"Added ({AddedRows.Count})";
        RemovedHeader = $"Removed ({RemovedRows.Count})";
        UpdatedHeader = $"Updated ({UpdatedSections.Count})";
    }
}
