namespace SHARD.Core.Schema;

/// <summary>The column structure of a table, extracted from its CREATE TABLE statement.</summary>
public sealed class TableSchema
{
    public string TableName { get; set; } = "";
    public List<ColumnDefinition> Columns { get; } = new();
}
