using Microsoft.Data.Sqlite;
using SHARD.Core.Pages;

namespace SHARD.Core.Tests;

/// <summary>
/// Verifies that ResolveOverflow correctly decodes every SQLite value type (Text, Blob,
/// Real, Integer) for both table-leaf and index-leaf cells.
///
/// Table-leaf tests use 4100-byte payloads on default 4096-byte pages (X = 4061 bytes,
/// so overflow occurs and the local portion is M ≈ 489 bytes). A single overflow page
/// holds the remaining ~3600 bytes, so no multi-page chain logic is needed.
///
/// Index-leaf tests use a 1200-char key: index cells overflow at X ≈ 1001 bytes on a
/// 4096-byte page, so a single 1200-char key is enough.
///
/// For Real and Integer tests, a 4100-char text column precedes the target field so
/// that the target field's start offset (~4104 bytes) is well past the local payload
/// minimum (M ≈ 489 bytes), guaranteeing the field lands in the overflow portion.
/// </summary>
public class OverflowParsingTests
{
    private static string TempDb() =>
        Path.Combine(Path.GetTempPath(), $"shard_ovf_{Guid.NewGuid():N}.db");

    private static uint GetRootPage(string path, string name)
    {
        using var conn = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT rootpage FROM sqlite_master WHERE name = @n";
        cmd.Parameters.AddWithValue("@n", name);
        return (uint)(long)cmd.ExecuteScalar()!;
    }

    // ── Table leaf: Text ──────────────────────────────────────────────────────

