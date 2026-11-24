namespace SHARD.Core.Schema;

/// <summary>The column structure of a table, extracted from its CREATE TABLE statement.</summary>
public sealed class TableSchema
{
    public string TableName { get; set; } = "";
    public List<ColumnDefinition> Columns { get; } = new();

    /// <summary>The raw CREATE TABLE statement this schema was parsed from, if known. Lets a
    /// schema be fully reconstructed later (column order, declared types, rowid-alias detection)
    /// via <see cref="CreateTableParser.ExtractTableSchema"/> without needing the original
    /// database open — e.g. a carving profile exported while a database was open, later applied
    /// somewhere that database is no longer available.</summary>
    public string? Sql { get; set; }
}
