using System.Buffers.Binary;
using System.Text;
using Microsoft.Data.Sqlite;
using SHARD.Core.Analysis;
using SHARD.Core.Enums;
using SHARD.Core.Pages;
using SHARD.Core.Records;
using SHARD.Core.Recovery;
using SHARD.Core.Schema;
using SHARD.Core.Shadow;

namespace SHARD.Core.Tests;

/// <summary>
/// Counting the non-zero bytes recovery leaves unexplained (issues #24 and #28). The page
/// fixtures are built by hand so every expected count is exact.
/// </summary>
public class ResidualBytesTests : IDisposable
{
    private const int PageSize = 512;
    private static readonly TableSchema Schema =
        CreateTableParser.ExtractTableSchema("CREATE TABLE t (id INTEGER PRIMARY KEY, v TEXT)")!;
    private static readonly RecordStructure Structure = RecordStructure.FromSchema(Schema);

    private readonly string _dir;

    public ResidualBytesTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"shard_residual_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    // ── Fixture helpers ──────────────────────────────────────────────────────

    /// <summary>A table leaf cell for <c>t</c>: payload length, rowid, then the record (NULL id, text v).</summary>
    private static byte[] Cell(long rowId, string v)
    {
        byte[] text = Encoding.UTF8.GetBytes(v);
        var payload = new List<byte> { 3, 0, (byte)(13 + 2 * text.Length) };
        payload.AddRange(text);
        return [(byte)payload.Count, (byte)rowId, .. payload];
    }

    private static int NonZero(IEnumerable<byte> bytes) => bytes.Count(b => b != 0);

    private sealed class PageBuilder
    {
        public readonly byte[] Data = new byte[PageSize];
        private readonly List<ushort> _live = [];
        private readonly List<ushort> _stale = [];
        private ushort _firstFreeblock;

        public PageBuilder Live(int offset, byte[] bytes) { Put(offset, bytes); _live.Add((ushort)offset); return this; }
        public PageBuilder Stale(int offset, byte[] bytes) { Put(offset, bytes); _stale.Add((ushort)offset); return this; }
        public PageBuilder Put(int offset, byte[] bytes) { bytes.CopyTo(Data, offset); return this; }

        public PageBuilder Freeblock(int offset, int size)
        {
            BinaryPrimitives.WriteUInt16BigEndian(Data.AsSpan(offset, 2), 0);
            BinaryPrimitives.WriteUInt16BigEndian(Data.AsSpan(offset + 2, 2), (ushort)size);
            _firstFreeblock = (ushort)offset;
            return this;
        }

        public TableBTreeLeafPage Build(int reservedBytes = 0)
        {
            Data[0] = (byte)PageType.BTreeLeafTable;
            BinaryPrimitives.WriteUInt16BigEndian(Data.AsSpan(1, 2), _firstFreeblock);
            BinaryPrimitives.WriteUInt16BigEndian(Data.AsSpan(3, 2), (ushort)_live.Count);
            BinaryPrimitives.WriteUInt16BigEndian(Data.AsSpan(5, 2), _live.Min());
            int p = 8;
            foreach (var o in _live.Concat(_stale)) { BinaryPrimitives.WriteUInt16BigEndian(Data.AsSpan(p, 2), o); p += 2; }
            return Assert.IsType<TableBTreeLeafPage>(
                SqlitePage.FromBytes(2, PageSize, Data, TextEncoding.Utf8, reservedBytes));
        }
    }

    // ── Counting ─────────────────────────────────────────────────────────────

    [Fact]
    public void Count_CountsEachByteOnce_WhenRangesOverlapOrOverrunTheArea()
    {
        byte[] data = [0, 1, 2, 3, 0, 5, 6, 7, 8, 9];
        var counted = ResidualBytes.Count(data, 2, 9, [new(0, 4), new(3, 6), new(8, 20)]);

        // Non-zero in [2,9): 2,3,5,6,7,8 = 6. Explained: 2,3 (0–4 and 3–6), 5 (3–6), 8 (8–20).
        Assert.Equal(new ResidualCount(6, 2), counted);
        Assert.Equal(4, counted.ExplainedNonZeroBytes);
    }

