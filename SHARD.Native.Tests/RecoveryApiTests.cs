using System.Text.Json;
using SHARD.Native;

namespace SHARD.Native.Tests;

/// <summary>
/// Exercises the JSON contract behind the native exports directly (no pointers, no AOT publish
/// needed — see RecoveryApi's doc comment). This is the real functional verification for the
/// SHARD.Native wire format; NativeExports itself is just marshalling on top of this.
/// </summary>
public class RecoveryApiTests
{
    private static readonly string CarvingDir =
        Path.Combine(AppContext.BaseDirectory, "TestData", "SHARDCreated", "Carving");
    private static readonly string OrphanLeafDb = Path.Combine(CarvingDir, "carving_orphan_leaf.db");
    private static readonly string AmbiguousDb = Path.Combine(CarvingDir, "carving_ambiguous_tables.db");

    private static long OpenHandle(string path)
    {
        var doc = JsonDocument.Parse(RecoveryApi.Open(path));
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        return doc.RootElement.GetProperty("data").GetProperty("handle").GetInt64();
    }

    [Fact]
    public void Open_ValidFile_ReturnsOkWithHandle()
    {
        var doc = JsonDocument.Parse(RecoveryApi.Open(OrphanLeafDb));
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("data").GetProperty("handle").GetInt64() > 0);
    }

    [Fact]
    public void Open_MissingFile_ReturnsError()
    {
        var doc = JsonDocument.Parse(RecoveryApi.Open("/tmp/definitely_does_not_exist_" + Guid.NewGuid() + ".db"));
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.False(string.IsNullOrEmpty(doc.RootElement.GetProperty("error").GetString()));
    }

    [Fact]
    public void UnknownHandle_ReturnsError()
    {
        var doc = JsonDocument.Parse(RecoveryApi.GetHeader(999_999_999));
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("handle", doc.RootElement.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Close_ThenSubsequentCall_ReturnsError()
    {
        long handle = OpenHandle(OrphanLeafDb);
        RecoveryApi.Close(handle);
        var doc = JsonDocument.Parse(RecoveryApi.GetHeader(handle));
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public void GetHeader_ReturnsPageSize()
    {
        long handle = OpenHandle(OrphanLeafDb);
        var doc = JsonDocument.Parse(RecoveryApi.GetHeader(handle));
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(4096, doc.RootElement.GetProperty("data").GetProperty("pageSize").GetInt32());
    }

    [Fact]
    public void GetSchema_ListsLiveTable()
    {
        long handle = OpenHandle(OrphanLeafDb);
        var doc = JsonDocument.Parse(RecoveryApi.GetSchema(handle));
        var names = doc.RootElement.GetProperty("data").EnumerateArray()
            .Select(e => e.GetProperty("name").GetString()).ToList();
        Assert.Contains("moz_places", names);
    }

    [Fact]
    public void GetRows_MatchesLiveCount()
    {
        long handle = OpenHandle(OrphanLeafDb);
        var doc = JsonDocument.Parse(RecoveryApi.GetRows(handle, "moz_places"));
        Assert.Equal(299, doc.RootElement.GetProperty("data").GetArrayLength());
    }

    [Fact]
    public void GetRows_FieldsRoundTripCorrectTypes()
    {
        long handle = OpenHandle(OrphanLeafDb);
        var doc = JsonDocument.Parse(RecoveryApi.GetRows(handle, "moz_places"));
        var first = doc.RootElement.GetProperty("data")[0];
        var fields = first.GetProperty("fields");
        // Field dictionary keys are raw SQL column names (from the evidence file's own schema),
        // not subject to the envelope's camelCase policy — moz_places declares visit_count.
        Assert.Equal(JsonValueKind.String, fields.GetProperty("url").ValueKind);
        Assert.Equal(JsonValueKind.Number, fields.GetProperty("visit_count").ValueKind);
    }

    [Fact]
    public void Carve_LooseMode_OrphanLeaf_FindsAllRows()
    {
        long handle = OpenHandle(OrphanLeafDb);
        var doc = JsonDocument.Parse(RecoveryApi.Carve(handle, "loose", null));
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(156, doc.RootElement.GetProperty("data").GetArrayLength());
    }

    [Fact]
    public void Carve_AmbiguousTables_LooseFindsNothing_TightDisambiguates_TableFilterWorks()
    {
        long handle = OpenHandle(AmbiguousDb);

        var loose = JsonDocument.Parse(RecoveryApi.Carve(handle, "loose", null));
        Assert.Equal(0, loose.RootElement.GetProperty("data").GetArrayLength());

        var tight = JsonDocument.Parse(RecoveryApi.Carve(handle, "tight", null));
        Assert.Equal(203, tight.RootElement.GetProperty("data").GetArrayLength());

        // Table filter: excluding table_a from candidates should leave the ambiguity permanently
        // unresolved (nothing left that could match), even in tight mode.
        var filtered = JsonDocument.Parse(RecoveryApi.Carve(handle, "tight", """["table_b"]"""));
        Assert.Equal(0, filtered.RootElement.GetProperty("data").GetArrayLength());
    }

    [Fact]
    public void RecoverToFile_WritesUsableDatabaseAndSummary()
    {
        long handle = OpenHandle(OrphanLeafDb);
        string outputPath = Path.Combine(Path.GetTempPath(), $"shard_native_test_{Guid.NewGuid():N}.db");
        try
        {
            var doc = JsonDocument.Parse(RecoveryApi.RecoverToFile(handle, outputPath, """{"processWal":false,"carveMode":"loose"}"""));
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());

            var data = doc.RootElement.GetProperty("data");
            Assert.Equal(156, data.GetProperty("carvedRecords").GetInt32());
            Assert.Equal(0, data.GetProperty("carveAmbiguousSkipped").GetInt32());
            Assert.True(File.Exists(outputPath));
        }
        finally
        {
            if (File.Exists(outputPath)) File.Delete(outputPath);
        }
    }
}
