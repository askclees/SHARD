using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Xunit.Abstractions;
using SHARD.Core.Enums;
using SHARD.Core.Pages;
using SHARD.Core.Records;
using SHARD.Core.Schema;

namespace SHARD.Core.Tests;

/// <summary>
/// Tests against the SQLite Forensic Corpus v2.0.
/// Set the SQLITE_CORPUS_PATH environment variable to the extracted corpus root,
/// or extract to /tmp/sqlite_corpus/sqlite_forensic_corpus_v2.0.
/// Tests are skipped automatically when the corpus is not found.
/// </summary>
public class CorpusTests(ITestOutputHelper output)
{
    // Set SHARD_SAVE_RESULTS to a directory path (or ".") to write each test's
    // output to "Test {section}_{file}.txt" in that directory.
    private static readonly string? SaveResultsDir =
        Environment.GetEnvironmentVariable("SHARD_SAVE_RESULTS");

    private static readonly string CorpusRoot =
        Environment.GetEnvironmentVariable("SQLITE_CORPUS_PATH")
        ?? Path.Combine(AppContext.BaseDirectory, "TestData", "Corpus");

    private static readonly bool CorpusAvailable = Directory.Exists(CorpusRoot);

    // Standard corpus sections only (skip anti-forensic 11+ for now)
    private static readonly string[] StandardSections =
        { "01", "02", "03", "04", "05", "06", "07", "08", "09", "0A", "0B", "0C", "0D", "0E" };

    // ── Test data sources ─────────────────────────────────────────────────────

    public static IEnumerable<object[]> AllCorpusDatabases()
    {
        if (!CorpusAvailable) yield break;
        foreach (var section in StandardSections)
        {
            var dir = Path.Combine(CorpusRoot, section);
            if (!Directory.Exists(dir)) continue;
            foreach (var db in Directory.GetFiles(dir, "*.db").OrderBy(f => f))
            {
                var xml = Path.ChangeExtension(db, ".xml");
                if (!File.Exists(xml)) continue;
                var tables = TryParseXml(xml);
                if (tables is null) continue; // malformed XML — skip
                yield return new object[] { section, Path.GetFileName(db), db, xml };
            }
        }
    }

    public static IEnumerable<object[]> DatabasesWithDeletedRecords()
    {
        if (!CorpusAvailable) yield break;
        foreach (var entry in AllCorpusDatabases())
        {
            string xmlPath = (string)entry[3];
            var tables = TryParseXml(xmlPath);
            if (tables is null) continue;
            if (tables.Any(t => !t.IsDeleted && t.RowsDeleted > 0))
                yield return entry;
        }
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Theory, MemberData(nameof(AllCorpusDatabases))]
    [Trait("Category", "Corpus")]
    public void LiveRecords_MatchExpected(string section, string file, string dbPath, string xmlPath)
    {
        var tables = TryParseXml(xmlPath) ?? [];
        using var db = SqliteForensicDatabase.Open(dbPath);
        var masterRows = db.ReadSqliteMaster()
            .Where(r => r.ObjectType == SqliteMasterObjectType.Table && r.RootPage is not null)
            .ToDictionary(r => r.Name ?? "", r => r);

        foreach (var expected in tables)
        {
            if (expected.IsDeleted) continue;
            if (!masterRows.TryGetValue(expected.Name, out var master)) continue;
            if (master.RootPage == 0u) continue; // virtual table (e.g. rtree, fts) — no B-tree

            int liveCount;
            try
            {
                liveCount = db.ReadTableRows(master.RootPage!.Value).Count();
            }
            catch (Exception ex)
            {
                Assert.Fail(
                    $"{section}/{file} table '{expected.Name}': " +
                    $"SHARD threw {ex.GetType().Name} reading {expected.RowsAlive} expected rows — {ex.Message}");
                return;
            }

            Assert.True(liveCount == expected.RowsAlive,
                $"{section}/{file} table '{expected.Name}': " +
                $"expected {expected.RowsAlive} live rows, SHARD found {liveCount}");
        }
    }

