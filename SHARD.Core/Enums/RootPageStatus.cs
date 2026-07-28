namespace SHARD.Core.Enums;

public enum RootPageStatus
{
    /// <summary>Page is a valid B-tree table page not claimed by any live object — data may be recoverable.</summary>
    Valid,

    /// <summary>Page is currently owned by a different live table or index — the deleted table's data was overwritten.</summary>
    Reused,

    /// <summary>Page exists but is a freelist, overflow, or non-table page — the data is gone.</summary>
    Freed,

    /// <summary>Page number is out of range, or the page cannot be read.</summary>
    Invalid,
}
