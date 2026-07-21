using SHARD.Core.Records;

namespace SHARD.Core.Comparison;

public class FieldComparison
{
    public int FieldIndex { get; }
    public SqliteValue? PreviousValue { get; }
    public SqliteValue? NewValue { get; }

    public FieldComparison(int index, SqliteValue? previous, SqliteValue? updated)
    {
        FieldIndex = index;
        PreviousValue = previous;
        NewValue = updated;
    }

}