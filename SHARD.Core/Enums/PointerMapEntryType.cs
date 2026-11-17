namespace SHARD.Core.Enums;

/// <summary>The type byte of a pointer-map entry (SQLite's PTRMAP_* constants).</summary>
public enum PointerMapEntryType : byte
{
    /// <summary>A B-tree root page. Parent is 0.</summary>
    RootPage      = 1,
    /// <summary>A page on the freelist. Parent is 0.</summary>
    FreePage      = 2,
    /// <summary>The first page of an overflow chain. Parent is the B-tree page whose cell points to it.</summary>
    Overflow1     = 3,
    /// <summary>A later page of an overflow chain. Parent is the overflow page before it.</summary>
    Overflow2     = 4,
    /// <summary>A non-root B-tree page. Parent is its parent B-tree page.</summary>
    BTree         = 5,
}