    [Theory, MemberData(nameof(DatabasesWithDeletedRecords))]
    [Trait("Category", "Corpus")]
    public void DeletedRecords_MatchExpected(string section, string file, string dbPath, string xmlPath)
    {
        var tables = TryParseXml(xmlPath) ?? [];
        using var db = SqliteForensicDatabase.Open(dbPath);
        var masterRows = db.ReadSqliteMaster()
            .Where(r => r.ObjectType == SqliteMasterObjectType.Table && r.RootPage is not null)
            .ToDictionary(r => r.Name ?? "", r => r);

        foreach (var expected in tables)
        {
            if (expected.IsDeleted || expected.RowsDeleted == 0) continue;
            if (!masterRows.TryGetValue(expected.Name, out var master)) continue;
            if (master.RootPage is 0) continue; // virtual table — no B-tree

            int deletedFound;
            try
            {
                var schema = db.GetTableSchema(expected.Name);
                var recordStructure = schema is not null
                    ? RecordStructure.FromSchema(schema)
                    : null;

                deletedFound = db.GetTreePageNumbers(master.RootPage!.Value)
                    .Select(p => db.ReadPage(p))
                    .OfType<TableBTreeLeafPage>()
                    .Sum(p =>
                    {
                        if (recordStructure is not null)
                        {
                            p.CarveDeletedCells(recordStructure);
                            p.CarveFreeblockCells(recordStructure);
                        }
                        return p.DeletedCells.Count + p.CarvedCells.Count + p.FreeblockCells.Count;
                    });
            }
            catch (Exception ex)
            {
                Assert.Fail(
                    $"{section}/{file} table '{expected.Name}': " +
                    $"SHARD threw {ex.GetType().Name} reading {expected.RowsDeleted} expected deleted rows — {ex.Message}");
                return;
            }

            Assert.True(deletedFound == expected.RowsDeleted,
                $"{section}/{file} table '{expected.Name}': " +
                $"expected {expected.RowsDeleted} deleted rows, SHARD recovered {deletedFound}");
        }
    }

    /// <summary>
    /// Informational test: reports how many deleted records were recovered exactly,
    /// partially (primary key matches but some fields differ), or not at all.
    /// Never fails — all diagnostics go to the test output.
    /// </summary>
    [Theory, MemberData(nameof(DatabasesWithDeletedRecords))]
    [Trait("Category", "Corpus")]
    public void DeletedRecords_ValuesMatchExpected(string section, string file, string dbPath, string xmlPath)
    {
        StreamWriter? fileOut = null;
        if (SaveResultsDir is not null)
        {
            Directory.CreateDirectory(SaveResultsDir);
            string fileName = $"Test {section}_{file}.txt";
            fileOut = new StreamWriter(Path.Combine(SaveResultsDir, fileName), append: false);
        }

        void Write(string line) { output.WriteLine(line); fileOut?.WriteLine(line); }

        try
        {

        var tables = TryParseXml(xmlPath) ?? [];
        using var db = SqliteForensicDatabase.Open(dbPath);
        var masterRows = db.ReadSqliteMaster()
            .Where(r => r.ObjectType == SqliteMasterObjectType.Table && r.RootPage is not null)
            .ToDictionary(r => r.Name ?? "", r => r);

        Write($"=== {section}/{file} ===");

        foreach (var expected in tables)
        {
            if (expected.IsDeleted || expected.RowsDeleted == 0 || expected.DeletedRows.Count == 0) continue;
            if (!masterRows.TryGetValue(expected.Name, out var master)) continue;
            if (master.RootPage is 0) continue;

            var schema = db.GetTableSchema(expected.Name);
            if (schema is null) continue;

            string? pkColName = FindPrimaryKeyColumn(schema);
            if (pkColName is null)
            {
                Write($"  [{expected.Name}] no primary key column found — skipped");
                continue;
            }

            var recordStructure = RecordStructure.FromSchema(schema);
            var colMap = BuildColumnFieldMap(schema);

            List<BTreeLeafCell> recovered;
            try
            {
                recovered = db.GetTreePageNumbers(master.RootPage!.Value)
                    .Select(p => db.ReadPage(p))
                    .OfType<TableBTreeLeafPage>()
                    .SelectMany(p =>
                    {
                        p.CarveDeletedCells(recordStructure);
                        p.CarveFreeblockCells(recordStructure);
                        return p.DeletedCells.Concat(p.CarvedCells).Concat(p.FreeblockCells);
                    })
                    .ToList();
            }
            catch (Exception ex)
            {
                Write($"  [{expected.Name}] EXCEPTION {ex.GetType().Name}: {ex.Message}");
                continue;
            }

            int exactCount = 0, partialCount = 0, missingCount = 0;
            var details = new List<string>();

            foreach (var expectedRow in expected.DeletedRows)
            {
                string expectedPk = expectedRow.Fields.TryGetValue(pkColName, out string? pv) ? pv : "?";

                // Find the best-matching recovered cell for this expected row (fewest field mismatches)
                (MatchLevel Level, List<FieldMismatch> Mismatches)? best = null;
                foreach (var cell in recovered)
                {
                    var match = ClassifyRowMatch(cell, expectedRow, pkColName, colMap, schema);
                    if (match is null) continue;
                    if (best is null || match.Value.Mismatches.Count < best.Value.Mismatches.Count)
                        best = match;
                    if (best.Value.Level == MatchLevel.Exact) break;
                }

                if (best is null)
                {
                    missingCount++;
                    string rowSummary = string.Join("  ", expectedRow.Fields
                        .Where(kv => kv.Key != pkColName)
                        .Select(kv => $"{kv.Key}={Trunc(kv.Value, 20)}"));
                    details.Add($"    MISSING  {pkColName}={expectedPk}" +
                                (rowSummary.Length > 0 ? $"  [{rowSummary}]" : ""));
                }
                else if (best.Value.Level == MatchLevel.Exact)
                {
                    exactCount++;
                }
                else
                {
                    partialCount++;
                    string mismatchSummary = string.Join("  ", best.Value.Mismatches.Select(m =>
                        $"{m.Col}: expected={Trunc(m.Expected, 25)} actual={Trunc(m.Actual, 25)}"));
                    details.Add($"    PARTIAL  {pkColName}={expectedPk}  {mismatchSummary}");
                }
            }

            Write($"  [{expected.Name}]  exact={exactCount}  partial={partialCount}  missing={missingCount}  (of {expected.DeletedRows.Count})");
            foreach (var d in details) Write(d);
        }

        } finally { fileOut?.Dispose(); }
    }

