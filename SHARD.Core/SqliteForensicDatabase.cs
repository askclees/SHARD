using System.Collections.Generic;
using System.IO;
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
    public static SqliteForensicDatabase Open(string filePath)
    {
        var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            var headerBytes = new byte[100];
            stream.ReadExactly(headerBytes);
            var header = new DatabaseHeader(headerBytes);
            return new SqliteForensicDatabase(stream, header);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Read and return a single page by 1-based page number.
    /// Returns the appropriate <see cref="SqlitePage"/> subclass.
    /// </summary>
    public SqlitePage ReadPage(uint pageNumber)
    {
        if (pageNumber < 1 || pageNumber > PageCount)
            throw new ArgumentOutOfRangeException(nameof(pageNumber));
        return SqlitePage.Read(_stream, pageNumber, Header.PageSize);
    }

    /// <summary>Enumerate all pages in page-number order.</summary>
    public IEnumerable<SqlitePage> ReadAllPages()
    {
        for (uint i = 1; i <= PageCount; i++)
            yield return ReadPage(i);
    }

    /// <summary>Enumerate every freelist trunk and its leaf pages.</summary>
    public IEnumerable<FreelistPage> ReadFreelistChain() =>
        throw new NotImplementedException();

    public void Dispose() => _stream.Dispose();
}
