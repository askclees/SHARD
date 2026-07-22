using SHARD.Core.Enums;

namespace SHARD.Core.Records;

public class RecordStructure
{
    public List<SerialTypeKind> ColumnDataTypes;
    public int NumColumns => ColumnDataTypes.Count;
}