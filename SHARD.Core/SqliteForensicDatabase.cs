using SHARD.Core.Pages;

namespace SHARD.Core;

/// <summary>
/// Top-level entry point for forensic analysis of a SQLite database file.
/// Opens the file, reads the header, and provides access to raw pages.
/// </summary>
public sealed class SqliteForensicDatabase : IDisposable
{
    private readonly FileStream _stream;

    /// <summary>Parsed 100-byte database header.</summary>
    public DatabaseHeader Header { get; }

    /// <summary>Total number of pages derived from file size and page size.</summary>
    public uint PageCount { get; }

    private SqliteForensicDatabase(FileStream stream, DatabaseHeader header)
    {
        _stream   = stream;
        Header    = header;
        PageCount = (uint)(stream.Length / header.PageSize);
    }

    /// <summary>Open a SQLite file and parse its header.</summary>
    public static SqliteForensicDatabase Open(string filePath) =>
        throw new NotImplementedException();

    /// <summary>
    /// Read and return a single page by 1-based page number.
    /// Returns the appropriate <see cref="SqlitePage"/> subclass.
    /// </summary>
    public SqlitePage ReadPage(uint pageNumber) =>
        throw new NotImplementedException();

    /// <summary>Enumerate all pages in page-number order.</summary>
    public IEnumerable<SqlitePage> ReadAllPages() =>
        throw new NotImplementedException();

    /// <summary>Enumerate every freelist trunk and its leaf pages.</summary>
    public IEnumerable<FreelistPage> ReadFreelistChain() =>
        throw new NotImplementedException();

    public void Dispose() => _stream.Dispose();
}
