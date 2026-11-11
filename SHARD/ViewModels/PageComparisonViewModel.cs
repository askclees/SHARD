using SHARD.Core.Comparison;
using SHARD.Core.Records;

namespace SHARD.ViewModels;

/// <summary>One changed field: its column and the value before and after, each shortened for display.</summary>
public sealed class FieldChangeViewModel
{
    private const int MaxDisplayLength = 80;

    public string Column     { get; }
    public string Before     { get; }
    public string After      { get; }
    // untruncated values, for the tooltip
    public string BeforeFull { get; }
    public string AfterFull  { get; }

    public FieldChangeViewModel(FieldComparison change, IReadOnlyList<string>? columnNames)
    {
        Column = columnNames is not null && change.FieldIndex < columnNames.Count
            ? columnNames[change.FieldIndex]
            : $"Column {change.FieldIndex}";
        BeforeFull = FormatValue(change.PreviousValue);
        AfterFull  = FormatValue(change.NewValue);
        Before     = Shorten(BeforeFull);
        After      = Shorten(AfterFull);
    }

    private static string Shorten(string value) =>
        value.Length <= MaxDisplayLength ? value : $"{value[..MaxDisplayLength]}… ({value.Length:N0} chars)";

    private static string FormatValue(SqliteValue? v) => v switch
    {
        null                                      => "NULL",
        { StorageClass: SqliteStorageClass.Null } => "NULL",
        { StorageClass: SqliteStorageClass.Blob } => $"BLOB ({v.DataLength} bytes)",
        _                                         => v.Value?.ToString() ?? "NULL"
    };
}

public sealed class UpdatedRecordSectionViewModel
{
    public string Header { get; }
    public IReadOnlyList<FieldChangeViewModel> Changes { get; }

    public UpdatedRecordSectionViewModel(BTreeLeafCellComparison comparison, IReadOnlyList<string>? columnNames)
    {
        int n = comparison.Changes.Count;
        Header = $"Row {comparison.RecordId}  —  {n} field change{(n == 1 ? "" : "s")}";
        Changes = comparison.Changes
            .Select(c => new FieldChangeViewModel(c, columnNames))
            .ToList();
    }
}

public sealed class PageComparisonViewModel
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

    /// <param name="columnNames">
    /// The table's column names in record order, so changes show "note" rather than "Column 2".
    /// Null when the page's table or schema is unknown.
    /// </param>
    public PageComparisonViewModel(TableBTreeLeafPageComparison comparison, string comparedAgainst,
        IReadOnlyList<string>? columnNames = null)
    {
        ComparedAgainst = comparedAgainst;

        AddedRows = comparison.AddedRecords
            .Select(r => new InfoRow("Row", r.RowId.Value.ToString()))
            .ToList();
        RemovedRows = comparison.RemovedRecords
            .Select(r => new InfoRow("Row", r.RowId.Value.ToString()))
            .ToList();
        UpdatedSections = comparison.UpdatedRecords
            .Select(r => new UpdatedRecordSectionViewModel(r, columnNames))
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
