using SHARD.Core.Enums;
using SHARD.Core.Recovery;
using SHARD.Core.Records;
using SHARD.Core.Schema;
using Xunit;

namespace SHARD.Core.Tests;

public class CarvingProfileMatcherTests
{
    private static TableSchema UsersSchema(params ColumnDefinition[] extraColumns)
    {
        var schema = new TableSchema
        {
            TableName = "users",
            Columns =
            {
                new ColumnDefinition { Name = "id", Affinity = TypeAffinity.Integer, IsRowIdAlias = true },
                new ColumnDefinition { Name = "name", Affinity = TypeAffinity.Text },
                new ColumnDefinition { Name = "surname", Affinity = TypeAffinity.Text },
            },
        };
        foreach (var col in extraColumns) schema.Columns.Add(col);
        return schema;
    }

    private static (TableSchema Schema, RecordStructure Structure) Candidate(TableSchema schema) =>
        (schema, RecordStructure.FromSchema(schema));

    private static CarvingProfile ProfileWith(params CarvingProfileTableEntry[] tables) =>
        new() { Tables = tables.ToList() };

    private static CarvingProfileTableEntry UsersProfileEntry(bool included = true, params (string Name, int Min, int Max)[] columns)
    {
        var entry = new CarvingProfileTableEntry { TableName = "users", Included = included };
        foreach (var (name, min, max) in columns)
            entry.Columns.Add(new CarvingProfileColumnEntry { ColumnName = name, MinLength = min, MaxLength = max });
        return entry;
    }

    [Fact]
    public void Match_ExactMatch_AppliesColumnRangesAndInclusion()
    {
        var candidates = new[] { Candidate(UsersSchema()) };
        var profile = ProfileWith(UsersProfileEntry(included: true, ("name", 2, 20), ("surname", 3, 30)));

        var result = CarvingProfileMatcher.Match(profile, candidates);

        var match = Assert.Single(result.Matches);
        Assert.Equal("users", match.TableName);
        Assert.True(match.Included);
        Assert.Equal(new CarvingProfileMatcher.ColumnRange(2, 20), match.Columns["name"].Range);
        Assert.Equal(new CarvingProfileMatcher.ColumnRange(3, 30), match.Columns["surname"].Range);
        Assert.Empty(match.ColumnsIgnored);
        Assert.Empty(result.NewTablesNotInProfile);
        Assert.Empty(result.TablesMissingFromDatabase);
    }

    [Fact]
    public void Match_TableNotInProfile_IsReportedAsNew()
    {
        var candidates = new[] { Candidate(UsersSchema()) };
        var profile = ProfileWith(); // empty — never saw "users"

        var result = CarvingProfileMatcher.Match(profile, candidates);

        Assert.Empty(result.Matches);
        Assert.Equal(["users"], result.NewTablesNotInProfile);
        Assert.Empty(result.TablesMissingFromDatabase);
    }

    [Fact]
    public void Match_ProfileTableNotInDatabase_IsReportedAsMissing()
    {
        var candidates = Array.Empty<(TableSchema, RecordStructure)>();
        var profile = ProfileWith(UsersProfileEntry());

        var result = CarvingProfileMatcher.Match(profile, candidates);

        Assert.Empty(result.Matches);
        Assert.Empty(result.NewTablesNotInProfile);
        Assert.Equal(["users"], result.TablesMissingFromDatabase);
    }

    [Fact]
    public void Match_ExcludedTableStillPresent_IsDistinguishableFromMissing()
    {
        var candidates = new[] { Candidate(UsersSchema()) };
        var profile = ProfileWith(UsersProfileEntry(included: false, ("name", 2, 20)));

        var result = CarvingProfileMatcher.Match(profile, candidates);

        var match = Assert.Single(result.Matches);
        Assert.False(match.Included);
        Assert.Empty(result.TablesMissingFromDatabase); // present, just excluded — not "missing"
        Assert.Empty(result.NewTablesNotInProfile);
    }

