using System.Text.Json;
using SHARD.Core.Recovery;

namespace SHARD.Cli.Tests;

/// <summary>
/// Regression tests for three bugs found and fixed in one pass: shard-cli's RowToDict silently
/// reporting fields under the wrong column name whenever a table's INTEGER PRIMARY KEY wasn't
/// the first column; `deleted` under-counting recoverable rows because it skipped the
/// freeblock/carve step the rest of the engine uses; and a raw NotImplementedException crash
/// (instead of a friendly error) on a file with a corrupted page 1. All three slipped in
/// silently because shard-cli duplicated logic that already existed, correctly, elsewhere
/// (SqliteRecoveryFacade) — these tests run the real, built CLI as a subprocess so they catch
/// drift the same way a user invoking shard-cli would notice it.
/// </summary>
public class CliRegressionTests
{
    [Fact]
    public void CliDll_IsRunnable()
    {
        var result = CliRunner.Run("--help");
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("shard-cli", result.Stdout);
    }

    [Fact]
    public void Rows_FieldsAreNotShifted_ForTableWithRowidAliasNotFirstColumn()
    {
        string dbPath = Fixtures.CreateRowidAliasNotFirstColumnDb();
        try
        {
            var result = CliRunner.Run("rows", dbPath, "items", "-f", "json");
            Assert.Equal(0, result.ExitCode);

            using var doc = JsonDocument.Parse(result.Stdout);
            var rows = doc.RootElement.GetProperty("rows");
            Assert.Equal(2, rows.GetArrayLength());

            var byId = new Dictionary<long, JsonElement>();
            foreach (var row in rows.EnumerateArray())
                byId[row.GetProperty("id").GetInt64()] = row;

            Assert.Equal("widget", byId[1].GetProperty("name").GetString());
            Assert.Equal(9.99, byId[1].GetProperty("price").GetDouble(), precision: 6);
            Assert.Equal("first", byId[1].GetProperty("note").GetString());

            Assert.Equal("gadget", byId[2].GetProperty("name").GetString());
            Assert.Equal(19.99, byId[2].GetProperty("price").GetDouble(), precision: 6);
            Assert.Equal("second", byId[2].GetProperty("note").GetString());
        }
        finally { File.Delete(dbPath); }
    }

    [Fact]
    public void Deleted_MatchesFacadeCount_AndIsNotUndercountingToZero()
    {
        string dbPath = Path.Combine(AppContext.BaseDirectory, "TestData", "Corpus", "0D", "0D-01.db");
        Assert.True(File.Exists(dbPath), $"Corpus fixture not found at {dbPath}.");

        int expectedCount = SqliteRecoveryFacade.GetDeletedRows(dbPath, "users").Count;
        Assert.True(expectedCount > 0, "Fixture sanity check: expected at least one recoverable deleted row.");

        var result = CliRunner.Run("deleted", dbPath, "users", "-f", "json");
        Assert.Equal(0, result.ExitCode);

        using var doc = JsonDocument.Parse(result.Stdout);
        int actualCount = doc.RootElement.GetProperty("count").GetInt32();
        Assert.Equal(expectedCount, actualCount);
    }

    [Fact]
    public void CorruptPage1_FailsWithFriendlyMessage_NotAStackTrace()
    {
        string dbPath = Fixtures.CreateCorruptPage1Db();
        try
        {
            var result = CliRunner.Run("schema", dbPath);

            Assert.Equal(2, result.ExitCode); // documented "usage or file error" exit code
            Assert.Contains("Not a valid SQLite file", result.Stderr);
            Assert.DoesNotContain("NotImplementedException", result.Stderr);
            Assert.DoesNotContain("Unhandled exception", result.Stderr);
        }
        finally { File.Delete(dbPath); }
    }
}