    /// <summary>
    /// Generates a markdown recovery performance report and writes it to the GitHub
    /// Actions step summary (<c>$GITHUB_STEP_SUMMARY</c>) when running in CI.
    /// Always passes — corpus results are informational, not a build gate.
    /// </summary>
    [Fact, Trait("Category", "Corpus")]
    public void GenerateCorpusReport()
    {
        if (!CorpusAvailable)
        {
            output.WriteLine("Corpus not available — skipping report generation.");
            return;
        }

        var allResults = new List<TestTableResult>();

        foreach (var section in StandardSections)
        {
            var dir = Path.Combine(CorpusRoot, section);
            if (!Directory.Exists(dir)) continue;

            foreach (var dbPath in Directory.GetFiles(dir, "*.db").OrderBy(f => f))
            {
                var xmlPath = Path.ChangeExtension(dbPath, ".xml");
                if (!File.Exists(xmlPath)) continue;
                var tables = TryParseXml(xmlPath);
                if (tables is null) continue;

                string testId = Path.GetFileNameWithoutExtension(dbPath);

                using var db = SqliteForensicDatabase.Open(dbPath);
                var masterRows = db.ReadSqliteMaster()
                    .Where(r => r.ObjectType == SqliteMasterObjectType.Table && r.RootPage is not null)
                    .ToDictionary(r => r.Name ?? "", r => r);

                foreach (var expected in tables)
                {
                    if (expected.IsDeleted || expected.RowsDeleted == 0 || expected.DeletedRows.Count == 0) continue;
                    if (!masterRows.TryGetValue(expected.Name, out var master)) continue;
                    if (master.RootPage is 0) continue;

                    var schema = db.GetTableSchema(expected.Name);
                    if (schema is null) continue;

                    string? pkColName = FindPrimaryKeyColumn(schema);
                    if (pkColName is null) continue;

                    var recordStructure = RecordStructure.FromSchema(schema);
                    var colMap = BuildColumnFieldMap(schema);
                    var colNames = schema.Columns.Select(c => c.Name).ToList();

                    List<BTreeLeafCell> recovered;
                    try
                    {
                        recovered = db.GetTreePageNumbers(master.RootPage!.Value)
                            .Select(p => db.ReadPage(p))
                            .OfType<TableBTreeLeafPage>()
                            .SelectMany(p =>
                            {
                                p.CarveDeletedCells(recordStructure);
                                p.CarveFreeblockCells(recordStructure);
                                return p.DeletedCells.Concat(p.CarvedCells).Concat(p.FreeblockCells);
                            })
                            .ToList();
                    }
                    catch
                    {
                        continue;
                    }

                    int exactCount = 0, partialCount = 0, missingCount = 0;
                    var missingRows = new List<IReadOnlyDictionary<string, string>>();
                    var changedRows = new List<(string Pk, List<FieldMismatch> Mismatches)>();

                    foreach (var expectedRow in expected.DeletedRows)
                    {
                        string expectedPk = expectedRow.Fields.TryGetValue(pkColName, out string? pv) ? pv : "?";

                        (MatchLevel Level, List<FieldMismatch> Mismatches)? best = null;
                        foreach (var cell in recovered)
                        {
                            var match = ClassifyRowMatch(cell, expectedRow, pkColName, colMap, schema);
                            if (match is null) continue;
                            if (best is null || match.Value.Mismatches.Count < best.Value.Mismatches.Count)
                                best = match;
                            if (best.Value.Level == MatchLevel.Exact) break;
                        }

                        if (best is null)
                        {
                            missingCount++;
                            missingRows.Add(expectedRow.Fields);
                        }
                        else if (best.Value.Level == MatchLevel.Exact)
                        {
                            exactCount++;
                        }
                        else
                        {
                            partialCount++;
                            changedRows.Add((expectedPk, best.Value.Mismatches));
                        }
                    }

                    allResults.Add(new TestTableResult(testId, expected.Name, pkColName, colNames,
                        expected.DeletedRows.Count, exactCount, partialCount, missingCount,
                        missingRows, changedRows));
                }
            }
        }

        string report = BuildMarkdownReport(allResults);
        output.WriteLine(report);

        string? summaryPath = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
        if (summaryPath is not null)
            File.AppendAllText(summaryPath, report);

        string? reportFilePath = Environment.GetEnvironmentVariable("CORPUS_REPORT_FILE");
        if (reportFilePath is not null)
            File.WriteAllText(reportFilePath, report);
    }

