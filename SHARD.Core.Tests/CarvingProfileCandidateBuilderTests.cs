using SHARD.Core.Enums;
using SHARD.Core.Recovery;
using Xunit;

namespace SHARD.Core.Tests;

public class CarvingProfileCandidateBuilderTests
{
    private const string UsersSql = "CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT NOT NULL, score INTEGER)";

    private static CarvingProfileTableEntry UsersEntry(bool included = true, string? sql = UsersSql, params CarvingProfileColumnEntry[] columns)
    {
        var entry = new CarvingProfileTableEntry { TableName = "users", Included = included, CreateTableSql = sql };
        entry.Columns.AddRange(columns);
        return entry;
    }

    [Fact]
    public void ReconstructsColumnOrderAndRowIdAlias_FromCreateTableSqlAlone()
    {
        var profile = new CarvingProfile { Tables = { UsersEntry() } };

        var candidates = CarvingProfileCandidateBuilder.BuildCandidates(profile);

        var candidate = Assert.Single(candidates);
        Assert.Equal("users", candidate.Schema.TableName);
        Assert.Equal(3, candidate.Schema.Columns.Count);
        Assert.True(candidate.Schema.Columns[0].IsRowIdAlias);
        Assert.Equal("name", candidate.Schema.Columns[1].Name);
        Assert.True(candidate.Schema.Columns[1].IsNotNull);
    }

    [Fact]
    public void AppliesSavedAllowedKindsAndLengthRange_OntoTheRebuiltStructure()
    {
        var profile = new CarvingProfile
        {
            Tables =
            {
                UsersEntry(columns:
                [
                    new CarvingProfileColumnEntry { ColumnName = "score", MinLength = 0, MaxLength = 0, AllowedKinds = ["Int0", "Int1"] },
                ]),
            },
        };

        var candidates = CarvingProfileCandidateBuilder.BuildCandidates(profile);

        var candidate = Assert.Single(candidates);
        int scoreIndex = candidate.Schema.Columns.FindIndex(c => c.Name == "score");
        Assert.Equal([SerialTypeKind.Int0, SerialTypeKind.Int1], candidate.Structure.AllowedKindsPerColumn[scoreIndex]);
        Assert.Equal((0, 0), candidate.Structure.AllowedContentLengthRangePerColumn[scoreIndex]);
    }

    [Fact]
    public void ExcludedTable_IsSkippedEntirely()
    {
        var profile = new CarvingProfile { Tables = { UsersEntry(included: false) } };

        var candidates = CarvingProfileCandidateBuilder.BuildCandidates(profile);

        Assert.Empty(candidates);
    }

    [Fact]
    public void TableWithNoCreateTableSql_IsSkipped_SinceItCannotBeReconstructed()
    {
        var profile = new CarvingProfile { Tables = { UsersEntry(sql: null) } };

        var candidates = CarvingProfileCandidateBuilder.BuildCandidates(profile);

        Assert.Empty(candidates);
    }

    [Fact]
    public void UnrecognizedSavedKindName_IsSkippedRatherThanFailing()
    {
        var profile = new CarvingProfile
        {
            Tables =
            {
                UsersEntry(columns:
                [
                    new CarvingProfileColumnEntry { ColumnName = "score", AllowedKinds = ["Int0", "SomeFutureKindThisVersionDoesNotKnowAbout"] },
                ]),
            },
        };

        var candidates = CarvingProfileCandidateBuilder.BuildCandidates(profile);

        var candidate = Assert.Single(candidates);
        int scoreIndex = candidate.Schema.Columns.FindIndex(c => c.Name == "score");
        Assert.Equal([SerialTypeKind.Int0], candidate.Structure.AllowedKindsPerColumn[scoreIndex]);
    }
}
