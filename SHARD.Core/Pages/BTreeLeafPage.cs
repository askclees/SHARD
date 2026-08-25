using SHARD.Core.Enums;

namespace SHARD.Core.Pages;

/// <summary>
/// Abstract base for leaf B-Tree pages (table and index).
/// Leaf pages hold actual cell data; they have no rightmost child pointer.
/// </summary>
public abstract class BTreeLeafPage : BTreePage
{
    protected BTreeLeafPage(uint pageNumber, int pageSize, byte[] data)
        : base(pageNumber, pageSize, data, (pageNumber == 1 ? 100 : 0) + 8) { }
}