    // ── Report builder ────────────────────────────────────────────────────────

    private static string BuildMarkdownReport(List<TestTableResult> allResults)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# SHARD Corpus Recovery Report");
        sb.AppendLine();

        var byTest = allResults
            .GroupBy(r => r.TestId, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToList();

        // Per-test sections — only emit where something is missing or changed
        foreach (var testGroup in byTest)
        {
            if (!testGroup.Any(r => r.MissingCount > 0 || r.PartialCount > 0)) continue;

            sb.AppendLine($"## {testGroup.Key}");
            sb.AppendLine();
            sb.AppendLine("| Table | Expected | Recovered | Status |");
            sb.AppendLine("|---|---|---|---|");
            foreach (var r in testGroup)
            {
                int recoveredCount = r.ExactCount + r.PartialCount;
                string status = r.MissingCount == 0 && r.PartialCount == 0
                    ? "✅"
                    : BuildStatusText(r.MissingCount, r.PartialCount);
                sb.AppendLine($"| {r.TableName} | {r.Expected} | {recoveredCount} | {status} |");
            }
            sb.AppendLine();

            foreach (var r in testGroup.Where(r => r.MissingCount > 0 || r.PartialCount > 0))
            {
                if (r.MissingCount > 0)
                {
                    sb.AppendLine($"### {r.TableName} — Missing Records");
                    sb.AppendLine();
                    sb.AppendLine("| " + string.Join(" | ", r.ColNames) + " |");
                    sb.AppendLine("| " + string.Join(" | ", r.ColNames.Select(_ => "---")) + " |");
                    foreach (var row in r.MissingRows)
                    {
                        var cells = r.ColNames.Select(c => row.TryGetValue(c, out var v) ? EscapeMd(v) : "");
                        sb.AppendLine("| " + string.Join(" | ", cells) + " |");
                    }
                    sb.AppendLine();
                }

                if (r.PartialCount > 0)
                {
                    sb.AppendLine($"### {r.TableName} — Changed Records");
                    sb.AppendLine();
                    foreach (var (pk, mismatches) in r.ChangedRows)
                    {
                        sb.AppendLine($"**{r.PkColName} = {pk}**");
                        sb.AppendLine();
                        sb.AppendLine("| Column | Expected | Actual |");
                        sb.AppendLine("|---|---|---|");
                        foreach (var m in mismatches)
                            sb.AppendLine($"| {m.Col} | {EscapeMd(m.Expected)} | {EscapeMd(m.Actual)} |");
                        sb.AppendLine();
                    }
                }
            }

            sb.AppendLine("---");
            sb.AppendLine();
        }

        // Summary
        sb.AppendLine("## Summary");
        sb.AppendLine();

        var perfectTests  = byTest.Where(g => g.All(r => r.MissingCount == 0 && r.PartialCount == 0)).ToList();
        var imperfectTests = byTest.Where(g => g.Any(r => r.MissingCount > 0 || r.PartialCount > 0)).ToList();

        if (perfectTests.Count > 0)
        {
            sb.AppendLine($"### ✅ Fully Recovered ({perfectTests.Count} tests)");
            sb.AppendLine();
            sb.AppendLine(string.Join(", ", perfectTests.Select(g => g.Key)));
            sb.AppendLine();
        }

        if (imperfectTests.Count > 0)
        {
            sb.AppendLine($"### ⚠️ Partial Recovery ({imperfectTests.Count} tests)");
            sb.AppendLine();
            sb.AppendLine("| Test | Table | Expected | Recovered | Missing | Changed |");
            sb.AppendLine("|---|---|---|---|---|---|");
            foreach (var testGroup in imperfectTests)
            {
                foreach (var r in testGroup.Where(r => r.MissingCount > 0 || r.PartialCount > 0))
                    sb.AppendLine($"| {testGroup.Key} | {r.TableName} | {r.Expected} | {r.ExactCount + r.PartialCount} | {r.MissingCount} | {r.PartialCount} |");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string BuildStatusText(int missing, int partial)
    {
        var parts = new List<string>();
        if (missing > 0) parts.Add($"{missing} missing");
        if (partial > 0) parts.Add($"{partial} changed");
        return "⚠️ " + string.Join(", ", parts);
    }

    private static string EscapeMd(string s) => s.Replace("|", "\\|");

    // ── XML parser ────────────────────────────────────────────────────────────

    private static List<CorpusTableExpectation>? TryParseXml(string xmlPath)
    {
        try
        {
            var settings = new XmlReaderSettings { CheckCharacters = false };
            using var reader = XmlReader.Create(xmlPath, settings);
            var doc = XDocument.Load(reader);
            var result = new List<CorpusTableExpectation>();

            foreach (var el in doc.Root?.Elements("element") ?? [])
            {
                var meta = el.Element("meta");
                if (!string.Equals(meta?.Element("type")?.Value, "table", StringComparison.OrdinalIgnoreCase))
                    continue;

                string name     = meta?.Element("name")?.Value ?? "";
                bool isDeleted  = string.Equals(meta?.Element("deleted")?.Value, "True", StringComparison.OrdinalIgnoreCase);
                int rowsAlive   = int.TryParse(meta?.Element("rowsAlive")?.Value,   out int a) ? a : 0;
                int rowsDeleted = int.TryParse(meta?.Element("rowsDeleted")?.Value, out int d) ? d : 0;

                var deletedRows = new List<CorpusRow>();
                foreach (var rowEl in el.Descendants("row"))
                {
                    if (rowEl.Attribute("deleted")?.Value != "1") continue;
                    var fields = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var col in rowEl.Elements("column"))
                    {
                        var colName = col.Element("name")?.Value;
                        var content = col.Element("content")?.Value;
                        if (colName is not null && content is not null)
                            fields[colName] = content;
                    }
                    deletedRows.Add(new CorpusRow(fields));
                }

                result.Add(new CorpusTableExpectation(name, isDeleted, rowsAlive, rowsDeleted, deletedRows));
            }

            return result;
        }
        catch (XmlException)
        {
            return null; // corpus XML file is malformed — skip
        }
    }

    // ── Matching helpers ──────────────────────────────────────────────────────

    private static string? FindPrimaryKeyColumn(TableSchema schema)
    {
        // Rowid alias (INTEGER PRIMARY KEY) is the most authoritative identity
        var rowidAlias = schema.Columns.FirstOrDefault(c => c.IsRowIdAlias);
        if (rowidAlias is not null) return rowidAlias.Name;
        // Explicit PRIMARY KEY constraint
        var pk = schema.Columns.FirstOrDefault(c => c.IsPrimaryKey);
        if (pk is not null) return pk.Name;
        // Fallback: first INTEGER-affinity column
        return schema.Columns.FirstOrDefault(c => c.Affinity == TypeAffinity.Integer)?.Name;
    }

    private static Dictionary<string, (bool IsRowId, int FieldIdx)> BuildColumnFieldMap(TableSchema schema)
    {
        var map = new Dictionary<string, (bool IsRowId, int FieldIdx)>(StringComparer.Ordinal);
        int fi = 0;
        foreach (var col in schema.Columns)
        {
            map[col.Name] = (col.IsRowIdAlias, col.IsRowIdAlias ? -1 : fi);
            if (!col.IsRowIdAlias) fi++;
        }
        return map;
    }

    private enum MatchLevel { Exact, Partial }

    private record struct FieldMismatch(string Col, string Expected, string Actual);

    /// <summary>
    /// Checks whether <paramref name="cell"/> matches <paramref name="expectedRow"/> by primary key.
    /// Returns null when the PK doesn't match (not a candidate).
    /// Otherwise returns the match level and a list of field mismatches (empty = exact).
    /// </summary>
    private static (MatchLevel Level, List<FieldMismatch> Mismatches)? ClassifyRowMatch(
        BTreeLeafCell cell,
        CorpusRow expectedRow,
        string pkColName,
        Dictionary<string, (bool IsRowId, int FieldIdx)> colMap,
        TableSchema schema)
    {
        if (!colMap.TryGetValue(pkColName, out var pkMapping)) return null;
        if (!expectedRow.Fields.TryGetValue(pkColName, out string? expectedPk)) return null;

        // Verify PK match
        if (pkMapping.IsRowId)
        {
            if (cell.RowId.Value == -1) return null;
            if (!long.TryParse(expectedPk, out long ev) || cell.RowId.Value != ev) return null;
        }
        else
        {
            int idx = pkMapping.FieldIdx;
            if (idx >= cell.FieldValues.Count) return null;
            var actual = cell.FieldValues[idx];
            if (actual is null) return null;
            var pkCol = schema.Columns.FirstOrDefault(c => c.Name == pkColName);
            if (pkCol is null || !StrictFieldValueMatches(actual, expectedPk, pkCol.Affinity)) return null;
        }

        // PK matched — compare all other fields strictly
        var mismatches = new List<FieldMismatch>();
        foreach (var (colName, expectedStr) in expectedRow.Fields)
        {
            if (colName == pkColName) continue;
            if (!colMap.TryGetValue(colName, out var mapping) || mapping.IsRowId) continue;

            int idx = mapping.FieldIdx;
            if (idx >= cell.FieldValues.Count) continue;
            var actual = cell.FieldValues[idx];
            if (actual is null) continue;

            var col = schema.Columns.FirstOrDefault(c => c.Name == colName);
            if (col is null) continue;

            if (!StrictFieldValueMatches(actual, expectedStr, col.Affinity))
            {
                string actualStr = actual.Value?.ToString() ?? "NULL";
                mismatches.Add(new FieldMismatch(colName, expectedStr, actualStr));
            }
        }

        return mismatches.Count == 0
            ? (MatchLevel.Exact, mismatches)
            : (MatchLevel.Partial, mismatches);
    }

    private static bool StrictFieldValueMatches(SqliteValue actual, string expectedStr, TypeAffinity affinity)
    {
        return affinity switch
        {
            TypeAffinity.Integer =>
                actual.StorageClass == SqliteStorageClass.Integer &&
                long.TryParse(expectedStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out long ei) &&
                actual.IntegerValue == ei,

            TypeAffinity.Real =>
                actual.StorageClass == SqliteStorageClass.Real &&
                double.TryParse(expectedStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double er) &&
                Math.Abs(actual.RealValue!.Value - er) <= Math.Abs(er) * 1e-9 + 1e-15,

            TypeAffinity.Text =>
                actual.StorageClass == SqliteStorageClass.Text &&
                actual.TextValue == expectedStr,

            _ => true // Blob and Numeric — no meaningful string comparison
        };
    }

    private static string Trunc(string s, int maxLen = 30)
        => s.Length <= maxLen ? s : s[..maxLen] + "…";

    // ── Data records ──────────────────────────────────────────────────────────

    private record CorpusTableExpectation(
        string Name,
        bool IsDeleted,
        int RowsAlive,
        int RowsDeleted,
        IReadOnlyList<CorpusRow> DeletedRows);

    private record CorpusRow(IReadOnlyDictionary<string, string> Fields);

    private record TestTableResult(
        string TestId,
        string TableName,
        string PkColName,
        IReadOnlyList<string> ColNames,
        int Expected,
        int ExactCount,
        int PartialCount,
        int MissingCount,
        IReadOnlyList<IReadOnlyDictionary<string, string>> MissingRows,
        IReadOnlyList<(string Pk, List<FieldMismatch> Mismatches)> ChangedRows);
}
