using System.Buffers.Binary;
using SHARD.Core.Decoding;
using SHARD.Core.Enums;
using SHARD.Core.Records;

namespace SHARD.Core.Pages;

public sealed class IndexBTreeInteriorPage : BTreeInteriorPage
{
    public override PageType PageType => PageType.BTreeInteriorIndex;

    public IndexBTreeInteriorPage(uint pageNumber, int pageSize, byte[] data)
        : base(pageNumber, pageSize, data)
    {

        
    }
}
