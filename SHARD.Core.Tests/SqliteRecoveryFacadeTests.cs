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
    public void GetPageResiduals_WithoutOrphanCarve_CountsOnlyTableLeafPages()
    {
        var residuals = SqliteRecoveryFacade.GetPageResiduals(Path.Combine(CarvingDir, "carving_orphan_leaf.db"));

        Assert.NotEmpty(residuals);
        Assert.All(residuals, r =>
        {
            Assert.Equal("unallocated", r.Area);
            Assert.Equal("BTreeLeafTable", r.Type);
            Assert.Equal("moz_places", r.TableName);
            Assert.InRange(r.UnexplainedNonZeroBytes, 0, r.NonZeroBytes);
        });
    }

    /// <summary>
    /// Page 7 is the freelist trunk that replaced a leaf's header, page 8 an intact leaf cut from
    /// the tree (see create_carving_orphan_leaf.py). Carving both explains every non-zero byte: the
    /// 156 records, the trunk's header and leaf list, and the old leaf's surviving cell pointers.
    /// </summary>
    [Fact]
    public void GetPageResiduals_WithOrphanCarve_ExplainsTheOrphanLeafFixtureCompletely()
    {
        var residuals = SqliteRecoveryFacade.GetPageResiduals(
            Path.Combine(CarvingDir, "carving_orphan_leaf.db"), CarveMode.Loose);

        var orphanPages = residuals.Where(r => r.Area == "page").ToList();
        Assert.Equal([7u, 8u], orphanPages.Select(r => r.PageNumber));
        Assert.Equal("FreelistTrunk", orphanPages[0].Type);
        Assert.Equal(156, orphanPages.Sum(r => r.RecoveredRecords));
        Assert.All(orphanPages, r => Assert.Equal("moz_places", r.TableName));
        Assert.All(residuals, r => Assert.Equal(0, r.UnexplainedNonZeroBytes));
    }

    /// <summary>
    /// Page 6 of the ambiguous-tables fixture holds row 352 with its first bytes overwritten, which
    /// the orphan carver can't read: those bytes are what's left unexplained.
    /// </summary>
    [Fact]
    public void GetPageResiduals_LeavesARecordTheCarverCannotRead_Unexplained()
    {
        var residuals = SqliteRecoveryFacade.GetPageResiduals(
            Path.Combine(CarvingDir, "carving_ambiguous_tables.db"), CarveMode.Tight);

        var page6 = Assert.Single(residuals, r => r.PageNumber == 6);
        Assert.Equal("page", page6.Area);
        Assert.Equal(23, page6.UnexplainedNonZeroBytes);
        Assert.Equal(23, residuals.Sum(r => r.UnexplainedNonZeroBytes));
    }

    [Fact]
    public void Recover_WritesUsableDatabaseAndSummary()
    {
        string inputPath = Path.Combine(CarvingDir, "carving_orphan_leaf.db");
        string outputPath = Path.Combine(Path.GetTempPath(), $"shard_facade_test_{Guid.NewGuid():N}.db");
        try
        {
            var result = SqliteRecoveryFacade.Recover(inputPath, outputPath,
                new RecoveryOptions(Flags: new RecoveryFlags(ProcessWal: false), CarveMode: CarveMode.Loose));

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
