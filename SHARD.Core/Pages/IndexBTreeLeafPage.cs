using SHARD.Core.Enums;

namespace SHARD.Core.Pages;

public sealed class IndexBTreeLeafPage : BTreeLeafPage
{
    public override PageType PageType => PageType.BTreeLeafIndex;

    public IndexBTreeLeafPage(uint pageNumber, int pageSize, byte[] data)
        : base(pageNumber, pageSize, data) { }
}
