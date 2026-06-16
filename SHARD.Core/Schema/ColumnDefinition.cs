using SHARD.Core.Enums;

namespace SHARD.Core.Schema;

/// <summary>One column extracted from a CREATE TABLE statement.</summary>
public sealed class ColumnDefinition
{
    public string Name { get; set; } = "";
    public string? DeclaredType { get; set; }
    public TypeAffinity Affinity { get; set; }
    public bool IsPrimaryKey { get; set; }
    public bool IsNotNull { get; set; }
    public bool IsUnique { get; set; }

    /// <summary>True for a single-column "INTEGER PRIMARY KEY" — an alias for rowid.</summary>
    public bool IsRowIdAlias { get; set; }
}
