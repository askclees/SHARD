namespace SHARD.Core.Analysis;

public class PageUnallocatedRegion
{
    public int Offset { get; }
    public int Size { get;}
    public int NonZeroBytes { get; }
    public bool ContainsNonZeroBytes => NonZeroBytes > 0;
    public byte[] AreaData { get; } 

    public PageUnallocatedRegion(int offset, int size, int nonZeroBytes, ReadOnlySpan<byte> blockData)
    {
        Offset = offset;
        Size = size;
        NonZeroBytes = nonZeroBytes;
        AreaData = blockData.ToArray();
    }
    
}