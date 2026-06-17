using System.Buffers.Binary;
using Microsoft.Data.Sqlite;
using SHARD.Core.Pages;

namespace SHARD.Core.Tests;

public class FreelistParsingTests
{
    // ── FreelistTrunkPage unit tests ─────────────────────────────────────────

    [Fact]
    public void FreelistTrunkPage_ParsesNextTrunkAndLeaves()
    {
        int pageSize = 4096;
        var data = new byte[pageSize];

        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0, 4), 7);   // next trunk = page 7
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(4, 4), 3);   // 3 leaf entries
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8,  4), 12); // leaf 1 = page 12
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(12, 4), 15); // leaf 2 = page 15
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(16, 4), 22); // leaf 3 = page 22

        var trunk = new FreelistTrunkPage(5, pageSize, data);

        Assert.Equal(5u,  trunk.PageNumber);
        Assert.Equal(7u,  trunk.NextTrunkPageNumber);
        Assert.Equal(3u,  trunk.LeafCount);
        Assert.Equal([12u, 15u, 22u], trunk.LeafPageNumbers);
    }

    [Fact]
    public void FreelistTrunkPage_LastInChain_HasNextTrunkZero()
    {
        int pageSize = 4096;
        var data = new byte[pageSize];

        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0, 4), 0); // no next trunk
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(4, 4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8, 4), 9);

        var trunk = new FreelistTrunkPage(3, pageSize, data);

        Assert.Equal(0u,  trunk.NextTrunkPageNumber);
        Assert.Equal(1u,  trunk.LeafCount);
        Assert.Equal([9u], trunk.LeafPageNumbers);
    }

    [Fact]
    public void FreelistTrunkPage_CorruptLeafCount_ClampsToPageCapacity()
    {
        int pageSize = 4096;
        var data = new byte[pageSize];

        uint maxEntries = (uint)((pageSize - 8) / 4); // 1022 for 4096-byte page
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0, 4), 0);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(4, 4), maxEntries + 999); // claims far more than fit

        var trunk = new FreelistTrunkPage(2, pageSize, data);

        Assert.Equal(maxEntries, (uint)trunk.LeafPageNumbers.Length);
    }

    // ── ReadFreelistChain integration tests ──────────────────────────────────

    private static SqliteForensicDatabase CreateDbWithFreelistPages(string path)
    {
        using (var conn = new SqliteConnection($"Data Source={path}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            // Insert enough large rows to span several pages, then drop the table
            // so all those pages are returned to the freelist.
            cmd.CommandText = """
                CREATE TABLE scratch (id INTEGER PRIMARY KEY, payload TEXT);
                INSERT INTO scratch (payload) VALUES (zeroblob(2000));
                INSERT INTO scratch (payload) VALUES (zeroblob(2000));
                INSERT INTO scratch (payload) VALUES (zeroblob(2000));
                INSERT INTO scratch (payload) VALUES (zeroblob(2000));
                INSERT INTO scratch (payload) VALUES (zeroblob(2000));
                DROP TABLE scratch;
                """;
            cmd.ExecuteNonQuery();
        }
        return SqliteForensicDatabase.Open(path);
    }

    [Fact]
    public void ReadFreelistChain_EmptyFreelist_ReturnsEmpty()
    {
        string path = Path.Combine(Path.GetTempPath(), $"shard_fl_{Guid.NewGuid():N}.db");
        try
        {
            using (var conn = new SqliteConnection($"Data Source={path}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "CREATE TABLE t (id INTEGER PRIMARY KEY)";
                cmd.ExecuteNonQuery();
            }

            using var db = SqliteForensicDatabase.Open(path);
            Assert.Equal(0u, db.Header.FirstFreelistTrunkPage);
            Assert.Empty(db.ReadFreelistChain());
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void ReadFreelistChain_ReturnsAllTrunkPages()
    {
        string path = Path.Combine(Path.GetTempPath(), $"shard_fl_{Guid.NewGuid():N}.db");
        try
        {
            using var db = CreateDbWithFreelistPages(path);

            Assert.NotEqual(0u, db.Header.FirstFreelistTrunkPage);
            Assert.NotEqual(0u, db.Header.TotalFreelistPages);

            var trunks = db.ReadFreelistChain().ToList();
            Assert.NotEmpty(trunks);
            // First trunk must be the one the header points to
            Assert.Equal(db.Header.FirstFreelistTrunkPage, trunks[0].PageNumber);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void ReadFreelistChain_TotalPageCountMatchesHeader()
    {
        string path = Path.Combine(Path.GetTempPath(), $"shard_fl_{Guid.NewGuid():N}.db");
        try
        {
            using var db = CreateDbWithFreelistPages(path);

            var trunks = db.ReadFreelistChain().ToList();
            uint totalAccounted = (uint)(trunks.Count + trunks.Sum(t => (long)t.LeafPageNumbers.Length));
            Assert.Equal(db.Header.TotalFreelistPages, totalAccounted);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void ReadFreelistChain_TrunkPagesAreInChainOrder()
    {
        string path = Path.Combine(Path.GetTempPath(), $"shard_fl_{Guid.NewGuid():N}.db");
        try
        {
            using var db = CreateDbWithFreelistPages(path);

            var trunks = db.ReadFreelistChain().ToList();
            for (int i = 0; i < trunks.Count - 1; i++)
                Assert.Equal(trunks[i].NextTrunkPageNumber, trunks[i + 1].PageNumber);

            Assert.Equal(0u, trunks[^1].NextTrunkPageNumber);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
