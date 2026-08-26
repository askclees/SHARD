using System.Globalization;
using Microsoft.Data.Sqlite;
using SHARD.Core.Enums;
using SHARD.Core.Recovery;
using SHARD.Core.Records;
using SHARD.Core.Schema;
using Xunit;

namespace SHARD.Core.Tests;

/// <summary>
/// End-to-end proof that a <see cref="CarvingProfile"/> exported while a database was still
/// readable is enough, on its own, to recover every row after the database's own container is
/// completely destroyed — no <see cref="SqliteForensicDatabase"/>, no sqlite_master, nothing but
/// the profile and the raw file bytes. This is the scenario <see cref="CarvingProfileTableEntry.CreateTableSql"/>
/// was added for.
/// </summary>
public class CarvingProfileRawRecoveryTests
{
    private static readonly string FixturePath = Path.Combine(
        AppContext.BaseDirectory, "TestData", "SHARDCreated", "Carving", "carving_raw_profile_recovery.db");

    [Fact]
    public void AllRowsAreRecovered_ViaProfileOnlyRawCarving_AfterFirstPageIsDestroyed()
    {
        Assert.True(File.Exists(FixturePath), $"Fixture not found: {FixturePath}");

        string tempPath = CopyToTemp(FixturePath);
        try
        {
            var expectedByTable = CaptureExpectedRows(tempPath);
            Assert.True(expectedByTable.Count >= 10, "fixture should define at least 10 tables");
            Assert.True(expectedByTable.Values.All(rows => rows.Count > 0), "every table should have rows");

            CarvingProfile profile;
            int pageSize;
            using (var db = SqliteForensicDatabase.Open(tempPath))
            {
                pageSize = db.Header.PageSize;
                var candidates = OrphanPageCarver.BuildCandidates(db, CarveMode.Tight);
                profile = BuildProfile(candidates, db.Header.TextEncoding);
            }

            // Round-trip through the real serialization path, exactly as a saved-to-disk profile would be.
            profile = CarvingProfile.FromJson(profile.ToJson());

            DestroyFirstPage(tempPath, pageSize);

            // Confirm the destruction is real: the normal container-based recovery path is gone entirely —
            // including the header, which is the only place TextEncoding otherwise lives.
            Assert.Throws<InvalidDataException>(() => SqliteForensicDatabase.Open(tempPath));

            var reconstructed = CarvingProfileCandidateBuilder.BuildCandidates(profile);
            Assert.Equal(expectedByTable.Count, reconstructed.Count);

            var candidateTuples = reconstructed
                .Select(c => (c.Schema.TableName, c.Structure))
                .ToList();
            var schemaByName = reconstructed
                .ToDictionary(c => c.Schema.TableName, c => c.Schema, StringComparer.OrdinalIgnoreCase);

            byte[] fileBytes = File.ReadAllBytes(tempPath);
            var matches = DeletedRecordParser.CarveRawBytesAnySchema(
                fileBytes, profile.ResolveTextEncoding(), candidateTuples, out int ambiguousSkipped);

            var actualByTable = matches
                .GroupBy(m => m.TableName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(m => CellToFieldValues(schemaByName[g.Key], m.Cell)).ToList(),
                    StringComparer.OrdinalIgnoreCase);

            foreach (var (tableName, expectedRows) in expectedByTable)
            {
                Assert.True(actualByTable.TryGetValue(tableName, out var actualRows),
                    $"table '{tableName}': no rows recovered at all (ambiguousSkipped={ambiguousSkipped})");

                // Grouped by id rather than a strict 1:1 dictionary: a live b-tree page split can
                // leave a stale duplicate of a cell behind in a page's unallocated space (the same
                // forensic residue this whole feature exists to carve) — benign here, so any
                // recovered cell with the right id and matching fields satisfies "this row survived".
                var actualById = actualRows!.ToLookup(r => Convert.ToInt64(r["id"], CultureInfo.InvariantCulture));

                foreach (var expectedRow in expectedRows)
                {
                    long id = Convert.ToInt64(expectedRow["id"], CultureInfo.InvariantCulture);
                    var candidates = actualById[id].ToList();
                    Assert.True(candidates.Count > 0, $"table '{tableName}' id={id}: expected row not recovered");

                    bool anyFullMatch = candidates.Any(actualRow => expectedRow.All(kv =>
                        actualRow.TryGetValue(kv.Key, out var actualValue) && ValuesEqual(kv.Value, actualValue)));

                    Assert.True(anyFullMatch,
                        $"table '{tableName}' id={id}: no recovered cell matched all expected field values");
                }
            }
        }
        finally
        {
            try { File.Delete(tempPath); } catch { }
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static string CopyToTemp(string sourcePath)
    {
        string tempBase = Path.GetTempFileName();
        string tempPath = Path.ChangeExtension(tempBase, ".db");
        File.Move(tempBase, tempPath);
        File.Copy(sourcePath, tempPath, overwrite: true);
        return tempPath;
    }

    private static Dictionary<string, List<Dictionary<string, object?>>> CaptureExpectedRows(string dbPath)
    {
        var result = new Dictionary<string, List<Dictionary<string, object?>>>(StringComparer.OrdinalIgnoreCase);
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();

        var tableNames = new List<string>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'";
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) tableNames.Add(reader.GetString(0));
        }

        foreach (var table in tableNames)
        {
            var rows = new List<Dictionary<string, object?>>();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT * FROM \"{table}\"";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < reader.FieldCount; i++)
                    row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                rows.Add(row);
            }
            result[table] = rows;
        }
        return result;
    }

    private static CarvingProfile BuildProfile(
        IReadOnlyList<(TableSchema Schema, RecordStructure Structure)> candidates, TextEncoding textEncoding)
    {
        var profile = new CarvingProfile { TextEncoding = textEncoding.ToString() };
        foreach (var (schema, structure) in candidates)
        {
            var entry = new CarvingProfileTableEntry
            {
                TableName = schema.TableName,
                Included = true,
                CreateTableSql = schema.Sql,
            };

            for (int i = 0; i < schema.Columns.Count; i++)
            {
                var col = schema.Columns[i];
                if (col.IsRowIdAlias) continue;

                var range = structure.AllowedContentLengthRangePerColumn[i] ?? (Min: 0, Max: 0);
                entry.Columns.Add(new CarvingProfileColumnEntry
                {
                    ColumnName   = col.Name,
                    MinLength    = range.Min,
                    MaxLength    = range.Max,
                    AllowedKinds = structure.AllowedKindsPerColumn[i].Select(k => k.ToString()).ToList(),
                });
            }

            profile.Tables.Add(entry);
        }
        return profile;
    }

    private static void DestroyFirstPage(string dbPath, int pageSize)
    {
        using var stream = new FileStream(dbPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        stream.Write(new byte[pageSize], 0, pageSize);
    }

    private static Dictionary<string, object?> CellToFieldValues(TableSchema schema, BTreeLeafCell cell)
    {
        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < schema.Columns.Count; i++)
        {
            var col = schema.Columns[i];
            dict[col.Name] = col.IsRowIdAlias ? cell.RowId.Value : cell.FieldValues.ElementAtOrDefault(i)?.Value;
        }
        return dict;
    }

    private static bool ValuesEqual(object? expected, object? actual)
    {
        if (expected is null) return actual is null;
        if (actual is null) return false;

        if (expected is long or int)
            return Convert.ToInt64(expected, CultureInfo.InvariantCulture) == Convert.ToInt64(actual, CultureInfo.InvariantCulture);
        if (expected is double expectedDouble)
            return Math.Abs(expectedDouble - Convert.ToDouble(actual, CultureInfo.InvariantCulture)) < 1e-9;
        if (expected is byte[] expectedBytes)
            return actual is byte[] actualBytes && expectedBytes.AsSpan().SequenceEqual(actualBytes);

        return Equals(expected, actual);
    }

    private static string Stringify(object? value) => value switch
    {
        null => "NULL",
        byte[] b => Convert.ToHexString(b),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "NULL",
    };
}
