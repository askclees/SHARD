using System.Collections.Generic;
using System.IO;
using SHARD.Core.Enums;
using SHARD.Core.Pages;
using SHARD.Core.Records;

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
        return SqlitePage.Read(_stream, pageNumber, Header.PageSize, Header.TextEncoding, Header.ReservedBytesPerPage);
    }

    /// <summary>Enumerate all pages in page-number order.</summary>
    public IEnumerable<SqlitePage> ReadAllPages()
    {
        for (uint i = 1; i <= PageCount; i++)
            yield return ReadPage(i);
    }

    /// <summary>
    /// Reads a page known (by context) to be an overflow page. Bypasses the type-byte
    /// classifier in <see cref="ReadPage"/>, since overflow pages have no type byte.
    /// </summary>
    private OverflowPage ReadOverflowPage(uint pageNumber)
    {
        if (pageNumber < 1 || pageNumber > PageCount)
            throw new ArgumentOutOfRangeException(nameof(pageNumber));
        return SqlitePage.ReadOverflowPage(_stream, pageNumber, Header.PageSize);
    }

    /// <summary>
    /// Walks an overflow page chain starting at <paramref name="firstPage"/>, collecting up
    /// to <paramref name="totalBytesNeeded"/> bytes of payload data along with per-page
    /// fragment metadata (page number, next pointer, fragment length) for forensic display.
    /// </summary>
    private (byte[] Data, List<OverflowFragment> Fragments) ReadOverflowChain(uint firstPage, int totalBytesNeeded)
    {
        var result = new byte[totalBytesNeeded];
        var fragments = new List<OverflowFragment>();
        int written = 0;
        uint pageNum = firstPage;
        int sequence = 1;
        while (pageNum != 0 && written < totalBytesNeeded)
        {
            var page = ReadOverflowPage(pageNum);
            int toCopy = Math.Min(page.PayloadData.Length, totalBytesNeeded - written);
            page.PayloadData[..toCopy].CopyTo(result.AsMemory(written));
            fragments.Add(new OverflowFragment(sequence, pageNum, page.NextOverflowPage, toCopy));
            written += toCopy;
            pageNum = page.NextOverflowPage;
            sequence++;
        }
        return (result, fragments);
    }

    /// <summary>
    /// Read and return all rows from the sqlite_master table (page 1).
    /// Traverses interior pages if necessary.
    /// </summary>
    public IEnumerable<SqliteMasterRow> ReadSqliteMaster()
    {
        SqlitePage page = ReadPage(1);
        //check if first page is a leaf or interior page
        if (page is TableBTreeLeafPage leafPage)
        {
            return ReadSqliteMasterFromLeafPage(leafPage,1);
        }
        else if (page is TableBTreeInteriorPage interiorPage)
        {
            return ReadSqliteMasterFromInteriorPage(interiorPage);
        }

        throw new NotImplementedException();
    }

    private IEnumerable<SqliteMasterRow> ReadSqliteMasterFromLeafPage(TableBTreeLeafPage page, uint pageNum)
    {
        List<SqliteMasterRow> retVal = new();
        for (int i = 0;i<page.Cells.Count;i++)
        {
            SqliteMasterRow? newCell = CreateSqliteMasterRowFromCell(page.Cells[i], pageNum, page.CellPointers[i]);
            if (newCell != null)
            {
                retVal.Add(newCell);
            }
        }

        return retVal;
    }

    private IEnumerable<SqliteMasterRow> ReadSqliteMasterFromInteriorPage(TableBTreeInteriorPage startPage)
    {
        List<uint> leafPages = GetLeafPageNumbers(1);
        List<SqliteMasterRow> retVal = new();
        foreach (uint pageNum in leafPages)
        {
            SqlitePage page = ReadPage(pageNum);
            if (page is TableBTreeLeafPage leafPageData)
            {
                retVal.AddRange(ReadSqliteMasterFromLeafPage(leafPageData, pageNum));
            }
        }
        return retVal;
    }

    private SqliteMasterRow? CreateSqliteMasterRowFromCell(BTreeLeafCell cell, uint pageNum, int cellOffset)
    {
        if (cell.OverflowPage != 0)
        {
            var (overflowBytes, _) = ReadOverflowChain(cell.OverflowPage, cell.OverflowBytesNeeded);
            cell.ResolveOverflow(overflowBytes);
        }

        if (cell.FieldValues.Count != 5)
        {
            return null;
            
        }
        SqliteMasterObjectType? objectType = cell.FieldValues[0]?.TextValue switch
        {
            "table"   => SqliteMasterObjectType.Table,
            "index"   => SqliteMasterObjectType.Index,
            "view"    => SqliteMasterObjectType.View,
            "trigger" => SqliteMasterObjectType.Trigger,
            _         => null
        };
        if (objectType is null) return null;
        SqliteMasterRow retVal = new SqliteMasterRow()
        {
            ObjectType = objectType.Value,
            Name = cell.FieldValues[1]?.TextValue,
            TableName = cell.FieldValues[2]?.TextValue,
            RootPage = (uint?)cell.FieldValues[3]?.IntegerValue,
            Sql = cell.FieldValues[4]?.TextValue,
            PageNumber = (uint)pageNum,
            CellOffset = cellOffset,
            CellLength = cell.CellByteLengthOnPage,
        };
        
        return retVal;
    }

    private List<uint> GetLeafPageNumbers(uint pageNum)
    {
        List<uint> retVal = new List<uint>();
        SqlitePage page = ReadPage(pageNum);
        if (page is TableBTreeInteriorPage tableLeafPage)
        {
            foreach (var cell in tableLeafPage.Cells)
            {
                retVal.AddRange(GetLeafPageNumbers((cell.PageNumber)));
            }
            retVal.AddRange(GetLeafPageNumbers((tableLeafPage.RightmostPointer)));
        }

        if (page is TableBTreeLeafPage)
        {
            retVal.Add(pageNum);
        }

        return retVal;
    }
    
    /// <summary>
    /// Enumerate every page number belonging to a table's B-tree (the root page itself,
    /// plus every interior and leaf page reachable from it), so callers can attribute
    /// pages to the table they belong to.
    /// </summary>
    public IEnumerable<uint> GetTreePageNumbers(uint rootPage)
    {
        yield return rootPage;

        if (ReadPage(rootPage) is TableBTreeInteriorPage interior)
        {
            foreach (var cell in interior.Cells)
                foreach (uint pageNumber in GetTreePageNumbers(cell.PageNumber))
                    yield return pageNumber;

            foreach (uint pageNumber in GetTreePageNumbers(interior.RightmostPointer))
                yield return pageNumber;
        }
    }

    /// <summary>
    /// Read and return all rows from a table's B-tree given its root page, resolving overflow
    /// chains and decorating each row with the forensic provenance of its primary cell.
    /// </summary>
    public IEnumerable<TableRow> ReadTableRows(uint rootPage)
    {
        foreach (var (cell, pageNum, cellOffset) in ReadTableCells(rootPage))
        {
            List<OverflowFragment> fragments = [];
            if (cell.OverflowPage != 0)
            {
                var (overflowBytes, frags) = ReadOverflowChain(cell.OverflowPage, cell.OverflowBytesNeeded);
                cell.ResolveOverflow(overflowBytes);
                fragments = frags;
            }

            yield return new TableRow
            {
                RowId             = cell.RowId.Value,
                FieldValues       = cell.FieldValues,
                PageNumber        = pageNum,
                CellOffset        = cellOffset,
                CellLength        = cell.CellByteLengthOnPage,
                OverflowFragments = fragments,
            };
        }
    }

    private IEnumerable<(BTreeLeafCell Cell, uint PageNumber, int CellOffset)> ReadTableCells(uint rootPage)
    {
        SqlitePage page = ReadPage(rootPage);
        if (page is TableBTreeLeafPage leaf)
        {
            for (int i = 0; i < leaf.Cells.Count; i++)
                yield return (leaf.Cells[i], rootPage, leaf.CellPointers[i]);
            yield break;
        }

        if (page is TableBTreeInteriorPage)
        {
            foreach (uint leafPageNum in GetLeafPageNumbers(rootPage))
            {
                var leafPage = (TableBTreeLeafPage)ReadPage(leafPageNum);
                for (int i = 0; i < leafPage.Cells.Count; i++)
                    yield return (leafPage.Cells[i], leafPageNum, leafPage.CellPointers[i]);
            }
        }
    }

    /// <summary>Enumerate every freelist trunk and its leaf pages.</summary>
    public IEnumerable<FreelistPage> ReadFreelistChain() =>
        throw new NotImplementedException();

    public void Dispose() => _stream.Dispose();
}