    [Fact]
    public void Count_IgnoresRangesOutsideTheArea()
    {
        byte[] data = [1, 1, 1, 1, 1, 1];
        Assert.Equal(new ResidualCount(2, 2), ResidualBytes.Count(data, 2, 4, [new(0, 2), new(4, 6)]));
    }

    // ── Table leaf pages (#24) ───────────────────────────────────────────────

    [Fact]
    public void Residual_FallsAsRecoveryExplainsMore_AndLeavesOnlyUnreadableBytes()
    {
        byte[] live = Cell(1, "live row"), deleted = Cell(2, "deleted"), carved = Cell(3, "carved");
        byte[] garbage = Enumerable.Repeat((byte)0x7A, 10).ToArray();
        var page = new PageBuilder()
            .Live(400, live)
            .Stale(300, deleted)     // pointer left after the live array: read as a deleted cell
            .Put(350, carved)        // no pointer: only carving finds it
            .Put(200, garbage)
            .Build();

        var region = Assert.Single(page.UnallocatedRegions, r => r.Offset < 400);
        Assert.Equal(10, region.Offset);   // after the header and the one live pointer
        int stalePointerBytes = NonZero(page.Data[10..12]);
        int expectedNonZero = stalePointerBytes + NonZero(deleted) + NonZero(carved) + garbage.Length;
        Assert.Equal(expectedNonZero, region.NonZeroBytes);

        // Before carving: the stale pointer and the deleted cell it points to are explained.
        Assert.Single(page.DeletedCells);
        Assert.Equal(new ResidualCount(expectedNonZero, NonZero(carved) + garbage.Length), page.Residual(region));

        page.CarveDeletedCells(Structure);
        page.CarveFreeblockCells(Structure);

        Assert.Contains(page.CarvedCells, c => c.PageOffset == 350);
        Assert.Equal(new ResidualCount(expectedNonZero, garbage.Length), page.Residual(region));
        // The count taken when the page was read doesn't change.
        Assert.Equal(expectedNonZero, region.NonZeroBytes);
    }

    [Fact]
    public void Residual_RisesAgain_WhenARecoveredCellIsRemoved()
    {
        byte[] carved = Cell(3, "carved");
        var page = new PageBuilder().Live(400, Cell(1, "live row")).Put(350, carved).Build();
        var region = page.UnallocatedRegions.First();

        page.CarveDeletedCells(Structure);
        Assert.Equal(0, page.Residual(region).UnexplainedNonZeroBytes);

        page.CarvedCells.Clear();
        Assert.Equal(NonZero(carved), page.Residual(region).UnexplainedNonZeroBytes);
    }

    [Fact]
    public void Residual_CountsAnnotatedCellsAsExplained()
    {
        byte[] carved = Cell(3, "carved");
        var page = new PageBuilder().Live(400, Cell(1, "live row")).Put(350, carved).Build();

        page.AnnotatedCells.Add(DeletedRecordParser.RecoverBTreeLeafRecord(page.Data, 350, TextEncoding.Utf8).Cell!);

        Assert.Equal(0, page.UnallocatedResidual().UnexplainedNonZeroBytes);
    }

    [Fact]
    public void Residual_CountsCellsCarvedFromTheWholePageAsExplained()
    {
        var page = new PageBuilder().Live(400, Cell(1, "live row")).Put(350, Cell(3, "carved")).Build();

        page.CarvedRecoveredCells.Add(DeletedRecordParser.RecoverBTreeLeafRecord(page.Data, 350, TextEncoding.Utf8).Cell!);

        Assert.Equal(0, page.UnallocatedResidual().UnexplainedNonZeroBytes);
    }

    [Fact]
    public void Residual_CountsFreeblockHeadersAndReservedBytesAsExplained()
    {
        const int reserved = 8;
        var builder = new PageBuilder()
            .Live(400, Cell(1, "live row"))
            .Freeblock(250, 40)
            .Put(PageSize - reserved, Enumerable.Repeat((byte)0xEE, reserved).ToArray());
        var page = builder.Build(reserved);

        var total = page.UnallocatedResidual();
        Assert.Equal(1 + reserved, total.NonZeroBytes); // the freeblock size byte, and the reserved bytes
        Assert.Equal(0, total.UnexplainedNonZeroBytes);
    }

