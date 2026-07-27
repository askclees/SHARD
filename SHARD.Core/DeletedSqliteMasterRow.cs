using SHARD.Core.Enums;

namespace SHARD.Core;

/// <summary>
/// A sqlite_master row recovered from a deleted, carved, or freeblock cell on page 1.
/// Wraps the parsed row data with forensic metadata about how it was found and whether
/// its root page still holds recoverable data.
/// </summary>
public sealed class DeletedSqliteMasterRow
{
    /// <summary>The parsed schema object (type, name, rootpage, sql, etc.).</summary>
    public SqliteMasterRow Row { get; }

    /// <summary>How the cell was found: "deleted-pointer", "carved", or "freeblock".</summary>
    public string RecoveryMethod { get; }

    /// <summary>Whether the root page referenced by this row is still recoverable.</summary>
    public RootPageStatus RootPageStatus { get; }

    public DeletedSqliteMasterRow(SqliteMasterRow row, string recoveryMethod, RootPageStatus rootPageStatus)
    {
        Row              = row;
        RecoveryMethod   = recoveryMethod;
        RootPageStatus   = rootPageStatus;
    }
}
