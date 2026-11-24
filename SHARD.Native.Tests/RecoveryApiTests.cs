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
    public void GetHashes_ReturnsDigestsTakenAtOpen()
    {
        long handle = OpenHandle(OrphanLeafDb);
        var doc = JsonDocument.Parse(RecoveryApi.GetHashes(handle));
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());

        var db = doc.RootElement.GetProperty("data").EnumerateArray()
            .Single(e => e.GetProperty("role").GetString() == "database");
        string expected = Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(OrphanLeafDb)));
        Assert.Equal(expected, db.GetProperty("sha256").GetString());
        Assert.Equal(32, db.GetProperty("md5").GetString()!.Length);
        Assert.Equal(40, db.GetProperty("sha1").GetString()!.Length);
    }

    [Fact]
    public void VerifyHashes_UnchangedFile_ReportsMatchAsString()
    {
        long handle = OpenHandle(OrphanLeafDb);
        var doc = JsonDocument.Parse(RecoveryApi.VerifyHashes(handle));
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.All(doc.RootElement.GetProperty("data").EnumerateArray(),
            e => Assert.Equal("Match", e.GetProperty("status").GetString()));
    }

    [Fact]
    public void RecoverToFile_InferZeroedJournalHeaderOption_IsParsedAndOffByDefault()
    {
        // A PERSIST-mode database whose last transaction deleted a row: its journal header is zeroed
        string dir = Path.Combine(Path.GetTempPath(), $"shard_native_persist_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string db = Path.Combine(dir, "p.db");
        try
        {
            using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db};Pooling=False"))
            {
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = """
                    PRAGMA journal_mode=PERSIST; PRAGMA secure_delete=ON;
                    CREATE TABLE t (id INTEGER PRIMARY KEY, v TEXT);
                    INSERT INTO t VALUES (1, 'one'), (2, 'two'), (3, 'three');
                    DELETE FROM t WHERE id = 2;
                    """;
                cmd.ExecuteNonQuery();
            }

            long handle = OpenHandle(db);
            int Inserted(string? options) => JsonDocument.Parse(RecoveryApi.RecoverToFile(handle, Path.Combine(dir, $"{Guid.NewGuid():N}.db"), options))
                .RootElement.GetProperty("data").GetProperty("journalRecordsInserted").GetInt32();

            Assert.Equal(0, Inserted(null));
            Assert.Equal(0, Inserted("""{"processWal":true}"""));
            Assert.True(Inserted("""{"inferZeroedJournalHeader":true}""") > 0);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(dir, true);
        }
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
    public void GetPageResiduals_WithOrphanCarve_ReportsEachPage()
    {
        long handle = OpenHandle(OrphanLeafDb);
        var doc = JsonDocument.Parse(RecoveryApi.GetPageResiduals(handle, "loose", null));
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());

        var pages = doc.RootElement.GetProperty("data").EnumerateArray().ToList();
        var page8 = pages.Single(p => p.GetProperty("pageNumber").GetUInt32() == 8);
        Assert.Equal("page", page8.GetProperty("area").GetString());
        Assert.Equal(78, page8.GetProperty("recoveredRecords").GetInt32());
        Assert.Equal(0, page8.GetProperty("unexplainedNonZeroBytes").GetInt32());
        Assert.True(page8.GetProperty("nonZeroBytes").GetInt32() > 0);

        var withoutCarve = JsonDocument.Parse(RecoveryApi.GetPageResiduals(handle, null, null));
        Assert.All(withoutCarve.RootElement.GetProperty("data").EnumerateArray(),
            p => Assert.Equal("unallocated", p.GetProperty("area").GetString()));
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
