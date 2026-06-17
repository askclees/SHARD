namespace SHARD.Core.Decoding;

public static class PayloadSizeCalculator
{
    /// <summary>
    /// Computes how many bytes of a table leaf cell's payload are stored locally on the
    /// page, per the SQLite file format spec. If the result equals <paramref name="payloadSize"/>,
    /// the payload fits entirely on the page and there is no overflow. Otherwise the remaining
    /// <c>payloadSize - localSize</c> bytes live in the overflow page chain, whose first page
    /// number follows the local payload bytes in the cell.
    /// </summary>
    public static int GetLocalPayloadSize(long payloadSize, int pageSize, int reservedBytes)
    {
        int usableSize = pageSize - reservedBytes;
        int x = usableSize - 35;
        if (payloadSize <= x) return (int)payloadSize;

        int m = ((usableSize - 12) * 32 / 255) - 23;
        int k = m + (int)((payloadSize - m) % (usableSize - 4));
        return k <= x ? k : m;
    }

    /// <summary>
    /// Computes how many bytes of an index leaf or index/table interior cell's payload
    /// are stored locally. Index pages use a tighter local limit than table leaf pages:
    /// X = M = ((usableSize-12)*32/255)-23, so any payload exceeding M bytes overflows.
    /// </summary>
    public static int GetIndexLocalPayloadSize(long payloadSize, int pageSize, int reservedBytes)
    {
        int usableSize = pageSize - reservedBytes;
        int m = ((usableSize - 12) * 32 / 255) - 23;
        return payloadSize <= m ? (int)payloadSize : m;
    }
}
