using SHARD.Core.Records;

namespace SHARD.Core.Comparison;

public class TableBTreeLeafPageComparison
{
    public List<BTreeLeafCell> AddedRecords { get; set; } = new();
    public List<BTreeLeafCell> RemovedRecords { get; set; } = new();
    
}