    [Fact]
    public void TableLeafCell_TextInOverflow_ResolvesCorrectly()
    {
        string path = TempDb();
        string expected = new string('T', 4100);
        try
        {
            using (var conn = new SqliteConnection($"Data Source={path}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "CREATE TABLE t (id INTEGER PRIMARY KEY, val TEXT)";
                cmd.ExecuteNonQuery();
                using var ins = conn.CreateCommand();
                ins.CommandText = "INSERT INTO t VALUES (1, @v)";
                ins.Parameters.AddWithValue("@v", expected);
                ins.ExecuteNonQuery();
            }

            using var db = SqliteForensicDatabase.Open(path);
            var page = (TableBTreeLeafPage)db.ReadPage(GetRootPage(path, "t"));
            var cell = page.Cells.Single(c => c.OverflowPage != 0);

            // FieldValues[0] = id (always null in the record — INTEGER PRIMARY KEY is stored
            // as the cell's rowid, not as a payload field). FieldValues[1] = val (in overflow).
            Assert.Null(cell.FieldValues[1]);

            db.ResolveOverflow(cell);

            Assert.Equal(expected, cell.FieldValues[1]!.TextValue);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // ── Table leaf: Blob ──────────────────────────────────────────────────────

    [Fact]
    public void TableLeafCell_BlobInOverflow_ResolvesCorrectly()
    {
        string path = TempDb();
        byte[] expected = Enumerable.Range(0, 4100).Select(i => (byte)(i & 0xFF)).ToArray();
        try
        {
            using (var conn = new SqliteConnection($"Data Source={path}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "CREATE TABLE t (id INTEGER PRIMARY KEY, val BLOB)";
                cmd.ExecuteNonQuery();
                using var ins = conn.CreateCommand();
                ins.CommandText = "INSERT INTO t VALUES (1, @v)";
                ins.Parameters.AddWithValue("@v", expected);
                ins.ExecuteNonQuery();
            }

            using var db = SqliteForensicDatabase.Open(path);
            var page = (TableBTreeLeafPage)db.ReadPage(GetRootPage(path, "t"));
            var cell = page.Cells.Single(c => c.OverflowPage != 0);

            Assert.Null(cell.FieldValues[1]);

            db.ResolveOverflow(cell);

            Assert.Equal(expected, cell.FieldValues[1]!.BlobValue);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // ── Table leaf: Real ──────────────────────────────────────────────────────

    [Fact]
    public void TableLeafCell_RealInOverflow_ResolvesCorrectly()
    {
        // A 4100-char text column pushes the total payload past the overflow threshold
        // (X = 4061 bytes on a 4096-byte page). The REAL field starts at ~byte 4104 of
        // the payload, which is well past the local minimum M ≈ 489 bytes, so it lands
        // in the overflow portion and is null until ResolveOverflow is called.
        string path = TempDb();
        const double expected = 3.14159265358979;
        try
        {
            using (var conn = new SqliteConnection($"Data Source={path}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "CREATE TABLE t (id INTEGER PRIMARY KEY, str TEXT, flt REAL)";
                cmd.ExecuteNonQuery();
                using var ins = conn.CreateCommand();
                ins.CommandText = "INSERT INTO t VALUES (1, @s, @f)";
                ins.Parameters.AddWithValue("@s", new string('X', 4100));
                ins.Parameters.AddWithValue("@f", expected);
                ins.ExecuteNonQuery();
            }

            using var db = SqliteForensicDatabase.Open(path);
            var page = (TableBTreeLeafPage)db.ReadPage(GetRootPage(path, "t"));
            var cell = page.Cells.Single(c => c.OverflowPage != 0);

            // FieldValues[0]=id (null), FieldValues[1]=str (in overflow), FieldValues[2]=flt (in overflow)
            Assert.Null(cell.FieldValues[1]);
            Assert.Null(cell.FieldValues[2]);

            db.ResolveOverflow(cell);

            Assert.Equal(expected, cell.FieldValues[2]!.RealValue!.Value);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // ── Table leaf: Integer ───────────────────────────────────────────────────

    [Fact]
    public void TableLeafCell_IntegerInOverflow_ResolvesCorrectly()
    {
        // Same layout as the Real test: a large text column pushes the INTEGER field
        // past the local payload boundary.
        string path = TempDb();
        const long expected = 123456789L;
        try
        {
            using (var conn = new SqliteConnection($"Data Source={path}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "CREATE TABLE t (id INTEGER PRIMARY KEY, str TEXT, num INTEGER)";
                cmd.ExecuteNonQuery();
                using var ins = conn.CreateCommand();
                ins.CommandText = "INSERT INTO t VALUES (1, @s, @n)";
                ins.Parameters.AddWithValue("@s", new string('X', 4100));
                ins.Parameters.AddWithValue("@n", expected);
                ins.ExecuteNonQuery();
            }

            using var db = SqliteForensicDatabase.Open(path);
            var page = (TableBTreeLeafPage)db.ReadPage(GetRootPage(path, "t"));
            var cell = page.Cells.Single(c => c.OverflowPage != 0);

            // FieldValues[0]=id (null), FieldValues[1]=str (in overflow), FieldValues[2]=num (in overflow)
            Assert.Null(cell.FieldValues[1]);
            Assert.Null(cell.FieldValues[2]);

            db.ResolveOverflow(cell);

            Assert.Equal(expected, cell.FieldValues[2]!.IntegerValue!.Value);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // ── Index leaf: Text ──────────────────────────────────────────────────────

    [Fact]
    public void IndexLeafCell_TextInOverflow_ResolvesCorrectly()
    {
        // Index cells on a 4096-byte page overflow at ~1001 bytes (X = ((U-12)*64/255)-23).
        // A 1200-char indexed key reliably triggers overflow with a single overflow page.
        string path = TempDb();
        string expected = new string('I', 1200);
        try
        {
            using (var conn = new SqliteConnection($"Data Source={path}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "CREATE TABLE t (id INTEGER PRIMARY KEY, name TEXT)";
                cmd.ExecuteNonQuery();
                using var idx = conn.CreateCommand();
                idx.CommandText = "CREATE INDEX idx_name ON t(name)";
                idx.ExecuteNonQuery();
                using var ins = conn.CreateCommand();
                ins.CommandText = "INSERT INTO t VALUES (1, @v)";
                ins.Parameters.AddWithValue("@v", expected);
                ins.ExecuteNonQuery();
            }

            using var db = SqliteForensicDatabase.Open(path);
            var page = (IndexBTreeLeafPage)db.ReadPage(GetRootPage(path, "idx_name"));
            var cell = page.Cells.Single(c => c.OverflowPage != 0);

            Assert.Null(cell.FieldValues[0]);

            db.ResolveOverflow(cell);

            Assert.Equal(expected, cell.FieldValues[0]!.TextValue);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
