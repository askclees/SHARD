namespace SHARD.Core.Records;

/// <summary>A decoded table row plus the forensic provenance of where it was found on disk.</summary>
public sealed class TableRow
{
    public long RowId { get; init; }
    public List<SqliteValue?> FieldValues { get; init; } = new();
    public uint PageNumber { get; init; }
    public int CellOffset { get; init; }
    public int CellLength { get; init; }
    public IReadOnlyList<OverflowFragment> OverflowFragments { get; init; } = [];
}
