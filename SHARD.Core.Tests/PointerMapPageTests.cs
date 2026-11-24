using System.Buffers.Binary;
using Microsoft.Data.Sqlite;
using SHARD.Core.Enums;
using SHARD.Core.Pages;
using SHARD.Core.Session;

namespace SHARD.Core.Tests;

/// <summary>
/// Pointer-map pages (auto-vacuum databases) and the lock-byte page, which are identified by page
/// number rather than content (see <see cref="PageLayout"/>). Every database fixture is written by
/// SQLite itself.
/// </summary>
public class PointerMapPageTests : IDisposable
{
    private readonly string _dir;

    public PointerMapPageTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"shard_ptrmap_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private const string Schema = """
        CREATE TABLE t (id INTEGER PRIMARY KEY, v TEXT);
        CREATE INDEX t_v ON t (v);
        """;

    // 60-character values: short enough that neither table nor index entries overflow at 512 bytes
    private const string Data = """
        WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 400)
          INSERT INTO t SELECT i, printf('%03d', i) || printf('%.*c', 57, 'x') FROM n;
        DELETE FROM t WHERE id % 7 = 0;
        """;

    private const string Rows = Schema + Data;

    /// <summary>
    /// A full auto-vacuum database with 512-byte pages: 102 entries per map page, so 400 rows and
    /// their index entries span more than one map page. Full auto-vacuum leaves no freelist.
    /// </summary>
    private string CreateAutoVacuumDatabase()
    {
        string db = Path.Combine(_dir, $"av_{Guid.NewGuid():N}.db");
        using var c = Open(db);
        Exec(c, "PRAGMA page_size=512; PRAGMA auto_vacuum=FULL;");
        Exec(c, Rows);
        return db;
    }

    // ── Which pages ──────────────────────────────────────────────────────────

    [Fact]
    public void PointerMapPages_AreExactlyThePagesNoBTreeUses()
    {
        string db = CreateAutoVacuumDatabase();
        using var database = SqliteForensicDatabase.Open(db);
        Assert.True(database.Layout.AutoVacuum);
        Assert.Equal(0u, database.Header.TotalFreelistPages); // the premise: every page is a tree page or a map page

        var treePages = TreePages(database).Keys.ToHashSet();
        var mapPages = Enumerable.Range(1, (int)database.PageCount).Select(p => (uint)p)
            .Where(database.Layout.IsPointerMapPage).ToList();

        Assert.True(mapPages.Count >= 2, $"expected the fixture to span several map pages, got {mapPages.Count}");
        Assert.Equal(2u, mapPages[0]);
        Assert.Equal(
            Enumerable.Range(1, (int)database.PageCount).Select(p => (uint)p).Where(p => !treePages.Contains(p)),
            mapPages);
        Assert.All(mapPages, p => Assert.IsType<PointerMapPage>(database.ReadPage(p)));
    }

    [Fact]
    public void PointerMapPage_WhoseFirstByteLooksLikeAnInteriorPage_IsNotReadAsOne()
    {
        string db = CreateAutoVacuumDatabase();
        using var database = SqliteForensicDatabase.Open(db);

        var lookalikes = Enumerable.Range(2, (int)database.PageCount - 1).Select(p => (uint)p)
            .Where(database.Layout.IsPointerMapPage)
            .Select(p => database.ReadPage(p))
            .Where(p => p.Data[0] is 0x02 or 0x05)
            .ToList();

        Assert.NotEmpty(lookalikes); // the premise: SQLite wrote a map page starting with an interior page's type byte
        Assert.All(lookalikes, p => Assert.Equal(PageType.PointerMap, p.PageType));
    }

    [Fact]
    public void Entries_DescribeEveryPage_WithItsParent()
    {
        string db = CreateAutoVacuumDatabase();
        using var database = SqliteForensicDatabase.Open(db);
        var tree = TreePages(database);

        var entries = Enumerable.Range(2, (int)database.PageCount - 1).Select(p => (uint)p)
            .Where(database.Layout.IsPointerMapPage)
            .SelectMany(p => ((PointerMapPage)database.ReadPage(p)).Entries)
            .Where(e => e.PageNumber <= database.PageCount)
            .ToDictionary(e => e.PageNumber);

        Assert.Equal(tree.Keys.Where(p => p != 1).Order(), entries.Keys.Order());
        foreach (var (page, parent) in tree)
        {
            if (page == 1) continue; // page 1 has no entry
            var entry = entries[page];
            if (parent == 0)
            {
                Assert.Equal(PointerMapEntryType.RootPage, entry.Type);
                Assert.Equal(0u, entry.ParentPage);
            }
            else
            {
                Assert.Equal(PointerMapEntryType.BTree, entry.Type);
                Assert.Equal(parent, entry.ParentPage);
            }
        }
    }

    [Fact]
    public void NotAutoVacuum_Page2IsAnOrdinaryPage()
    {
        string db = Path.Combine(_dir, "plain.db");
        using (var c = Open(db))
            Exec(c, "PRAGMA page_size=512;" + Rows);

        using var database = SqliteForensicDatabase.Open(db);

        Assert.False(database.Layout.AutoVacuum);
        Assert.False(database.Layout.IsPointerMapPage(2));
        Assert.IsAssignableFrom<BTreePage>(database.ReadPage(2));
    }

    [Fact]
    public void UnclaimedPages_LeaveOutPointerMapPages()
    {
        // With no freelist, the map pages are the only pages no table reaches
        string db = CreateAutoVacuumDatabase();
        using var database = SqliteForensicDatabase.Open(db);

        Assert.Empty(database.GetUnclaimedPageNumbers());
    }

    // ── Lock-byte page ───────────────────────────────────────────────────────

    [Fact]
    public void LockBytePage_TakesTheMapPagesPlace_AndTheMapMovesToTheNextPage()
    {
        // 1024-byte pages: 204 entries per map page, so map pages fall every 205 pages from page
        // 2, and one of them would be page 1,048,577: the lock-byte page (SQLite's ptrmapPageno).
        var layout = new PageLayout(1024, 0, AutoVacuum: true);
        const uint lockPage = 0x40000000 / 1024 + 1;
        Assert.Equal(lockPage, layout.LockBytePageNumber);
        Assert.Equal(0u, (lockPage - 2) % 205); // the premise

        Assert.False(layout.IsPointerMapPage(lockPage));
        Assert.True(layout.IsPointerMapPage(lockPage + 1));
        Assert.Equal(lockPage + 1, layout.PointerMapPageFor(lockPage + 2));
        Assert.True(layout.IsPointerMapPage(lockPage - 205));
        Assert.True(layout.IsPointerMapPage(lockPage + 205));

        // Without auto-vacuum there are no map pages, but the lock-byte page is still there
        var plain = layout with { AutoVacuum = false };
        Assert.False(plain.IsPointerMapPage(2));
        Assert.True(plain.IsLockBytePage(lockPage));
    }

    [Fact]
    public void LockBytePage_InAFileOver1GiB_IsRecognised()
    {
        // A real 1024-byte-page auto-vacuum database, extended (sparsely) past 1 GiB
        string db = Path.Combine(_dir, "big.db");
        using (var c = Open(db))
            Exec(c, "PRAGMA page_size=1024; PRAGMA auto_vacuum=FULL; CREATE TABLE t (id INTEGER PRIMARY KEY);");
        const uint lockPage = 0x40000000 / 1024 + 1;
        using (var fs = new FileStream(db, FileMode.Open, FileAccess.Write))
            fs.SetLength((long)(lockPage + 2) * 1024);
        using (var fs = new FileStream(db, FileMode.Open, FileAccess.Write))
        {
            // Bytes that would read as a table leaf page if the page were classified by content
            fs.Position = (long)(lockPage - 1) * 1024;
            fs.WriteByte(0x0D);
        }

        using var database = SqliteForensicDatabase.Open(db);

        Assert.IsType<LockBytePage>(database.ReadPage(lockPage));
        Assert.IsType<PointerMapPage>(database.ReadPage(lockPage + 1));
        Assert.DoesNotContain(lockPage, database.GetUnclaimedPageNumbers());
    }

    // ── WAL and journal copies ───────────────────────────────────────────────

    [Fact]
    public void WalFrames_OfPointerMapPages_AreRecognised()
    {
        string db = Path.Combine(_dir, "wal.db");
        string evidence = Path.Combine(_dir, "wal_evidence.db");
        using (var c = Open(db))
        {
            Exec(c, "PRAGMA page_size=512; PRAGMA auto_vacuum=FULL; PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0;");
            Exec(c, Schema + "PRAGMA wal_checkpoint(TRUNCATE);"); // the file gets its header
            Exec(c, Data); // not checkpointed: every page it writes, map pages included, is in the WAL
            CopyShared(db, evidence);
            CopyShared(db + "-wal", evidence + "-wal");
        }

        using var session = EvidenceSession.Open(evidence);

        var mapFrames = session.Wal!.Frames.Where(f => session.Database.Layout.IsPointerMapPage(f.Header.PageNumber)).ToList();
        Assert.NotEmpty(mapFrames);
        Assert.All(mapFrames, f => Assert.IsType<PointerMapPage>(f.Page));
        Assert.Contains(mapFrames, f => f.Page.Data[0] is 0x02 or 0x05); // one that would have been misread
    }

    [Fact]
    public void JournalRecords_OfPointerMapPages_AreRecognised()
    {
        string db = CreateAutoVacuumDatabase();
        string evidence = Path.Combine(_dir, "journal_evidence.db");
        using (var c = Open(db))
        {
            // Each page the inserts allocate gets a map entry, so the map pages are journaled
            // before they change; a 1-page cache spills the transaction to the file, so the
            // journal is hot when copied
            Exec(c, """
                PRAGMA cache_size=1; BEGIN;
                WITH RECURSIVE n(i) AS (SELECT 1000 UNION ALL SELECT i + 1 FROM n WHERE i < 1200)
                  INSERT INTO t SELECT i, printf('%04d', i) || printf('%.*c', 56, 'y') FROM n;
                """);
            CopyShared(db, evidence);
            CopyShared(db + "-journal", evidence + "-journal");
            Exec(c, "ROLLBACK;");
        }

        using var session = EvidenceSession.Open(evidence);

        var records = session.Journal!.Segments.SelectMany(s => s.PageRecords)
            .Where(r => session.Database.Layout.IsPointerMapPage(r.PageNumber)).ToList();
        Assert.NotEmpty(records);
        Assert.All(records, r => Assert.IsType<PointerMapPage>(r.Page));
    }

    [Fact]
    public void ZeroedHeaderJournal_KeepsPointerMapRecords()
    {
        // journal_mode=PERSIST zeroes the header on commit. The last transaction's inserts
        // allocate pages, so it journals map pages; page 2's first byte is a root-page entry
        // (0x01), which only the layout identifies as a page.
        string db = CreateAutoVacuumDatabase();
        using (var c = Open(db))
            Exec(c, """
                PRAGMA journal_mode=PERSIST;
                WITH RECURSIVE n(i) AS (SELECT 1000 UNION ALL SELECT i + 1 FROM n WHERE i < 1200)
                  INSERT INTO t SELECT i, printf('%04d', i) || printf('%.*c', 56, 'y') FROM n;
                """);

        using var session = EvidenceSession.Open(db, inferZeroedJournalHeader: true);

        Assert.True(session.Journal is not null, session.JournalError);
        var journal = session.Journal!;
        Assert.True(journal.HeaderInferred);
        var records = journal.Segments.SelectMany(s => s.PageRecords).ToList();
        var mapRecords = records.Where(r => session.Database.Layout.IsPointerMapPage(r.PageNumber)).ToList();
        Assert.Contains(mapRecords, r => r.PageNumber == 2 && r.Page.Data[0] == 0x01);
        Assert.All(mapRecords, r => Assert.IsType<PointerMapPage>(r.Page));
        Assert.All(records, r => Assert.True(r.IsChecksumValid));
    }

    [Fact]
    public void ZeroedHeaderJournal_StartingWithAPointerMapRecord_IsRead()
    {
        // SQLite's journal, with the records before page 2's cut out (as in a partly overwritten
        // or carved journal), so the first record is a map page whose first byte (0x01) isn't a
        // B-tree type byte. Only the layout tells the reader it's a page.
        string db = CreateAutoVacuumDatabase();
        using (var c = Open(db))
            Exec(c, """
                PRAGMA journal_mode=PERSIST;
                WITH RECURSIVE n(i) AS (SELECT 1000 UNION ALL SELECT i + 1 FROM n WHERE i < 1200)
                  INSERT INTO t SELECT i, printf('%04d', i) || printf('%.*c', 56, 'y') FROM n;
                """);
        long mapRecordOffset, sector;
        using (var session = EvidenceSession.Open(db, inferZeroedJournalHeader: true))
        {
            var journal = session.Journal!;
            sector = journal.Segments[0].Header.SizeOfDiskSector;
            mapRecordOffset = journal.Segments.SelectMany(s => s.PageRecords).First(r => r.PageNumber == 2).FileOffset;
        }
        Assert.True(mapRecordOffset > sector); // the premise: other records come before it
        var bytes = File.ReadAllBytes(db + "-journal");
        File.WriteAllBytes(db + "-journal", [.. bytes[..(int)sector], .. bytes[(int)mapRecordOffset..]]);

        using var cut = EvidenceSession.Open(db, inferZeroedJournalHeader: true);

        Assert.True(cut.Journal is not null, cut.JournalError);
        var first = cut.Journal!.Segments[0].PageRecords[0];
        Assert.Equal(2u, first.PageNumber);
        Assert.IsType<PointerMapPage>(first.Page);
        Assert.True(first.IsChecksumValid);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>Every page of every B-tree in the schema, with its parent (0 for a root page).</summary>
    private static Dictionary<uint, uint> TreePages(SqliteForensicDatabase database)
    {
        var parents = new Dictionary<uint, uint>();
        void Walk(uint page, uint parent)
        {
            parents.Add(page, parent);
            if (database.ReadPage(page) is not BTreeInteriorPage interior) return;
            foreach (ushort offset in interior.CellPointers)
                Walk(BinaryPrimitives.ReadUInt32BigEndian(interior.Data.AsSpan(offset, 4)), page);
            Walk(interior.RightmostPointer, page);
        }

        Walk(1, 0);
        foreach (var row in database.ReadSqliteMaster())
            if (row.RootPage is { } root and not 0) Walk(root, 0);
        return parents;
    }

    private static SqliteConnection Open(string path)
    {
        var c = new SqliteConnection($"Data Source={path};Pooling=False");
        c.Open();
        return c;
    }

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static void CopyShared(string from, string to)
    {
        using var src = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var dst = File.Create(to);
        src.CopyTo(dst);
    }
}
