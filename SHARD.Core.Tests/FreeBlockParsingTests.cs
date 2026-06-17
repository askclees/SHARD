using System.Buffers.Binary;
using Microsoft.Data.Sqlite;
using SHARD.Core.Pages;

namespace SHARD.Core.Tests;

public class FreeBlockParsingTests
{
    // ── Unit tests — parse known raw byte layouts ─────────────────────────────

    [Fact]
    public void TableBTreeLeafPage_NoFreeblocks_ReturnEmptyList()
    {
        // A fresh page with FirstFreeblock = 0 should have no freeblocks.
        string path = Path.Combine(Path.GetTempPath(), $"shard_fb_{Guid.NewGuid():N}.db");
        try
        {
            using (var conn = new SqliteConnection($"Data Source={path}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "CREATE TABLE t (id INTEGER PRIMARY KEY, val TEXT); INSERT INTO t VALUES (1, 'hello');";
                cmd.ExecuteNonQuery();
            }

            using var db = SqliteForensicDatabase.Open(path);
            var page = db.ReadPage(1) as TableBTreeLeafPage
                       ?? throw new Exception("Expected TableBTreeLeafPage for page 1");

            // sqlite_master leaf with a single row — no deleted cells, no freeblocks
            Assert.Empty(page.FreeBlocks);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void TableBTreeLeafPage_AfterDelete_HasFreeblocks()
    {
        string path = Path.Combine(Path.GetTempPath(), $"shard_fb_{Guid.NewGuid():N}.db");
        try
        {
            using (var conn = new SqliteConnection($"Data Source={path}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                // Insert several rows then delete some — deleted cell space becomes freeblocks.
                cmd.CommandText = """
                    CREATE TABLE t (id INTEGER PRIMARY KEY, val TEXT);
                    INSERT INTO t VALUES (1, 'alpha');
                    INSERT INTO t VALUES (2, 'beta');
                    INSERT INTO t VALUES (3, 'gamma');
                    DELETE FROM t WHERE id = 2;
                    """;
                cmd.ExecuteNonQuery();
            }

            long leafPageNum;
            using (var verify = new SqliteConnection($"Data Source={path};Mode=ReadOnly"))
            {
                verify.Open();
                using var cmd = verify.CreateCommand();
                cmd.CommandText = "SELECT rootpage FROM sqlite_master WHERE name = 't'";
                leafPageNum = (long)cmd.ExecuteScalar()!;
            }

            using var db = SqliteForensicDatabase.Open(path);
            var page = (TableBTreeLeafPage)db.ReadPage((uint)leafPageNum);

            Assert.NotEmpty(page.FreeBlocks);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void FreeBlock_OffsetAndSizeAreWithinPage()
    {
        string path = Path.Combine(Path.GetTempPath(), $"shard_fb_{Guid.NewGuid():N}.db");
        try
        {
            using (var conn = new SqliteConnection($"Data Source={path}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE t (id INTEGER PRIMARY KEY, val TEXT);
                    INSERT INTO t VALUES (1, 'alpha');
                    INSERT INTO t VALUES (2, 'beta');
                    INSERT INTO t VALUES (3, 'gamma');
                    DELETE FROM t WHERE id = 2;
                    """;
                cmd.ExecuteNonQuery();
            }

            long leafPageNum;
            using (var verify = new SqliteConnection($"Data Source={path};Mode=ReadOnly"))
            {
                verify.Open();
                using var cmd = verify.CreateCommand();
                cmd.CommandText = "SELECT rootpage FROM sqlite_master WHERE name = 't'";
                leafPageNum = (long)cmd.ExecuteScalar()!;
            }

            using var db = SqliteForensicDatabase.Open(path);
            var page = (TableBTreeLeafPage)db.ReadPage((uint)leafPageNum);

            foreach (var fb in page.FreeBlocks)
            {
                Assert.True(fb.PageOffset + fb.BlockSize <= page.PageSize,
                    $"Freeblock at {fb.PageOffset} with size {fb.BlockSize} exceeds page size {page.PageSize}");
                Assert.True(fb.BlockSize >= 4, "Freeblock size must be at least 4 bytes");
            }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void FreeBlocks_AreInAscendingOffsetOrder()
    {
        string path = Path.Combine(Path.GetTempPath(), $"shard_fb_{Guid.NewGuid():N}.db");
        try
        {
            using (var conn = new SqliteConnection($"Data Source={path}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                // Delete multiple rows to create multiple freeblocks.
                cmd.CommandText = """
                    CREATE TABLE t (id INTEGER PRIMARY KEY, val TEXT);
                    INSERT INTO t VALUES (1, 'alpha');
                    INSERT INTO t VALUES (2, 'beta');
                    INSERT INTO t VALUES (3, 'gamma');
                    INSERT INTO t VALUES (4, 'delta');
                    DELETE FROM t WHERE id IN (1, 3);
                    """;
                cmd.ExecuteNonQuery();
            }

            long leafPageNum;
            using (var verify = new SqliteConnection($"Data Source={path};Mode=ReadOnly"))
            {
                verify.Open();
                using var cmd = verify.CreateCommand();
                cmd.CommandText = "SELECT rootpage FROM sqlite_master WHERE name = 't'";
                leafPageNum = (long)cmd.ExecuteScalar()!;
            }

            using var db = SqliteForensicDatabase.Open(path);
            var page = (TableBTreeLeafPage)db.ReadPage((uint)leafPageNum);

            var offsets = page.FreeBlocks.Select(fb => fb.PageOffset).ToList();
            Assert.Equal(offsets.OrderBy(x => x).ToList(), offsets);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void FreeBlock_ChainLinksMatchParsedOffsets()
    {
        string path = Path.Combine(Path.GetTempPath(), $"shard_fb_{Guid.NewGuid():N}.db");
        try
        {
            using (var conn = new SqliteConnection($"Data Source={path}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE t (id INTEGER PRIMARY KEY, val TEXT);
                    INSERT INTO t VALUES (1, 'alpha');
                    INSERT INTO t VALUES (2, 'beta');
                    INSERT INTO t VALUES (3, 'gamma');
                    INSERT INTO t VALUES (4, 'delta');
                    DELETE FROM t WHERE id IN (1, 3);
                    """;
                cmd.ExecuteNonQuery();
            }

            long leafPageNum;
            using (var verify = new SqliteConnection($"Data Source={path};Mode=ReadOnly"))
            {
                verify.Open();
                using var cmd = verify.CreateCommand();
                cmd.CommandText = "SELECT rootpage FROM sqlite_master WHERE name = 't'";
                leafPageNum = (long)cmd.ExecuteScalar()!;
            }

            using var db = SqliteForensicDatabase.Open(path);
            var page = (TableBTreeLeafPage)db.ReadPage((uint)leafPageNum);
            var blocks = page.FreeBlocks;

            // Each freeblock's NextFreeblockPageOffset should point to the next block's offset,
            // and the last block should have NextFreeblockPageOffset == 0.
            for (int i = 0; i < blocks.Count - 1; i++)
                Assert.Equal(blocks[i + 1].PageOffset, blocks[i].NextFreeblockPageOffset);

            Assert.Equal(0u, blocks[^1].NextFreeblockPageOffset);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
