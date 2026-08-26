using SHARD.Core.Recovery;
using Xunit;

namespace SHARD.Core.Tests;

public class CarvingProfileTests
{
    [Fact]
    public void RoundTrip_PreservesAllFields()
    {
        var profile = new CarvingProfile
        {
            SourceDatabaseFileName = "places.sqlite",
            Tables =
            {
                new CarvingProfileTableEntry
                {
                    TableName = "moz_places",
                    Included  = true,
                    Columns =
                    {
                        new CarvingProfileColumnEntry { ColumnName = "url", MinLength = 5, MaxLength = 512, AllowedKinds = ["Text"] },
                        new CarvingProfileColumnEntry { ColumnName = "title", MinLength = 0, MaxLength = 256 },
                        new CarvingProfileColumnEntry { ColumnName = "is_flag", MinLength = 0, MaxLength = 0, AllowedKinds = ["Int0", "Int1"] },
                    },
                },
            },
        };

        var roundTripped = CarvingProfile.FromJson(profile.ToJson());

        Assert.Equal(profile.FormatVersion, roundTripped.FormatVersion);
        Assert.Equal(profile.SourceDatabaseFileName, roundTripped.SourceDatabaseFileName);
        Assert.Single(roundTripped.Tables);
        var table = roundTripped.Tables[0];
        Assert.Equal("moz_places", table.TableName);
        Assert.True(table.Included);
        Assert.Equal(3, table.Columns.Count);
        Assert.Equal("url", table.Columns[0].ColumnName);
        Assert.Equal(5, table.Columns[0].MinLength);
        Assert.Equal(512, table.Columns[0].MaxLength);
        Assert.Equal(["Text"], table.Columns[0].AllowedKinds);
        Assert.Equal(["Int0", "Int1"], table.Columns[2].AllowedKinds);
    }

    [Fact]
    public void RoundTrip_PreservesExcludedTableWithEmptyColumns()
    {
        // An excluded table with no narrowable columns must still round-trip as a distinct
        // entry (Included = false, empty Columns) — this is what lets a later load tell
        // "existed but excluded" apart from "never seen in the profile at all".
        var profile = new CarvingProfile
        {
            Tables = { new CarvingProfileTableEntry { TableName = "moz_excluded", Included = false } },
        };

        var roundTripped = CarvingProfile.FromJson(profile.ToJson());

        var table = Assert.Single(roundTripped.Tables);
        Assert.Equal("moz_excluded", table.TableName);
        Assert.False(table.Included);
        Assert.Empty(table.Columns);
    }

    [Fact]
    public void FromJson_ThrowsOnMalformedJson()
    {
        Assert.Throws<InvalidDataException>(() => CarvingProfile.FromJson("{ not valid json"));
    }

    [Fact]
    public void FromJson_ThrowsOnUnsupportedFutureFormatVersion()
    {
        string json = $$"""{"FormatVersion": {{CarvingProfile.CurrentFormatVersion + 1}}, "Tables": []}""";
        Assert.Throws<InvalidDataException>(() => CarvingProfile.FromJson(json));
    }

    [Fact]
    public void FromJson_AcceptsCurrentFormatVersion()
    {
        string json = $$"""{"FormatVersion": {{CarvingProfile.CurrentFormatVersion}}, "Tables": []}""";
        var profile = CarvingProfile.FromJson(json);
        Assert.Empty(profile.Tables);
    }
}
