namespace SHARD.Core.Pages;

/// <summary>
/// An overflow page, used when a cell's payload exceeds the inline limit.
///
/// Layout (offsets from start of page):
///   +0 : 4 bytes — next overflow page number (0 = last in chain)
///   +4 : (PageSize - 4) bytes — overflow payload data
/// </summary>
public sealed class OverflowPage : SqlitePage
{
    public override PageType PageType => PageType.Overflow;

    /// <summary>Page number of the next overflow page in the chain (0 = end).</summary>
    public uint NextOverflowPage { get; }

    /// <summary>The portion of payload data stored on this page.</summary>
    public ReadOnlyMemory<byte> PayloadData { get; }

    public OverflowPage(uint pageNumber, int pageSize, byte[] data)
        : base(pageNumber, pageSize, data)
    {
        NextOverflowPage = default;
        PayloadData      = ReadOnlyMemory<byte>.Empty;
        throw new NotImplementedException();
    }
}
