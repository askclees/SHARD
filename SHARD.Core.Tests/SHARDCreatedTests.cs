using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;
using SHARD.Core.Enums;
using SHARD.Core.Recovery;
using SHARD.Core.Shadow;
using SHARD.Core.WAL;

namespace SHARD.Core.Tests;

/// <summary>
/// Tests against SHARD-created test databases in TestData/SHARDCreated/.
/// These databases exercise recovery paths that cannot be tested using the
/// third-party forensic corpus (e.g. WAL deleted-record recovery).
/// </summary>
public class SHARDCreatedTests(ITestOutputHelper output)
{
    private static readonly string TestDataRoot =
        Path.Combine(AppContext.BaseDirectory, "TestData", "SHARDCreated");

    // ── Test data sources ─────────────────────────────────────────────────────

    public static IEnumerable<object[]> AllWalDatabases()
    {
        var walDir = Path.Combine(TestDataRoot, "WAL");
        if (!Directory.Exists(walDir)) yield break;
        foreach (var dbPath in Directory.GetFiles(walDir, "*.db").OrderBy(f => f))
        {
            string walPath = dbPath + "-wal";
            string xmlPath = Path.ChangeExtension(dbPath, ".xml");
            if (!File.Exists(walPath) || !File.Exists(xmlPath)) continue;
            var tables = TryParseXml(xmlPath);
            if (tables is null) continue;
            yield return new object[] { Path.GetFileName(dbPath), dbPath, walPath, xmlPath };
        }
    }

    /// <summary>
    /// Orphan-page carving fixtures: plain (non-WAL) databases containing a page that's been
    /// unlinked from its table's tree but still holds its original bytes. Unlike
    /// <see cref="AllWalDatabases"/>, only a <c>.db</c> + matching <c>.xml</c> pair is required.
    /// </summary>
    public static IEnumerable<object[]> AllCarvingDatabases()
    {
        var carvingDir = Path.Combine(TestDataRoot, "Carving");
        if (!Directory.Exists(carvingDir)) yield break;
        foreach (var dbPath in Directory.GetFiles(carvingDir, "*.db").OrderBy(f => f))
        {
            string xmlPath = Path.ChangeExtension(dbPath, ".xml");
            if (!File.Exists(xmlPath)) continue;
            var tables = TryParseXml(xmlPath);
            if (tables is null) continue;
            yield return new object[] { Path.GetFileName(dbPath), dbPath, xmlPath };
        }
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Theory, MemberData(nameof(AllWalDatabases))]
#pragma warning disable xUnit1026 // walPath unused here; kept for uniform MemberData signature
    public void WalLiveRecords_MatchExpected(string file, string dbPath, string walPath, string xmlPath)
#pragma warning restore xUnit1026
    {
        _ = walPath;
        var tables = TryParseXml(xmlPath) ?? [];
        using var db = SqliteForensicDatabase.Open(dbPath);
        var masterRows = db.ReadSqliteMaster()
            .Where(r => r.ObjectType == SqliteMasterObjectType.Table && r.RootPage is not null)
            .ToDictionary(r => r.Name ?? "", r => r);

        foreach (var expected in tables)
        {
            if (expected.IsDeleted) continue;
            if (!masterRows.TryGetValue(expected.Name, out var master)) continue;
            if (master.RootPage == 0u) continue;

            int liveCount = db.ReadTableRows(master.RootPage!.Value).Count();
            Assert.True(liveCount == expected.RowsAlive,
                $"{file} table '{expected.Name}': expected {expected.RowsAlive} live rows (raw DB), SHARD found {liveCount}");
        }
    }

    [Theory, MemberData(nameof(AllWalDatabases))]
    public void WalDeletedRecords_CountMatchesExpected(string file, string dbPath, string walPath, string xmlPath)
    {
        var tables = TryParseXml(xmlPath) ?? [];

        using var db  = SqliteForensicDatabase.Open(dbPath);
        var wal = new WalFile(walPath, db.Header.TextEncoding, db.Header.ReservedBytesPerPage);

        using var shadow = CreateTempShadow(dbPath, db, wal);

        var masterRows = db.ReadSqliteMaster()
            .Where(r => r.ObjectType == SqliteMasterObjectType.Table && r.RootPage is not null)
            .ToDictionary(r => r.Name ?? "", r => r);

        using var conn = new SqliteConnection($"Data Source={shadow.Path};Mode=ReadOnly");
        conn.Open();

        foreach (var expected in tables)
        {
            if (expected.IsDeleted) continue;
            if (!masterRows.ContainsKey(expected.Name)) continue;

            string recoveredTable = $"\"{ShadowDatabaseBuilder.RecoveredTablePrefix}{expected.Name}\"";
            int count;
            try
            {
                count = QueryCount(conn, recoveredTable,
                    $"\"{ShadowDatabaseBuilder.RecoveryMethodColumn}\" IN " +
                    $"('{ShadowDatabaseBuilder.RecoveryMethodWalFrame}', " +
                    $"'{ShadowDatabaseBuilder.RecoveryMethodWalPreviousVersion}')");
            }
            catch
            {
                count = 0;
            }

            Assert.True(count == expected.RowsDeleted,
                $"{file} table '{expected.Name}': expected {expected.RowsDeleted} WAL-recovered records, found {count}");
        }
    }

