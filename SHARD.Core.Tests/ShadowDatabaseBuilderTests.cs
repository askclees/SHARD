using Microsoft.Data.Sqlite;
using SHARD.Core.Shadow;

namespace SHARD.Core.Tests;

public class ShadowDatabaseBuilderTests
{
    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "TestData", name);

    private static List<string> GetColumnNames(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{table}\")";
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
            names.Add(reader.GetString(reader.GetOrdinal("name")));
        return names;
    }

    private static List<string> GetTableNames(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
            names.Add(reader.GetString(0));
        return names;
    }

    [Fact]
    public void Create_MirrorsWideTableColumns()
    {
        string shadowPath = Path.Combine(Path.GetTempPath(), $"shard_shadow_{Guid.NewGuid():N}.db");
        try
        {
            using var db = SqliteForensicDatabase.Open(FixturePath("single_leaf_with_overflow.db"));
            ShadowDatabaseBuilder.Create(shadowPath, db);

            using var shadow = new SqliteConnection($"Data Source={shadowPath}");
            shadow.Open();

            var tables = GetTableNames(shadow);
            Assert.Contains("wide_table", tables);
            Assert.Contains("small_table", tables);

            var columns = GetColumnNames(shadow, "wide_table");
            Assert.Equal(28, columns.Count); // 25 source columns + 3 provenance columns
            Assert.Equal("column_with_a_fairly_long_name_00", columns[0]);
            Assert.Equal("column_with_a_fairly_long_name_24", columns[24]);
            Assert.Contains("_page_number", columns);
            Assert.Contains("_cell_offset", columns);
            Assert.Contains("_overflow_page", columns);
        }
        finally
        {
            if (File.Exists(shadowPath)) File.Delete(shadowPath);
        }
    }

    [Fact]
    public void Create_SkipsInternalSqliteTables()
    {
        string shadowPath = Path.Combine(Path.GetTempPath(), $"shard_shadow_{Guid.NewGuid():N}.db");
        try
        {
            using var db = SqliteForensicDatabase.Open(FixturePath("interior_no_overflow.db"));
            ShadowDatabaseBuilder.Create(shadowPath, db);

            using var shadow = new SqliteConnection($"Data Source={shadowPath}");
            shadow.Open();

            var tables = GetTableNames(shadow);
            Assert.Equal(42, tables.Count); // 40 source tables + _shard_overflow_pages + _shard_pages
            Assert.Contains("_shard_overflow_pages", tables);
            Assert.Contains("_shard_pages", tables);
            Assert.DoesNotContain(tables, t => t.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (File.Exists(shadowPath)) File.Delete(shadowPath);
        }
    }

    [Fact]
    public void Create_TagsSqliteMasterAndInteriorPages()
    {
        string shadowPath = Path.Combine(Path.GetTempPath(), $"shard_shadow_{Guid.NewGuid():N}.db");
        try
        {
            using var db = SqliteForensicDatabase.Open(FixturePath("interior_no_overflow.db"));
            ShadowDatabaseBuilder.Create(shadowPath, db);

            using var shadow = new SqliteConnection($"Data Source={shadowPath}");
            shadow.Open();

            // No page should be left untagged just because it belongs to sqlite_master
            // (whose mirrored table is intentionally skipped) or because it's an interior page.
            using var untaggedCommand = shadow.CreateCommand();
            untaggedCommand.CommandText = "SELECT COUNT(*) FROM \"_shard_pages\" WHERE table_name IS NULL AND page_type != 'Unknown'";
            long untaggedCount = (long)untaggedCommand.ExecuteScalar()!;
            Assert.Equal(0, untaggedCount);

            using var masterCommand = shadow.CreateCommand();
            masterCommand.CommandText = "SELECT page_type FROM \"_shard_pages\" WHERE page_number = 1";
            Assert.Equal("BTreeInteriorTable", (string)masterCommand.ExecuteScalar()!);

            using var masterTableCommand = shadow.CreateCommand();
            masterTableCommand.CommandText = "SELECT table_name FROM \"_shard_pages\" WHERE page_number = 1";
            Assert.Equal("sqlite_master", (string)masterTableCommand.ExecuteScalar()!);
        }
        finally
        {
            if (File.Exists(shadowPath)) File.Delete(shadowPath);
        }
    }

    [Fact]
    public void Create_CopiesRowDataWithProvenance()
    {
        string shadowPath = Path.Combine(Path.GetTempPath(), $"shard_shadow_{Guid.NewGuid():N}.db");
        try
        {
            using var db = SqliteForensicDatabase.Open(FixturePath("table_with_rows.db"));
            ShadowDatabaseBuilder.Create(shadowPath, db);

            using var shadow = new SqliteConnection($"Data Source={shadowPath}");
            shadow.Open();

            using var command = shadow.CreateCommand();
            command.CommandText = "SELECT id, name, note, _page_number, _cell_offset, _overflow_page FROM people ORDER BY id";
            using var reader = command.ExecuteReader();

            Assert.True(reader.Read());
            Assert.Equal(1L, reader.GetInt64(0));
            Assert.Equal("Alice", reader.GetString(1));
            Assert.Equal("short note", reader.GetString(2));
            Assert.True(reader.GetInt64(3) > 0);   // _page_number
            Assert.True(reader.GetInt64(5) == 0);  // _overflow_page: row 1 doesn't overflow

            Assert.True(reader.Read());
            Assert.Equal(2L, reader.GetInt64(0));
            Assert.Equal("Bob", reader.GetString(1));
            Assert.Equal(1000, reader.GetString(2).Length); // note re-assembled from overflow chain
            long overflowPage = reader.GetInt64(5);
            Assert.True(overflowPage > 0); // row 2's long note overflows

            Assert.False(reader.Read());

            using var overflowCommand = shadow.CreateCommand();
            overflowCommand.CommandText = "SELECT page_number, next_page_number, sequence FROM \"_shard_overflow_pages\" WHERE table_name = 'people' AND row_id = 2 ORDER BY sequence";
            using var overflowReader = overflowCommand.ExecuteReader();

            int expectedSequence = 1;
            long previousPage = overflowPage;
            while (overflowReader.Read())
            {
                Assert.Equal(previousPage, overflowReader.GetInt64(0));
                Assert.Equal(expectedSequence, overflowReader.GetInt64(2));
                previousPage = overflowReader.GetInt64(1);
                expectedSequence++;
            }
            Assert.Equal(0, previousPage); // chain terminates with next_page_number = 0
            Assert.True(expectedSequence > 1); // at least one fragment was recorded
        }
        finally
        {
            if (File.Exists(shadowPath)) File.Delete(shadowPath);
        }
    }

    [Fact]
    public void Create_PersistsPageClassifications()
    {
        string shadowPath = Path.Combine(Path.GetTempPath(), $"shard_shadow_{Guid.NewGuid():N}.db");
        try
        {
            using var db = SqliteForensicDatabase.Open(FixturePath("table_with_rows.db"));
            ShadowDatabaseBuilder.Create(shadowPath, db);

            using var shadow = new SqliteConnection($"Data Source={shadowPath}");
            shadow.Open();

            using var countCommand = shadow.CreateCommand();
            countCommand.CommandText = "SELECT COUNT(*) FROM \"_shard_pages\"";
            long pageRowCount = (long)countCommand.ExecuteScalar()!;
            Assert.Equal(db.PageCount, (uint)pageRowCount);

            using var overflowPageCommand = shadow.CreateCommand();
            overflowPageCommand.CommandText = "SELECT page_number FROM \"_shard_overflow_pages\" WHERE table_name = 'people' AND row_id = 2 ORDER BY sequence LIMIT 1";
            long overflowPage = (long)overflowPageCommand.ExecuteScalar()!;

            using var typeCommand = shadow.CreateCommand();
            typeCommand.CommandText = "SELECT page_type, table_name FROM \"_shard_pages\" WHERE page_number = @page";
            typeCommand.Parameters.AddWithValue("@page", overflowPage);
            using var typeReader = typeCommand.ExecuteReader();
            Assert.True(typeReader.Read());
            Assert.Equal("Overflow", typeReader.GetString(0));
            Assert.Equal("people", typeReader.GetString(1)); // overflow page is still attributed to its owning table

            using var leafPageCommand = shadow.CreateCommand();
            leafPageCommand.CommandText = "SELECT _page_number FROM people WHERE id = 1";
            long leafPage = (long)leafPageCommand.ExecuteScalar()!;

            using var leafTypeCommand = shadow.CreateCommand();
            leafTypeCommand.CommandText = "SELECT page_type, table_name FROM \"_shard_pages\" WHERE page_number = @page";
            leafTypeCommand.Parameters.AddWithValue("@page", leafPage);
            using var leafTypeReader = leafTypeCommand.ExecuteReader();
            Assert.True(leafTypeReader.Read());
            Assert.Equal("BTreeLeafTable", leafTypeReader.GetString(0));
            Assert.Equal("people", leafTypeReader.GetString(1));
        }
        finally
        {
            if (File.Exists(shadowPath)) File.Delete(shadowPath);
        }
    }
}
