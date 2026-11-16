using SHARD.Core.Records;

namespace SHARD.Core.Comparison;

public class TableBTreeInteriorPageComparison
{
    public List<BTreeInteriorCell> AddedRecords { get; set; } = new();
    public List<BTreeInteriorCell> RemovedRecords { get; set; } = new();
    public List<BTreeInteriorCellComparison> UpdatedRecords { get; set; } = new();
    public uint? PreviousRightPointer { get; set; }
    public uint? NewRightPointer { get; set; }
}