    [Fact]
    public void Match_ColumnAddedSinceProfileWasExported_HasNoRangeEntry()
    {
        // "email" exists in the current schema but was never in the profile — the caller is
        // expected to leave it at whatever default it already computed, so it simply has no key.
        var candidates = new[] { Candidate(UsersSchema(new ColumnDefinition { Name = "email", Affinity = TypeAffinity.Text })) };
        var profile = ProfileWith(UsersProfileEntry(included: true, ("name", 2, 20)));

        var result = CarvingProfileMatcher.Match(profile, candidates);

        var match = Assert.Single(result.Matches);
        Assert.True(match.Columns.ContainsKey("name"));
        Assert.False(match.Columns.ContainsKey("email"));
        Assert.Empty(match.ColumnsIgnored);
    }

    [Fact]
    public void Match_ColumnRemovedOrRenamedSinceProfileWasExported_IsIgnored()
    {
        var candidates = new[] { Candidate(UsersSchema()) };
        var profile = ProfileWith(UsersProfileEntry(included: true, ("name", 2, 20), ("nickname", 1, 10)));

        var result = CarvingProfileMatcher.Match(profile, candidates);

        var match = Assert.Single(result.Matches);
        Assert.True(match.Columns.ContainsKey("name"));
        Assert.Equal(["nickname"], match.ColumnsIgnored);
    }

    [Fact]
    public void Match_TableAndColumnNamesAreCaseInsensitive()
    {
        var candidates = new[] { Candidate(UsersSchema()) };
        var profile = ProfileWith(new CarvingProfileTableEntry
        {
            TableName = "USERS",
            Included  = true,
            Columns   = { new CarvingProfileColumnEntry { ColumnName = "NAME", MinLength = 2, MaxLength = 20 } },
        });

        var result = CarvingProfileMatcher.Match(profile, candidates);

        var match = Assert.Single(result.Matches);
        Assert.True(match.Columns.ContainsKey("name"));
    }

    [Fact]
    public void Match_PreservesAllowedKinds_ForAColumnNarrowedToInt0Int1Only()
    {
        var candidates = new[] { Candidate(UsersSchema()) };
        var entry = new CarvingProfileTableEntry { TableName = "users", Included = true };
        entry.Columns.Add(new CarvingProfileColumnEntry
        {
            ColumnName   = "name",
            MinLength    = 0,
            MaxLength    = 0,
            AllowedKinds = ["Int0", "Int1"],
        });
        var profile = ProfileWith(entry);

        var result = CarvingProfileMatcher.Match(profile, candidates);

        var match = Assert.Single(result.Matches);
        Assert.Equal([SerialTypeKind.Int0, SerialTypeKind.Int1], match.Columns["name"].AllowedKinds);
    }

    [Fact]
    public void Match_ColumnWithNoSavedKinds_HasEmptyAllowedKindsList()
    {
        var candidates = new[] { Candidate(UsersSchema()) };
        var profile = ProfileWith(UsersProfileEntry(included: true, ("name", 2, 20)));

        var result = CarvingProfileMatcher.Match(profile, candidates);

        var match = Assert.Single(result.Matches);
        Assert.Empty(match.Columns["name"].AllowedKinds);
    }

    [Fact]
    public void Match_UnrecognizedKindName_IsSkippedRatherThanFailing()
    {
        var candidates = new[] { Candidate(UsersSchema()) };
        var entry = new CarvingProfileTableEntry { TableName = "users", Included = true };
        entry.Columns.Add(new CarvingProfileColumnEntry
        {
            ColumnName   = "name",
            AllowedKinds = ["Int0", "SomeFutureKindThisVersionDoesNotKnowAbout"],
        });
        var profile = ProfileWith(entry);

        var result = CarvingProfileMatcher.Match(profile, candidates);

        var match = Assert.Single(result.Matches);
        Assert.Equal([SerialTypeKind.Int0], match.Columns["name"].AllowedKinds);
    }
}
