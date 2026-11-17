using System.Collections.Generic;
using System.IO;
using SHARD.Core.Enums;
using SHARD.Core.Journal;
using SHARD.Core.Pages;
using SHARD.Core.Records;
using SHARD.Core.Recovery;
using SHARD.Core.Schema;
using SHARD.Core.WAL;

namespace SHARD.Core;

/// <summary>
/// Top-level entry point for forensic analysis of a SQLite database file.
/// Opens the file, reads the header, and provides access to raw pages.
/// </summary>
public sealed class SqliteForensicDatabase : IDisposable
{
    private readonly FileStream _stream;

    // Page images laid over the file (null when reading the file alone): the latest committed WAL
    // image of each page, or the image a hot rollback journal would restore.
    private readonly IReadOnlyDictionary<uint, byte[]>? _overlayPages;
    // 1-based WAL frame number, or 1-based journal record number, each of those images came from.
    private readonly IReadOnlyDictionary<uint, int>? _overlaySources;
    private readonly bool _overlayIsJournal;

    /// <summary>Parsed 100-byte database header (from the WAL's latest image of page 1, when
    /// <see cref="IncludesWal"/> and the WAL holds one).</summary>
    public DatabaseHeader Header { get; }

    /// <summary>Total number of pages: from the file size, or — when <see cref="IncludesWal"/> —
    /// the database size recorded in the WAL's last commit frame, as SQLite uses.</summary>
    public uint PageCount { get; }

    /// <summary>True when this reads the database as SQLite sees it: the file with every
    /// committed WAL frame applied (see <see cref="OpenCommittedState"/>).</summary>
    public bool IncludesWal => _overlayPages is not null && !_overlayIsJournal;

    /// <summary>True when this reads the database as SQLite would after rolling back a hot journal:
    /// the file with the journal's original page images restored (see <see cref="OpenRolledBackState"/>).</summary>
    public bool IncludesJournal => _overlayPages is not null && _overlayIsJournal;

    /// <summary>Pages whose content comes from the WAL rather than the file.</summary>
    public IReadOnlyCollection<uint> WalPageNumbers => IncludesWal ? (IReadOnlyCollection<uint>)_overlayPages!.Keys : [];

    /// <summary>Pages whose content comes from the rollback journal rather than the file.</summary>
    public IReadOnlyCollection<uint> JournalPageNumbers => IncludesJournal ? (IReadOnlyCollection<uint>)_overlayPages!.Keys : [];

    /// <summary>Pages whose content comes from the WAL or the journal rather than the file.</summary>
    public IReadOnlyCollection<uint> OverlayPageNumbers => (IReadOnlyCollection<uint>?)_overlayPages?.Keys ?? [];

    /// <summary>The 1-based WAL frame page <paramref name="pageNumber"/> is read from, or null when
    /// it's read from the file.</summary>
    public int? WalFrameNumberFor(uint pageNumber) =>
        IncludesWal && _overlaySources!.TryGetValue(pageNumber, out int frame) ? frame : null;

    /// <summary>The 1-based journal record page <paramref name="pageNumber"/> is read from, or null
    /// when it's read from the file.</summary>
    public int? JournalRecordNumberFor(uint pageNumber) =>
        IncludesJournal && _overlaySources!.TryGetValue(pageNumber, out int record) ? record : null;

    private SqliteForensicDatabase(FileStream stream, DatabaseHeader header,
        IReadOnlyDictionary<uint, byte[]>? overlayPages = null, uint? pageCount = null,
        IReadOnlyDictionary<uint, int>? overlaySources = null, bool overlayIsJournal = false)
    {
        _stream   = stream;
        Header    = header;
        _overlayPages = overlayPages;
        _overlaySources = overlaySources;
        _overlayIsJournal = overlayIsJournal;
        PageCount = pageCount ?? (uint)(stream.Length / header.PageSize);
    }

