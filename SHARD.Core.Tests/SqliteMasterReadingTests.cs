using SHARD.Core;
using SHARD.Core.Enums;

namespace SHARD.Core.Tests;

public class SqliteMasterReadingTests
{
    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "TestData", name);

    private static SqliteForensicDatabase Open(string fixtureName) =>
        SqliteForensicDatabase.Open(FixturePath(fixtureName));

    [Fact]
    public void SingleLeafPage_NoOverflow_ReadsAllRows()
    {
        using var db = Open("single_leaf_no_overflow.db");

        Assert.IsType<SHARD.Core.Pages.TableBTreeLeafPage>(db.ReadPage(1));

        var rows = db.ReadSqliteMaster().ToList();

        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.Name == "people" && r.ObjectType == SqliteMasterObjectType.Table);
        Assert.Contains(rows, r => r.Name == "notes" && r.ObjectType == SqliteMasterObjectType.Table);
        Assert.All(rows, r => Assert.True(r.RootPage > 0));
        Assert.All(rows, r => Assert.False(string.IsNullOrEmpty(r.Sql)));
    }

    [Fact]
    public void SingleLeafPage_WithOverflow_ReconstructsFullSql()
    {
        using var db = Open("single_leaf_with_overflow.db");

        Assert.IsType<SHARD.Core.Pages.TableBTreeLeafPage>(db.ReadPage(1));

        var rows = db.ReadSqliteMaster().ToList();

        Assert.Equal(2, rows.Count);

        var wideTable = rows.Single(r => r.Name == "wide_table");
        Assert.NotNull(wideTable.Sql);
        Assert.Equal(1076, wideTable.Sql!.Length);
        Assert.StartsWith("CREATE TABLE wide_table", wideTable.Sql);
        Assert.Contains("column_with_a_fairly_long_name_24", wideTable.Sql);
        Assert.EndsWith(")", wideTable.Sql.TrimEnd());

        var smallTable = rows.Single(r => r.Name == "small_table");
        Assert.Equal("CREATE TABLE small_table (id INTEGER)", smallTable.Sql);
    }

    [Fact]
    public void InteriorRootPage_NoOverflow_ReadsAllRowsAcrossLeaves()
    {
        using var db = Open("interior_no_overflow.db");

        Assert.IsType<SHARD.Core.Pages.TableBTreeInteriorPage>(db.ReadPage(1));

        var rows = db.ReadSqliteMaster().ToList();

        Assert.Equal(40, rows.Count);
        for (int i = 0; i < 40; i++)
        {
            var name = $"t{i:00}";
            Assert.Contains(rows, r => r.Name == name && r.ObjectType == SqliteMasterObjectType.Table);
        }
    }

    [Fact]
    public void InteriorRootPage_WithOverflow_ReconstructsAllRows()
    {
        using var db = Open("test_multipage_schema.db");

        Assert.IsType<SHARD.Core.Pages.TableBTreeInteriorPage>(db.ReadPage(1));

        var rows = db.ReadSqliteMaster().ToList();

        // 60 forensic tables + sqlite_sequence = 61 "table" rows, plus one auto-index per
        // forensic table (60) = 121 total sqlite_master rows.
        Assert.Equal(121, rows.Count);

        var tableRows = rows.Where(r => r.ObjectType == SqliteMasterObjectType.Table).ToList();
        Assert.All(tableRows, r => Assert.False(string.IsNullOrEmpty(r.TableName)));
        Assert.All(tableRows, r => Assert.True(r.RootPage > 0));
        Assert.All(tableRows, r => Assert.False(string.IsNullOrEmpty(r.Sql)));

        var firstTable = rows.Single(r => r.Name == "forensic_table_000");
        Assert.Equal("forensic_table_000", firstTable.TableName);
        Assert.Contains("CREATE TABLE forensic_table_000", firstTable.Sql);
        Assert.Contains("notes       BLOB", firstTable.Sql);
    }

    [Fact]
    public void OverflowingCell_HighlightBoundsStayWithinPage()
    {
        using var db = Open("test_multipage_schema.db");
        var pageSize = db.Header.PageSize;

        var rows = db.ReadSqliteMaster().ToList();

        Assert.All(rows, r => Assert.True(r.CellOffset + r.CellLength <= pageSize,
            $"{r.Name}: cell [{r.CellOffset}, {r.CellOffset + r.CellLength}) exceeds page size {pageSize}"));
    }
}
