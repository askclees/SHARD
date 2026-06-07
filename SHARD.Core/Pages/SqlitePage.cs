using System.IO;
using SHARD.Core.Enums;

namespace SHARD.Core.Pages;

/// <summary>
/// Abstract base for all SQLite pages. Pages are 1-indexed.
/// Page 1 is special: its first 100 bytes are the database header,
/// so the page header / cell content starts at offset 100.
/// </summary>
public abstract class SqlitePage
{
    /// <summary>1-based page number.</summary>
    public uint PageNumber { get; }

    /// <summary>Page size in bytes (from the database header).</summary>
    public int PageSize { get; }

    /// <summary>Raw page bytes.</summary>
    public byte[] Data { get; }

    /// <summary>Classified type of this page.</summary>
    public abstract PageType PageType { get; }

    /// <summary>
    /// Byte offset within <see cref="Data"/> where the page header begins.
    /// 100 for page 1, 0 for all other pages.
    /// </summary>
    public int HeaderOffset => PageNumber == 1 ? 100 : 0;

    protected SqlitePage(uint pageNumber, int pageSize, byte[] data)
    {
        PageNumber = pageNumber;
        PageSize   = pageSize;
        Data       = data;
    }

    /// <summary>
    /// Read and classify a page from the stream.
    /// Returns the appropriate subclass based on the type byte.
    /// </summary>
    public static SqlitePage Read(Stream stream, uint pageNumber, int pageSize)
    {
        var data = new byte[pageSize];
        stream.Position = (long)(pageNumber - 1) * pageSize;
        stream.ReadExactly(data);

        int headerOffset = pageNumber == 1 ? 100 : 0;
        var typeByte = (PageType)data[headerOffset];

        try
        {
            return typeByte switch
            {
                PageType.BTreeInteriorTable => new TableBTreeInteriorPage(pageNumber, pageSize, data),
                PageType.BTreeInteriorIndex => new IndexBTreeInteriorPage(pageNumber, pageSize, data),
                PageType.BTreeLeafTable     => new TableBTreeLeafPage(pageNumber, pageSize, data),
                PageType.BTreeLeafIndex     => new IndexBTreeLeafPage(pageNumber, pageSize, data),
                _                           => new UnknownPage(pageNumber, pageSize, data),
            };
        }
        catch
        {
            return new UnknownPage(pageNumber, pageSize, data);
        }
    }
}

/// <summary>Catch-all for pages whose type byte is unrecognised.</summary>
public sealed class UnknownPage(uint pageNumber, int pageSize, byte[] data)
    : SqlitePage(pageNumber, pageSize, data)
{
    public override PageType PageType => PageType.Unknown;
}
