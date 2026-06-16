using SHARD.Core.Enums;

namespace SHARD.Core;

/// <summary>
/// A single row from the sqlite_master table (page 1).
/// Describes one schema object in the database.
/// </summary>
public sealed class SqliteMasterRow
{
    /// <summary>The kind of schema object.</summary>
    public SqliteMasterObjectType ObjectType { get; init; }

    /// <summary>Name of the schema object.</summary>
    public string Name { get; init; } = "";

    /// <summary>
    /// For tables: same as <see cref="Name"/>.
    /// For indices, triggers, and views: the name of the associated table.
    /// </summary>
    public string TableName { get; init; } = "";

    /// <summary>
    /// Root B-tree page number for tables and indices.
    /// NULL for views and some internal objects.
    /// </summary>
    public uint? RootPage { get; init; }

    /// <summary>
    /// The original CREATE statement for this object.
    /// NULL for automatically-created objects (e.g. sqlite_sequence).
    /// </summary>
    public string? Sql { get; init; }

    // ── Forensic location ─────────────────────────────────────────────────────

    /// <summary>1-based page number this record was read from.</summary>
    public uint PageNumber { get; init; }

    /// <summary>Byte offset within the page where the cell starts.</summary>
    public int CellOffset { get; init; }

    /// <summary>Total byte length of the cell (header varints + payload).</summary>
    public int CellLength { get; init; }
}
