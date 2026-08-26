namespace SHARD.Core.Comparison;

public class BTreeInteriorCellComparison
{
    public uint PageNumber { get; set; }
    public long PreviousRecordId { get; set;}
    public long NewRecordId { get; set;}
}