    [Fact]
    public void UnallocatedResidual_SumsEveryRegion()
    {
        var page = new PageBuilder()
            .Live(300, Cell(1, "a"))
            .Live(400, Cell(2, "b"))
            .Put(100, [0x7A, 0x7A])     // before the first live cell
            .Put(500, [0x7A, 0x7A, 0x7A]) // after the last
            .Build();

        Assert.True(page.UnallocatedRegions.Count >= 2);
        Assert.Equal(new ResidualCount(5, 5), page.UnallocatedResidual());
    }

    /// <summary>A database SQLite wrote, with deletes recovery can explain fully or partly.</summary>
    [Fact]
    public void Residual_OnASqliteWrittenPage_IsBelowTheNonZeroCount_AfterRecovery()
    {
        string db = Path.Combine(_dir, "t.db");
        using (var c = new SqliteConnection($"Data Source={db};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                PRAGMA page_size=512; PRAGMA secure_delete=OFF;
                CREATE TABLE t (id INTEGER PRIMARY KEY, v TEXT);
                WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 12)
                  INSERT INTO t SELECT i, printf('value %02d', i) FROM n;
                DELETE FROM t WHERE id IN (3, 7, 8);
                """;
            cmd.ExecuteNonQuery();
        }

        using var database = SqliteForensicDatabase.Open(db);
        var page = Assert.IsType<TableBTreeLeafPage>(database.ReadPage(2));
        var before = page.UnallocatedResidual();
        Assert.True(before.NonZeroBytes > 0);

        page.CarveDeletedCells(Structure);
        page.CarveFreeblockCells(Structure);
        var after = page.UnallocatedResidual();

        Assert.Equal(before.NonZeroBytes, after.NonZeroBytes);
        Assert.True(after.UnexplainedNonZeroBytes < before.UnexplainedNonZeroBytes,
            $"recovery should explain some bytes: before {before}, after {after}");
    }

    // ── Whole pages (#28) ───────────────────────────────────────────────────

    /// <summary>
    /// A virtual table (here an R-tree, as in corpus 06) is listed in sqlite_master with root page
    /// 0. Finding the unclaimed pages, which orphan carving and the residual counts both do, must
    /// skip it rather than read page 0.
    /// </summary>
    [Fact]
    public void GetUnclaimedPageNumbers_SkipsVirtualTables()
    {
        string db = Path.Combine(_dir, "rtree.db");
        using (var c = new SqliteConnection($"Data Source={db};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "CREATE VIRTUAL TABLE coordinates USING rtree(id, min_x, max_x); " +
                              "INSERT INTO coordinates VALUES (1, 0, 10), (2, 5, 15);";
            cmd.ExecuteNonQuery();
        }

        using var database = SqliteForensicDatabase.Open(db);
        Assert.Contains(database.ReadSqliteMaster(), r => r.Name == "coordinates" && r.RootPage == 0); // the premise
        Assert.Empty(database.GetUnclaimedPageNumbers());
        Assert.Empty(OrphanPageCarver.Carve(database, OrphanPageCarver.BuildCandidates(database, CarveMode.Loose), out _));
        SqliteRecoveryFacade.GetPageResiduals(db, CarveMode.Loose); // doesn't throw
    }

    /// <summary>
    /// A CREATE statement too long for one page spills into overflow pages. They belong to
    /// sqlite_master, so the orphan-page carver mustn't scan them, nor count their text as
    /// unexplained.
    /// </summary>
    [Fact]
    public void GetUnclaimedPageNumbers_ClaimsSqliteMastersOverflowPages()
    {
        string db = Path.Combine(_dir, "longschema.db");
        string columns = string.Join(", ", Enumerable.Range(0, 80).Select(i => $"column_with_a_long_name_{i:D3} TEXT"));
        using (var c = new SqliteConnection($"Data Source={db};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"PRAGMA page_size=512; CREATE TABLE wide ({columns});";
            cmd.ExecuteNonQuery();
        }

        using var database = SqliteForensicDatabase.Open(db);
        Assert.Contains(database.ReadTableRows(1), r => r.OverflowFragments.Count > 0); // the premise
        Assert.Empty(database.GetUnclaimedPageNumbers());
    }

    [Fact]
    public void ForWholePage_ExplainsCarvedCells_AndLeavesTheRest()
    {
        var data = new byte[PageSize];
        byte[] cell = Cell(5, "orphan");
        cell.CopyTo(data, 100);
        new byte[] { 0x7A, 0x7A, 0x7A }.CopyTo(data, 300);
        var page = SqlitePage.FromBytes(3, PageSize, data, TextEncoding.Utf8, 0);
        var carved = DeletedRecordParser.RecoverBTreeLeafRecord(data, 100, TextEncoding.Utf8).Cell!;

        Assert.Equal(new ResidualCount(NonZero(cell) + 3, NonZero(cell) + 3), ResidualBytes.ForWholePage(page, 0, []));
        Assert.Equal(new ResidualCount(NonZero(cell) + 3, 3), ResidualBytes.ForWholePage(page, 0, [carved]));
    }

    [Fact]
    public void ForWholePage_ExplainsSurvivingPointersToCarvedCells_AndNoOtherSlots()
    {
        var data = new byte[PageSize];
        byte[] a = Cell(1, "first"), b = Cell(2, "second");
        a.CopyTo(data, 300);
        b.CopyTo(data, 400);
        // A trunk header over the old leaf header, then the old pointer array: 400, 300, and a
        // pointer to a cell that wasn't carved.
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(4, 4), 0);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(8, 2), 400);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(10, 2), 300);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(12, 2), 0x0123);
        var page = SqlitePage.FromBytes(3, PageSize, data, TextEncoding.Utf8, 0);
        var cells = new[] { 300, 400 }
            .Select(o => DeletedRecordParser.RecoverBTreeLeafRecord(data, o, TextEncoding.Utf8).Cell!).ToList();

        Assert.Equal([new ByteRange(8, 10), new ByteRange(10, 12)], ResidualBytes.PointersTo(data, cells));
        Assert.Equal(2, ResidualBytes.ForWholePage(page, 0, cells).UnexplainedNonZeroBytes); // 0x01, 0x23
    }

    /// <summary>The GUI reads orphan-carve results back from the project to show them on the page.</summary>
    [Fact]
    public void ReadOrphanCarvedCells_ReturnsWhatCarveUnknownPagesStored()
    {
        string db = Path.Combine(_dir, "orphan.db");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "TestData", "SHARDCreated", "Carving", "carving_orphan_leaf.db"), db);
        using var database = SqliteForensicDatabase.Open(db);
        var (project, _) = ShadowProject.CreateTemporary(db, database);
        using (project)
        {
            Assert.Empty(project.ReadOrphanCarvedCells(8));

            var candidates = OrphanPageCarver.BuildCandidates(database, CarveMode.Loose);
            var expected = OrphanPageCarver.Carve(database, candidates, out _)
                .Where(r => r.PageNumber == 8).Select(r => ("moz_places", r.Cell.PageOffset)).Order().ToList();
            project.CarveUnknownPages(database, candidates, out _);

            Assert.Equal(78, expected.Count);
            Assert.Equal(expected, project.ReadOrphanCarvedCells(8).Order());
        }
    }

    [Fact]
    public void StructureRanges_OnPage1_IncludeTheDatabaseHeader()
    {
        var data = new byte[PageSize];
        "SQLite format 3\0"u8.ToArray().CopyTo(data, 0);
        data[100] = (byte)PageType.BTreeLeafTable;
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(105, 2), PageSize); // empty leaf
        var page = SqlitePage.FromBytes(1, PageSize, data, TextEncoding.Utf8, 0);

        Assert.Contains(new ByteRange(0, 100), ResidualBytes.StructureRanges(page, 0));
        Assert.Equal(0, ResidualBytes.ForWholePage(page, 0, []).UnexplainedNonZeroBytes);
    }

    [Fact]
    public void ForWholePage_ExplainsAFreelistTrunksHeaderAndLeafList()
    {
        var data = new byte[PageSize];
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0, 4), 9);  // next trunk
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(4, 4), 2);  // two leaves
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8, 4), 4);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(12, 4), 5);
        data[200] = 0x7A;
        var trunk = new FreelistTrunkPage(3, PageSize, data);

        Assert.Equal(new ResidualCount(5, 1), ResidualBytes.ForWholePage(trunk, 0, [])); // 9, 2, 4, 5 and the stray byte
    }
}
