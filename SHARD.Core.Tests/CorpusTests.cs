using System.Xml;
using System.Xml.Linq;
using SHARD.Core.Enums;
using SHARD.Core.Pages;

namespace SHARD.Core.Tests;

/// <summary>
/// Tests against the SQLite Forensic Corpus v2.0.
/// Set the SQLITE_CORPUS_PATH environment variable to the extracted corpus root,
/// or extract to /tmp/sqlite_corpus/sqlite_forensic_corpus_v2.0.
/// Tests are skipped automatically when the corpus is not found.
/// </summary>
public class CorpusTests
{
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
                    ? SHARD.Core.Records.RecordStructure.FromSchema(schema)
                    : null;

                deletedFound = db.GetTreePageNumbers(master.RootPage!.Value)
                    .Select(p => db.ReadPage(p))
                    .OfType<TableBTreeLeafPage>()
                    .Sum(p =>
                    {
                        if (recordStructure is not null) p.CarveDeletedCells(recordStructure);
                        return p.DeletedCells.Count + p.CarvedCells.Count;
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

                result.Add(new CorpusTableExpectation(name, isDeleted, rowsAlive, rowsDeleted));
            }

            return result;
        }
        catch (XmlException)
        {
            return null; // corpus XML file is malformed — skip
        }
    }

    private record CorpusTableExpectation(string Name, bool IsDeleted, int RowsAlive, int RowsDeleted);
}
