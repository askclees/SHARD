using SHARD.Core.Records;

namespace SHARD.Core.Recovery;

public class DeletedBTreeLeafCellResult
{
    public bool IsValid { get; }
    public BTreeLeafCell? Cell { get; }
    public IReadOnlyList<string> ValidationErrors { get; }

    public DeletedBTreeLeafCellResult(BTreeLeafCell cell)
    {
        IsValid = true;
        Cell = cell;
        ValidationErrors = new List<string>();
    }

    public DeletedBTreeLeafCellResult(List<string> errors)
    {
        IsValid = false;
        ValidationErrors = errors;
    }

}