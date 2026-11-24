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

    /// <summary>
    /// Raw SQL text of this column's DEFAULT expression (e.g. "0", "'x'", "CURRENT_TIMESTAMP"),
    /// or null if the column has none. Used to fill in a value for rows recorded before a later
    /// <c>ALTER TABLE ... ADD COLUMN ... NOT NULL DEFAULT ...</c> migration, whose physical
    /// records have fewer serial-type header entries than the current schema has columns.
    /// </summary>
    public string? DefaultValueSql { get; set; }

    /// <summary>True for a single-column "INTEGER PRIMARY KEY" — an alias for rowid.</summary>
    public bool IsRowIdAlias { get; set; }
}
