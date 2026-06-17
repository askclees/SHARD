namespace SHARD.Core.Records;

public class PageFreeBlock
{
    
    public uint PageOffset { get; }
    public uint NextFreeblockPageOffset { get; }
    public uint BlockSize { get; }

    public PageFreeBlock(uint offset,uint nextOffset,uint size)
    {
        PageOffset = offset;
        NextFreeblockPageOffset = nextOffset;
        BlockSize = size;
    }
    
}