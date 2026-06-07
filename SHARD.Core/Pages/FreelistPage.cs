using SHARD.Core.Enums;

namespace SHARD.Core.Pages;

/// <summary>
/// A freelist trunk page. Trunk pages chain together and each holds
/// a list of freelist leaf page numbers.
///
/// Layout (offsets from start of page):
///   +0 : 4 bytes — next trunk page number (0 = end of chain)
///   +4 : 4 bytes — number of leaf entries on this trunk
///   +8 : 4 bytes each — leaf page numbers (up to (PageSize - 8) / 4 entries)
/// </summary>
public sealed class FreelistPage : SqlitePage
{
    public override PageType PageType => PageType.FreelistTrunk;

    /// <summary>Page number of the next freelist trunk (0 = this is the last).</summary>
    public uint NextTrunkPageNumber { get; }

    /// <summary>Number of leaf page entries on this trunk.</summary>
    public uint LeafCount { get; }

    /// <summary>Page numbers of freelist leaf pages recorded on this trunk.</summary>
    public uint[] LeafPageNumbers { get; }

    public FreelistPage(uint pageNumber, int pageSize, byte[] data)
        : base(pageNumber, pageSize, data)
    {
        NextTrunkPageNumber = default;
        LeafCount           = default;
        LeafPageNumbers     = [];
        throw new NotImplementedException();
    }
}
