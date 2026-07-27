using System.Collections.Generic;
using System.IO;
using SHARD.Core.Enums;
using SHARD.Core.Pages;
using SHARD.Core.Records;
using SHARD.Core.Schema;

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
    /// Follows the overflow chain for a table leaf cell and calls <see cref="BTreeLeafCell.ResolveOverflow"/>
    /// so that fields that spilled past the local payload boundary are fully decoded.
    /// </summary>
    public void ResolveOverflow(BTreeLeafCell cell)
    {
        if (cell.OverflowPage == 0) return;
        var (bytes, _) = ReadOverflowChain(cell.OverflowPage, cell.OverflowBytesNeeded);
        cell.ResolveOverflow(bytes);
    }

    /// <summary>
    /// Follows the overflow chain for an index leaf cell and calls <see cref="IndexBTreeLeafCell.ResolveOverflow"/>
    /// so that fields that spilled past the local payload boundary are fully decoded.
    /// </summary>
    public void ResolveOverflow(IndexBTreeLeafCell cell)
    {
        if (cell.OverflowPage == 0) return;
        var (bytes, _) = ReadOverflowChain(cell.OverflowPage, cell.OverflowBytesNeeded);
        cell.ResolveOverflow(bytes);
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
    /// Builds a map from every B-tree page number to the name of the sqlite_master object
    /// (table or index) that owns it. Page 1 (sqlite_master itself) is mapped to
    /// "sqlite_master". Pages not reachable from any known root are absent from the map.
    /// </summary>
    public Dictionary<uint, string> BuildPageTableMap()
    {
        var map = new Dictionary<uint, string>();
        foreach (uint p in GetTreePageNumbers(1))
            map[p] = "sqlite_master";

        foreach (var row in ReadSqliteMaster())
        {
            if (row.RootPage is null || row.Name is null) continue;
            string label = row.Name;
            foreach (uint p in GetTreePageNumbers(row.RootPage.Value))
                map.TryAdd(p, label);
        }
        return map;
    }

    // sqlite_master always has this fixed schema — it is not in itself, so we hardcode it.
    public static readonly TableSchema SqliteMasterSchema =
        CreateTableParser.ExtractTableSchema(
            "CREATE TABLE sqlite_master (type TEXT, name TEXT, tbl_name TEXT, rootpage INTEGER, sql TEXT)")!;

    /// <summary>
    /// Returns the parsed <see cref="TableSchema"/> for the named evidence table,
    /// or null if the table is not found or its SQL cannot be parsed.
    /// </summary>
    public TableSchema? GetTableSchema(string tableName)
    {
        if (string.Equals(tableName, "sqlite_master", StringComparison.OrdinalIgnoreCase))
            return SqliteMasterSchema;

        foreach (var row in ReadSqliteMaster())
        {
            if (row.ObjectType != SqliteMasterObjectType.Table) continue;
            if (!string.Equals(row.Name, tableName, StringComparison.OrdinalIgnoreCase)) continue;
            if (row.Sql is null) return null;
            return CreateTableParser.ExtractTableSchema(row.Sql);
        }
        return null;
    }

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
    /// Enumerate every page number belonging to a B-tree (the root page itself,
    /// plus every interior and leaf page reachable from it). Works for both table
    /// and index B-trees — interior cells always lead with a 4-byte left-child pointer
    /// regardless of B-tree type.
    /// </summary>
    public IEnumerable<uint> GetTreePageNumbers(uint rootPage)
    {
        yield return rootPage;

        if (ReadPage(rootPage) is BTreeInteriorPage interior)
        {
            // All interior-page cells (table or index) start with a 4-byte big-endian
            // left-child page pointer, so we can read child pointers without knowing
            // the full cell format.
            foreach (ushort cellOffset in interior.CellPointers)
            {
                uint childPage = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(
                    interior.Data.AsSpan(cellOffset, 4));
                foreach (uint pageNumber in GetTreePageNumbers(childPage))
                    yield return pageNumber;
            }

            foreach (uint pageNumber in GetTreePageNumbers(interior.RightmostPointer))
                yield return pageNumber;
        }
    }

    /// <summary>
    /// Read and return all rows from a table's B-tree given its root page, resolving overflow
    /// chains and decorating each row with the forensic provenance of its primary cell.
    /// Handles both ordinary (rowid) tables and WITHOUT ROWID tables, which SQLite stores as
    /// index B-trees (leaf page type 0x0A) rather than table B-trees (0x0D).
    /// </summary>
    public IEnumerable<TableRow> ReadTableRows(uint rootPage)
    {
        // WITHOUT ROWID tables are stored as index B-trees
        if (ReadPage(rootPage) is IndexBTreeLeafPage or IndexBTreeInteriorPage)
        {
            foreach (var row in ReadWithoutRowidTableRows(rootPage))
                yield return row;
            yield break;
        }

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

    private IEnumerable<TableRow> ReadWithoutRowidTableRows(uint rootPage)
    {
        foreach (uint pageNum in GetTreePageNumbers(rootPage))
        {
            if (ReadPage(pageNum) is not IndexBTreeLeafPage leaf) continue;
            for (int i = 0; i < leaf.Cells.Count; i++)
            {
                var cell = leaf.Cells[i];
                List<OverflowFragment> fragments = [];
                if (cell.OverflowPage != 0)
                {
                    var (overflowBytes, frags) = ReadOverflowChain(cell.OverflowPage, cell.OverflowBytesNeeded);
                    cell.ResolveOverflow(overflowBytes);
                    fragments = frags;
                }
                yield return new TableRow
                {
                    RowId             = 0, // WITHOUT ROWID tables have no rowid
                    FieldValues       = cell.FieldValues,
                    PageNumber        = pageNum,
                    CellOffset        = leaf.CellPointers[i],
                    CellLength        = cell.CellByteLengthOnPage,
                    OverflowFragments = fragments,
                };
            }
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
    public IEnumerable<FreelistTrunkPage> ReadFreelistChain()
    {
        List<FreelistTrunkPage> retVal = new();
        //Check we have freelist trunk pages
        if (Header.FirstFreelistTrunkPage != 0 && Header.TotalFreelistPages != 0)
        {
            retVal.AddRange(GetFreelistPageNumbersFromTrunkPage(Header.FirstFreelistTrunkPage, Header.PageSize));
        }
        return retVal;
    }

    private IEnumerable<FreelistTrunkPage> GetFreelistPageNumbersFromTrunkPage(uint pageNum, int pageSize)
    {
        List<FreelistTrunkPage> retVal = new();
        var data = new byte[pageSize];
        _stream.Position = (long)(pageNum - 1) * pageSize;
        _stream.ReadExactly(data);
        FreelistTrunkPage trunk = new FreelistTrunkPage(pageNum, pageSize, data);
        retVal.Add(trunk);
        if (trunk.NextTrunkPageNumber != 0)
        {
            retVal.AddRange(GetFreelistPageNumbersFromTrunkPage(trunk.NextTrunkPageNumber, pageSize));
        }
        return retVal;
    }
        

    public void Dispose() => _stream.Dispose();
}