    /// <summary>
    /// Opens the database as SQLite would see it with <paramref name="wal"/> applied: each page is
    /// read from its latest <see cref="WalFrameStatus.Committed"/> frame if it has one, otherwise
    /// from the file, and the page count is the database size in the last commit frame (a commit
    /// can grow or shrink the database). Frames SQLite ignores — uncommitted, invalid or
    /// old-generation — play no part. With no committed frames this is the same as <see cref="Open"/>.
    /// </summary>
    public static SqliteForensicDatabase OpenCommittedState(string filePath, WalFile wal)
    {
        if (wal.CommittedFrameCount == 0) return Open(filePath);

        var walPages = new Dictionary<uint, byte[]>();
        var walFrameNumbers = new Dictionary<uint, int>();
        for (int i = 0; i < wal.CommittedFrameCount; i++)
        {
            var frame = wal.Frames[i];
            walPages[frame.Header.PageNumber] = frame.PageData;
            walFrameNumbers[frame.Header.PageNumber] = i + 1;
        }
        uint pageCount = wal.Frames[wal.CommittedFrameCount - 1].Header.SizeOfDatabaseInPages;

        var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            byte[] headerBytes;
            if (walPages.TryGetValue(1, out var page1))
            {
                headerBytes = page1[..100];
            }
            else
            {
                headerBytes = new byte[100];
                stream.ReadExactly(headerBytes);
            }
            var header = new DatabaseHeader(headerBytes);
            if (header.PageSize != wal.Header.DatabasePageSize)
                throw new InvalidDataException(
                    $"The WAL's page size ({wal.Header.DatabasePageSize:N0}) doesn't match the database's ({header.PageSize:N0}).");

            return new SqliteForensicDatabase(stream, header, walPages, pageCount, walFrameNumbers);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The records of <paramref name="journal"/> SQLite would play back when it next opens the
    /// database (pager_playback in pager.c): <see cref="JournalRecordStatus.Valid"/> records of
    /// segments whose header gives a record count. A hot journal's header count is 0 only when
    /// the journal was never synced, and SQLite then restores nothing from that segment. A journal
    /// with an inferred header (zeroed: its transaction committed) is never played back. Each
    /// record is paired with its 1-based number in the file.
    /// </summary>
    public static IReadOnlyList<(RollbackJournalPageRecord Record, int Number, RollbackJournalSegment Segment)>
        RecordsPlayedBack(RollbackJournalFile journal)
    {
        var played = new List<(RollbackJournalPageRecord, int, RollbackJournalSegment)>();
        if (journal.HeaderInferred) return played;
        int number = 0;
        foreach (var segment in journal.Segments)
        {
            foreach (var record in segment.PageRecords)
            {
                number++;
                if (segment.Header.PageCount != 0 && record.Status == JournalRecordStatus.Valid)
                    played.Add((record, number, segment));
            }
        }
        return played;
    }

    /// <summary>
    /// Opens the database as SQLite would see it after rolling back <paramref name="journal"/>, as
    /// it does on opening a database with a hot journal (one left by a transaction that never
    /// committed): each page a played-back record holds (see <see cref="RecordsPlayedBack"/>) is
    /// read from the journal, in file order so a later record wins as it does in SQLite, and the
    /// page count is the database size in the last played segment's header (SQLite truncates the
    /// file to it). With nothing to play back this is the same as <see cref="Open"/>.
    /// </summary>
    public static SqliteForensicDatabase OpenRolledBackState(string filePath, RollbackJournalFile journal)
    {
        var played = RecordsPlayedBack(journal);
        if (played.Count == 0) return Open(filePath);

        var pages = new Dictionary<uint, byte[]>();
        var numbers = new Dictionary<uint, int>();
        foreach (var (record, number, _) in played)
        {
            pages[record.PageNumber] = record.Page.Data;
            numbers[record.PageNumber] = number;
        }
        int originalSize = played[^1].Segment.Header.DatabaseSize;

        var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            byte[] headerBytes;
            if (pages.TryGetValue(1, out var page1))
            {
                headerBytes = page1[..100];
            }
            else
            {
                headerBytes = new byte[100];
                stream.ReadExactly(headerBytes);
            }
            var header = new DatabaseHeader(headerBytes);
            int journalPageSize = journal.Segments[0].Header.SizeOfJournalPages;
            if (header.PageSize != journalPageSize)
                throw new InvalidDataException(
                    $"The journal's page size ({journalPageSize:N0}) doesn't match the database's ({header.PageSize:N0}).");

            // 0 would mean the transaction created the database; keep the file's size rather than
            // describing an empty database.
            uint? pageCount = originalSize > 0 ? (uint)originalSize : null;
            return new SqliteForensicDatabase(stream, header, pages, pageCount, numbers, overlayIsJournal: true);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>A copy of page <paramref name="pageNumber"/>'s bytes, from the WAL or journal if it
    /// holds the page's image, otherwise from the file.</summary>
    private byte[] ReadPageBytes(uint pageNumber)
    {
        if (_overlayPages is not null && _overlayPages.TryGetValue(pageNumber, out var overlayData))
            return (byte[])overlayData.Clone();

        int pageSize = Header.PageSize;
        long offset = (long)(pageNumber - 1) * pageSize;
        if (offset + pageSize > _stream.Length)
            throw new InvalidDataException($"Page {pageNumber} is beyond the end of the file and not in the WAL or journal.");

        var data = new byte[pageSize];
        _stream.Position = offset;
        _stream.ReadExactly(data);
        return data;
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
        return SqlitePage.FromBytes(pageNumber, Header.PageSize, ReadPageBytes(pageNumber), Header.TextEncoding, Header.ReservedBytesPerPage);
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
        return new OverflowPage(pageNumber, Header.PageSize, ReadPageBytes(pageNumber));
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

    /// <summary>
    /// Carves deleted records from every leaf page of the named table's B-tree by
    /// scanning unallocated regions with schema-driven validation. Results are stored
    /// in <see cref="TableBTreeLeafPage.CarvedCells"/> on each page and also yielded
    /// here for convenience. Returns an empty sequence if the schema cannot be resolved
    /// or the table is not found.
    /// </summary>
    public IEnumerable<BTreeLeafCell> CarveTableDeletedCells(string tableName)
    {
        var schema = GetTableSchema(tableName);
        if (schema is null) yield break;

        var recordStructure = RecordStructure.FromSchema(schema);

        uint? rootPage = string.Equals(tableName, "sqlite_master", StringComparison.OrdinalIgnoreCase)
            ? 1u
            : ReadSqliteMaster()
                .FirstOrDefault(r => r.ObjectType == SqliteMasterObjectType.Table
                                  && string.Equals(r.Name, tableName, StringComparison.OrdinalIgnoreCase))
                ?.RootPage;

        if (rootPage is null) yield break;

        foreach (uint pageNum in GetTreePageNumbers(rootPage.Value))
        {
            if (ReadPage(pageNum) is not TableBTreeLeafPage leaf) continue;
            leaf.CarveDeletedCells(recordStructure);
            foreach (var cell in leaf.CarvedCells)
                yield return cell;
        }
    }

    /// <summary>
    /// Carves deleted records from every sqlite_master leaf page and returns those that
    /// parse as valid schema rows. Each result includes how the cell was recovered and
    /// whether the table's root page still holds readable B-tree data.
    /// </summary>
    public IEnumerable<DeletedSqliteMasterRow> ReadDeletedSqliteMaster()
    {
        // Build the set of root pages currently claimed by live objects so we can
        // distinguish "still valid" from "reused by another table".
        var liveRootPages = new HashSet<uint>();
        liveRootPages.Add(1); // sqlite_master itself
        foreach (var row in ReadSqliteMaster())
            if (row.RootPage.HasValue)
                liveRootPages.Add(row.RootPage.Value);

        var recordStructure = RecordStructure.FromSchema(SqliteMasterSchema);

        foreach (uint pageNum in GetLeafPageNumbers(1))
        {
            if (ReadPage(pageNum) is not TableBTreeLeafPage leaf) continue;

            leaf.CarveDeletedCells(recordStructure);
            leaf.CarveFreeblockCells(recordStructure);

            var candidates =
                leaf.DeletedCells  .Select(c => (Cell: c, Method: "deleted-pointer"))
                .Concat(leaf.CarvedCells   .Select(c => (Cell: c, Method: "carved")))
                .Concat(leaf.FreeblockCells.Select(c => (Cell: c, Method: "freeblock")));

            foreach (var (cell, method) in candidates)
            {
                var masterRow = CreateSqliteMasterRowFromCell(cell, pageNum, cell.PageOffset);
                if (masterRow is null) continue;

                var status = masterRow.RootPage.HasValue
                    ? CheckRootPageValidity(masterRow.RootPage.Value, liveRootPages)
                    : RootPageStatus.Invalid;

                yield return new DeletedSqliteMasterRow(masterRow, method, status);
            }
        }
    }

    private RootPageStatus CheckRootPageValidity(uint rootPage, HashSet<uint> liveRootPages)
    {
        if (rootPage < 1 || rootPage > PageCount) return RootPageStatus.Invalid;
        if (liveRootPages.Contains(rootPage))      return RootPageStatus.Reused;
        try
        {
            var page = ReadPage(rootPage);
            return page is TableBTreeLeafPage or TableBTreeInteriorPage
                ? RootPageStatus.Valid
                : RootPageStatus.Freed;
        }
        catch
        {
            return RootPageStatus.Invalid;
        }
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

        string detail = page is UnknownPage unknown
            ? unknown.ParseError is not null
                ? $"declared type byte {(int)unknown.DeclaredTypeByte} ({unknown.ParseError.Message})"
                : $"declared type byte {(int)unknown.DeclaredTypeByte}"
            : $"page type {page.PageType}";
        throw new InvalidDataException(
            $"Page 1 is not a table B-tree page ({detail}) — the database header may be intact but the schema page itself appears corrupted, or this is not a valid SQLite database.");
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
        if (rootPage < 1 || rootPage > PageCount) yield break;

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
        var data = ReadPageBytes(pageNum);
        FreelistTrunkPage trunk = new FreelistTrunkPage(pageNum, pageSize, data);
        retVal.Add(trunk);
        if (trunk.NextTrunkPageNumber != 0)
        {
            retVal.AddRange(GetFreelistPageNumbersFromTrunkPage(trunk.NextTrunkPageNumber, pageSize));
        }
        return retVal;
    }

    /// <summary>
    /// Returns every page number that isn't explained by anything this database knows about:
    /// not part of the sqlite_master tree, not part of any live table/index's tree, and not an
    /// overflow page for a live row. This includes pages whose type byte is unrecognizable, freelist
    /// leaf pages, and freelist <em>trunk</em> pages — SQLite doesn't zero freed pages, and a trunk
    /// page only has its first ~8-12 bytes overwritten with the trunk header/leaf-pointer array;
    /// the rest of the page (especially common when a whole small table's only page becomes the
    /// sole freelist entry after a DROP TABLE) can still be exactly what it was before, so trunk
    /// pages are scanned too rather than skipped wholesale — the byte-level scanner already discards
    /// the handful of header bytes as non-record-shaped on its own.
    /// Connection-free: works directly off the evidence file, no shadow database required.
    /// </summary>
    /// <param name="excludeDroppedTablePages">
    /// When true, also subtracts pages already explained by a recovered deleted sqlite_master
    /// entry (<see cref="ReadDeletedSqliteMaster"/>) — a still-valid dropped table's whole tree, or
    /// a freed dropped table's root page. Defaults to false: <see cref="OrphanPageCarver"/> now
    /// includes dropped-table schemas as carving candidates too (writing to a different shadow
    /// table, <c>_shard_recovered_*</c>, than the structural dropped-table flow's
    /// <c>_shard_deleted_*</c>), so there's no collision to avoid — excluding these pages by default
    /// would make orphan carving blind to the common "just dropped a table" case entirely,
    /// including small single-page tables where the whole thing is a single "unclaimed" page.
    /// </param>
    public IReadOnlyList<uint> GetUnclaimedPageNumbers(bool excludeDroppedTablePages = false)
    {
        var claimed = new HashSet<uint>();

        foreach (uint p in GetTreePageNumbers(1)) claimed.Add(p); // sqlite_master

        foreach (var row in ReadSqliteMaster())
        {
            if (!row.RootPage.HasValue) continue;
            foreach (uint p in GetTreePageNumbers(row.RootPage.Value)) claimed.Add(p);

            if (row.ObjectType == SqliteMasterObjectType.Table)
            {
                foreach (var tableRow in ReadTableRows(row.RootPage.Value))
                    foreach (var fragment in tableRow.OverflowFragments)
                        claimed.Add(fragment.PageNumber);
            }
        }

        if (excludeDroppedTablePages)
        {
            try
            {
                foreach (var deleted in ReadDeletedSqliteMaster())
                {
                    if (!deleted.Row.RootPage.HasValue) continue;
                    switch (deleted.RootPageStatus)
                    {
                        case RootPageStatus.Valid:
                            foreach (uint p in GetTreePageNumbers(deleted.Row.RootPage.Value)) claimed.Add(p);
                            break;
                        case RootPageStatus.Freed:
                            claimed.Add(deleted.Row.RootPage.Value);
                            break;
                    }
                }
            }
            catch
            {
                // Best-effort: dropped-table detection is a forensic aid, not required for correctness.
            }
        }

        var unclaimed = new List<uint>();
        for (uint p = 1; p <= PageCount; p++)
            if (!claimed.Contains(p)) unclaimed.Add(p);
        return unclaimed;
    }

    public void Dispose() => _stream.Dispose();
}