    [Theory, MemberData(nameof(AllWalDatabases))]
    public void WalDeletedRecords_ValuesMatchExpected(string file, string dbPath, string walPath, string xmlPath)
    {
        var tables = TryParseXml(xmlPath) ?? [];
        if (tables.All(t => t.DeletedRows.Count == 0)) return;

        using var db  = SqliteForensicDatabase.Open(dbPath);
        var wal = new WalFile(walPath, db.Header.TextEncoding, db.Header.ReservedBytesPerPage);

        using var shadow = CreateTempShadow(dbPath, db, wal);

        var masterRows = db.ReadSqliteMaster()
            .Where(r => r.ObjectType == SqliteMasterObjectType.Table && r.RootPage is not null)
            .ToDictionary(r => r.Name ?? "", r => r);

        using var conn = new SqliteConnection($"Data Source={shadow.Path};Mode=ReadOnly");
        conn.Open();

        foreach (var expected in tables)
        {
            if (expected.IsDeleted || expected.DeletedRows.Count == 0) continue;
            if (!masterRows.TryGetValue(expected.Name, out var master)) continue;

            var schema = db.GetTableSchema(expected.Name);
            if (schema is null) continue;

            string? pkCol = schema.Columns.FirstOrDefault(c => c.IsRowIdAlias)?.Name
                         ?? schema.Columns.FirstOrDefault(c => c.IsPrimaryKey)?.Name;
            if (pkCol is null) continue;

            string recoveredTable = $"\"{ShadowDatabaseBuilder.RecoveredTablePrefix}{expected.Name}\"";
            var recovered = QueryRecoveredRows(conn, recoveredTable, schema.Columns.Select(c => c.Name).ToList());

            foreach (var expectedRow in expected.DeletedRows)
            {
                if (!expectedRow.Fields.TryGetValue(pkCol, out string? expectedPk)) continue;

                var actualRow = recovered.FirstOrDefault(r =>
                    r.TryGetValue(pkCol, out string? actualPk) && actualPk == expectedPk);

                if (actualRow is null)
                {
                    Assert.Fail(
                        $"{file} table '{expected.Name}': expected WAL-recovered row with " +
                        $"{pkCol}={expectedPk} but it was not found in the shadow DB");
                    return;
                }

                foreach (var (colName, expectedValue) in expectedRow.Fields)
                {
                    if (!actualRow.TryGetValue(colName, out string? actualValue)) continue;
                    Assert.True(actualValue == expectedValue,
                        $"{file} table '{expected.Name}' row {pkCol}={expectedPk}: " +
                        $"column '{colName}' expected '{expectedValue}', got '{actualValue}'");
                }
            }
        }
    }

