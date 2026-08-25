using SHARD.Core.Recovery;
using Xunit;

namespace SHARD.Core.Tests;

/// <summary>
/// Exercises the high-level facade against the existing Carving fixtures, cross-checking against
/// the same known-correct counts already encoded in their .xml expectations
/// (see SHARDCreatedTests.CarvingRecords_MatchExpected for the original derivation).
/// </summary>
public class SqliteRecoveryFacadeTests
{
    private static readonly string CarvingDir =
        Path.Combine(AppContext.BaseDirectory, "TestData", "SHARDCreated", "Carving");

    [Fact]
    public void GetHeader_ReturnsPlausibleValues()
    {
        var header = SqliteRecoveryFacade.GetHeader(Path.Combine(CarvingDir, "carving_orphan_leaf.db"));
        Assert.Equal(4096, header.PageSize);
        Assert.StartsWith("SQLite format 3", header.Magic);
    }

    [Fact]
    public void GetSchema_ListsLiveTable()
    {
        var schema = SqliteRecoveryFacade.GetSchema(Path.Combine(CarvingDir, "carving_orphan_leaf.db"));
        Assert.Contains(schema, e => e.Type == "table" && e.Name == "moz_places");
    }

    [Fact]
    public void GetRows_MatchesLiveCount()
    {
        var rows = SqliteRecoveryFacade.GetRows(Path.Combine(CarvingDir, "carving_orphan_leaf.db"), "moz_places");
        Assert.Equal(299, rows.Count);
    }

    [Fact]
    public void CarveUnknownPages_OrphanLeaf_LooseMode_FindsAllRows()
    {
        var carved = SqliteRecoveryFacade.CarveUnknownPages(
            Path.Combine(CarvingDir, "carving_orphan_leaf.db"), CarveMode.Loose);

        Assert.Equal(156, carved.Count);
        Assert.All(carved, r => Assert.Equal("moz_places", r.TableName));
    }

    [Fact]
    public void CarveUnknownPages_AmbiguousTables_LooseModeFindsNothing_TightModeDisambiguates()
    {
        string dbPath = Path.Combine(CarvingDir, "carving_ambiguous_tables.db");

        var loose = SqliteRecoveryFacade.CarveUnknownPages(dbPath, CarveMode.Loose);
        Assert.Empty(loose);

        var tight = SqliteRecoveryFacade.CarveUnknownPages(dbPath, CarveMode.Tight);
        Assert.Equal(203, tight.Count);
        Assert.All(tight, r => Assert.Equal("table_a", r.TableName));
    }

    [Fact]
    public void Recover_WritesUsableDatabaseAndSummary()
    {
        string inputPath = Path.Combine(CarvingDir, "carving_orphan_leaf.db");
        string outputPath = Path.Combine(Path.GetTempPath(), $"shard_facade_test_{Guid.NewGuid():N}.db");
        try
        {
            var result = SqliteRecoveryFacade.Recover(inputPath, outputPath,
                new RecoveryOptions(ProcessWal: false, CarveMode: CarveMode.Loose));

            Assert.Equal(156, result.CarvedRecords);
            Assert.Equal(0, result.CarveAmbiguousSkipped);

            var mozPlaces = Assert.Single(result.Tables, t => t.TableName == "moz_places");
            Assert.Equal(299, mozPlaces.LiveRowCount);
            Assert.True(mozPlaces.RecoveredRowCount >= 156, "expected at least the carved rows to be reflected in the recovered count");

            Assert.True(File.Exists(outputPath));
        }
        finally
        {
            if (File.Exists(outputPath)) File.Delete(outputPath);
        }
    }
}
