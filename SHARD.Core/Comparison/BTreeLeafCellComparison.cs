namespace SHARD.Core.Comparison;

public class BTreeLeafCellComparison
{
    public long RecordId { get; }
    public List<FieldComparison> Changes { get; set; } = new();
    public bool HasChanges => Changes.Count > 0;

    public BTreeLeafCellComparison(long recordId)
    {
        RecordId = recordId;
    }
    
}