    /// <summary>
    /// Verifies <see cref="OrphanPageCarver.Carve"/> against the Carving fixtures: for each
    /// expectation entry (one per table+mode combination), builds fresh candidates in that mode
    /// and asserts the carved count/values for that table match — including the loose-mode
    /// ambiguity-rejection case (expected count 0) and the tight-mode disambiguation case.
    /// </summary>
    [Theory, MemberData(nameof(AllCarvingDatabases))]
    public void CarvingRecords_MatchExpected(string file, string dbPath, string xmlPath)
    {
        var tables = TryParseXml(xmlPath) ?? [];
        using var db = SqliteForensicDatabase.Open(dbPath);

        foreach (var expected in tables)
        {
            if (expected.IsDeleted) continue;

            var mode = string.Equals(expected.Mode, "tight", StringComparison.OrdinalIgnoreCase)
                ? CarveMode.Tight : CarveMode.Loose;
            var candidates = OrphanPageCarver.BuildCandidates(db, mode);
            var carved = OrphanPageCarver.Carve(db, candidates, out _);

            var matches = carved
                .Where(c => string.Equals(c.Schema.TableName, expected.Name, StringComparison.OrdinalIgnoreCase))
                .ToList();

            Assert.True(matches.Count == expected.RowsDeleted,
                $"{file} table '{expected.Name}' mode={expected.Mode}: expected {expected.RowsDeleted} " +
                $"orphan-carved records, found {matches.Count}");

            if (expected.RowsDeleted == 0) continue;

            var schema = matches[0].Schema;
            string? pkCol = schema.Columns.FirstOrDefault(c => c.IsRowIdAlias)?.Name
                         ?? schema.Columns.FirstOrDefault(c => c.IsPrimaryKey)?.Name;
            if (pkCol is null) continue;

            var actualRows = matches.Select(m => CellToFieldStrings(m.Schema, m.Cell)).ToList();

            foreach (var expectedRow in expected.DeletedRows)
            {
                if (!expectedRow.Fields.TryGetValue(pkCol, out string? expectedPk)) continue;

                var actualRow = actualRows.FirstOrDefault(r =>
                    r.TryGetValue(pkCol, out string? actualPk) && actualPk == expectedPk);

                if (actualRow is null)
                {
                    Assert.Fail(
                        $"{file} table '{expected.Name}' mode={expected.Mode}: expected orphan-carved row " +
                        $"with {pkCol}={expectedPk} but it was not found");
                    return;
                }

                foreach (var (colName, expectedValue) in expectedRow.Fields)
                {
                    if (!actualRow.TryGetValue(colName, out string? actualValue)) continue;
                    Assert.True(actualValue == expectedValue,
                        $"{file} table '{expected.Name}' mode={expected.Mode} row {pkCol}={expectedPk}: " +
                        $"column '{colName}' expected '{expectedValue}', got '{actualValue}'");
                }
            }
        }
    }

    /// <summary>
    /// Generates a markdown orphan-carving section and appends it to the corpus report
    /// (<c>$CORPUS_REPORT_FILE</c>) and GitHub Actions step summary when running in CI.
    /// Always passes — results are informational.
    /// </summary>
    [Fact]
    public void GenerateCarvingReport()
    {
        var carvingDir = Path.Combine(TestDataRoot, "Carving");
        if (!Directory.Exists(carvingDir))
        {
            output.WriteLine("SHARD-created carving test data not found — skipping report.");
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("# SHARD-Created Test Data — Orphan-Page Carving Report");
        sb.AppendLine();

        bool anyData = false;

        foreach (var args in AllCarvingDatabases())
        {
            string file   = (string)args[0];
            string dbPath = (string)args[1];
            string xmlPath = (string)args[2];

            anyData = true;

            var tables = TryParseXml(xmlPath) ?? [];
            using var db = SqliteForensicDatabase.Open(dbPath);

            sb.AppendLine($"## {Path.GetFileNameWithoutExtension(file)}");
            sb.AppendLine();
            sb.AppendLine("| Table | Mode | Expected | Found | Status |");
            sb.AppendLine("|---|---|---|---|---|");

            foreach (var expected in tables)
            {
                if (expected.IsDeleted) continue;

                var mode = string.Equals(expected.Mode, "tight", StringComparison.OrdinalIgnoreCase)
                    ? CarveMode.Tight : CarveMode.Loose;

                int found = -1;
                try
                {
                    var candidates = OrphanPageCarver.BuildCandidates(db, mode);
                    var carved = OrphanPageCarver.Carve(db, candidates, out _);
                    found = carved.Count(c => string.Equals(c.Schema.TableName, expected.Name, StringComparison.OrdinalIgnoreCase));
                }
                catch { }

                string foundStr = found >= 0 ? found.ToString() : "error";
                string status = found == expected.RowsDeleted ? "✅" : "⚠️";

                sb.AppendLine($"| {expected.Name} | {expected.Mode} | {expected.RowsDeleted} | {foundStr} | {status} |");
            }

            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();
        }

        if (!anyData)
            sb.AppendLine("No SHARD-created carving test databases found.");

        string report = sb.ToString();
        output.WriteLine(report);

        string? summaryPath = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
        if (summaryPath is not null)
            File.AppendAllText(summaryPath, report);

        string? reportFilePath = Environment.GetEnvironmentVariable("CORPUS_REPORT_FILE");
        if (reportFilePath is not null)
            File.AppendAllText(reportFilePath, report);
    }

    /// <summary>
    /// Generates a markdown WAL recovery section and appends it to the corpus report
    /// (<c>$CORPUS_REPORT_FILE</c>) and GitHub Actions step summary when running in CI.
    /// Always passes — results are informational.
    /// </summary>
    [Fact]
    public void GenerateSHARDCreatedReport()
    {
        if (!Directory.Exists(TestDataRoot))
        {
            output.WriteLine("SHARD-created test data not found — skipping report.");
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("# SHARD-Created Test Data — WAL Recovery Report");
        sb.AppendLine();

        bool anyData = false;

        foreach (var args in AllWalDatabases())
        {
            string file    = (string)args[0];
            string dbPath  = (string)args[1];
            string walPath = (string)args[2];
            string xmlPath = (string)args[3];

            anyData = true;

            var tables = TryParseXml(xmlPath) ?? [];

            using var db  = SqliteForensicDatabase.Open(dbPath);
            var wal = new WalFile(walPath, db.Header.TextEncoding, db.Header.ReservedBytesPerPage);

            using var shadow = CreateTempShadow(dbPath, db, wal);

            var masterRows = db.ReadSqliteMaster()
                .Where(r => r.ObjectType == SqliteMasterObjectType.Table && r.RootPage is not null)
                .ToDictionary(r => r.Name ?? "", r => r);

            using var conn = new SqliteConnection($"Data Source={shadow.Path};Mode=ReadOnly");
            conn.Open();

            sb.AppendLine($"## {Path.GetFileNameWithoutExtension(file)}");
            sb.AppendLine();
            sb.AppendLine("| Table | Live Expected | Live Found | WAL-Recovered Expected | WAL-Recovered Found | Status |");
            sb.AppendLine("|---|---|---|---|---|---|");

            foreach (var expected in tables)
            {
                if (expected.IsDeleted) continue;
                if (!masterRows.TryGetValue(expected.Name, out var master)) continue;

                int liveFound = -1;
                try { liveFound = db.ReadTableRows(master.RootPage!.Value).Count(); } catch { }

                int walFound = 0;
                if (expected.RowsDeleted > 0)
                {
                    string recoveredTable = $"\"{ShadowDatabaseBuilder.RecoveredTablePrefix}{expected.Name}\"";
                    try
                    {
                        walFound = QueryCount(conn, recoveredTable,
                            $"\"{ShadowDatabaseBuilder.RecoveryMethodColumn}\" IN " +
                            $"('{ShadowDatabaseBuilder.RecoveryMethodWalFrame}', " +
                            $"'{ShadowDatabaseBuilder.RecoveryMethodWalPreviousVersion}')");
                    }
                    catch { walFound = -1; }
                }

                string liveStr   = liveFound >= 0 ? liveFound.ToString() : "error";
                string walStr    = expected.RowsDeleted > 0 ? (walFound >= 0 ? walFound.ToString() : "error") : "—";
                string delExp    = expected.RowsDeleted > 0 ? expected.RowsDeleted.ToString() : "—";

                bool liveOk = liveFound == expected.RowsAlive;
                bool walOk  = expected.RowsDeleted == 0 || walFound == expected.RowsDeleted;
                string status = liveOk && walOk ? "✅" : "⚠️";

                sb.AppendLine($"| {expected.Name} | {expected.RowsAlive} | {liveStr} | {delExp} | {walStr} | {status} |");
            }

            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();
        }

        if (!anyData)
            sb.AppendLine("No SHARD-created WAL test databases found.");

        string report = sb.ToString();
        output.WriteLine(report);

        string? summaryPath = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
        if (summaryPath is not null)
            File.AppendAllText(summaryPath, report);

        string? reportFilePath = Environment.GetEnvironmentVariable("CORPUS_REPORT_FILE");
        if (reportFilePath is not null)
            File.AppendAllText(reportFilePath, report);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private sealed class TempShadowDb(string path) : IDisposable
    {
        public string Path { get; } = path;
        public void Dispose()
        {
            try { File.Delete(Path); } catch { }
        }
    }

    private static TempShadowDb CreateTempShadow(string dbPath, SqliteForensicDatabase db, WalFile wal)
    {
        string tempBase = System.IO.Path.GetTempFileName();
        string tempPath = System.IO.Path.ChangeExtension(tempBase, ".db");
        File.Move(tempBase, tempPath);

        ShadowDatabaseBuilder.Create(tempPath, db);

        using var conn = new SqliteConnection($"Data Source={tempPath}");
        conn.Open();
        ShadowDatabaseBuilder.InsertWalDeletedRows(conn, db, wal);

        return new TempShadowDb(tempPath);
    }

    private static int QueryCount(SqliteConnection conn, string table, string? where = null)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table}" +
                          (where is not null ? $" WHERE {where}" : "");
        var result = cmd.ExecuteScalar();
        return result is long l ? (int)l : 0;
    }

    private static List<Dictionary<string, string>> QueryRecoveredRows(
        SqliteConnection conn, string table, IReadOnlyList<string> colNames)
    {
        var results = new List<Dictionary<string, string>>();
        using var cmd = conn.CreateCommand();
        string quotedCols = string.Join(", ", colNames.Select(c => $"\"{c}\""));
        cmd.CommandText =
            $"SELECT {quotedCols}, \"{ShadowDatabaseBuilder.RecoveryMethodColumn}\" " +
            $"FROM {table} " +
            $"WHERE \"{ShadowDatabaseBuilder.RecoveryMethodColumn}\" IN " +
            $"('{ShadowDatabaseBuilder.RecoveryMethodWalFrame}', " +
            $"'{ShadowDatabaseBuilder.RecoveryMethodWalPreviousVersion}')";

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var row = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < colNames.Count; i++)
            {
                row[colNames[i]] = reader.IsDBNull(i) ? "NULL"
                    : reader.GetFieldType(i) == typeof(long)
                        ? reader.GetInt64(i).ToString(CultureInfo.InvariantCulture)
                        : reader.GetString(i);
            }
            results.Add(row);
        }
        return results;
    }

    /// <summary>Renders a carved cell's fields as strings keyed by column name, substituting the cell's rowid for a rowid-alias column (its own header slot is always NULL on disk).</summary>
    private static Dictionary<string, string> CellToFieldStrings(SHARD.Core.Schema.TableSchema schema, SHARD.Core.Records.BTreeLeafCell cell)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < schema.Columns.Count; i++)
        {
            var col = schema.Columns[i];
            object? value = col.IsRowIdAlias ? cell.RowId.Value : cell.FieldValues.ElementAtOrDefault(i)?.Value;
            dict[col.Name] = value is null ? "NULL" : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "NULL";
        }
        return dict;
    }

    // ── XML parser (same format as CorpusTests) ───────────────────────────────

    private static List<TableExpectation>? TryParseXml(string xmlPath)
    {
        try
        {
            var settings = new XmlReaderSettings { CheckCharacters = false };
            using var reader = XmlReader.Create(xmlPath, settings);
            var doc = XDocument.Load(reader);
            var result = new List<TableExpectation>();

            foreach (var el in doc.Root?.Elements("element") ?? [])
            {
                var meta = el.Element("meta");
                if (!string.Equals(meta?.Element("type")?.Value, "table", StringComparison.OrdinalIgnoreCase))
                    continue;

                string name     = meta?.Element("name")?.Value ?? "";
                bool isDeleted  = string.Equals(meta?.Element("deleted")?.Value, "True", StringComparison.OrdinalIgnoreCase);
                int rowsAlive   = int.TryParse(meta?.Element("rowsAlive")?.Value,   out int a) ? a : 0;
                int rowsDeleted = int.TryParse(meta?.Element("rowsDeleted")?.Value, out int d) ? d : 0;
                string mode     = meta?.Element("mode")?.Value ?? "loose";

                var deletedRows = new List<Row>();
                foreach (var rowEl in el.Descendants("row"))
                {
                    if (rowEl.Attribute("deleted")?.Value != "1") continue;
                    var fields = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var col in rowEl.Elements("column"))
                    {
                        string? colName = col.Element("name")?.Value;
                        string? content = col.Element("content")?.Value;
                        if (colName is not null && content is not null)
                            fields[colName] = content;
                    }
                    deletedRows.Add(new Row(fields));
                }

                result.Add(new TableExpectation(name, isDeleted, rowsAlive, rowsDeleted, deletedRows, mode));
            }

            return result;
        }
        catch (XmlException)
        {
            return null;
        }
    }

    // ── Data records ──────────────────────────────────────────────────────────

    private record TableExpectation(
        string Name,
        bool IsDeleted,
        int RowsAlive,
        int RowsDeleted,
        IReadOnlyList<Row> DeletedRows,
        string Mode = "loose");

    private record Row(IReadOnlyDictionary<string, string> Fields);
}
