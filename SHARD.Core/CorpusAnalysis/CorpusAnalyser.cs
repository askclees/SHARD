using System.Xml;
using System.Xml.Linq;
using SHARD.Core.Enums;
using SHARD.Core.Pages;

namespace SHARD.Core.CorpusAnalysis;

public sealed record CorpusTableExpectation(string Name, bool IsDeleted, int RowsAlive, int RowsDeleted);

public sealed record TableAnalysisResult(
    string TableName,
    int ExpectedLive,
    int ActualLive,
    int ExpectedDeleted,
    int ActualDeleted,
    string? Error)
{
    public bool LivePass    => Error is null && ActualLive == ExpectedLive;
    public bool DeletedPass => Error is null && (ExpectedDeleted == 0 || ActualDeleted == ExpectedDeleted);
    public bool IsPass      => LivePass && DeletedPass;
}

public sealed record DatabaseAnalysisResult(
    string Section,
    string FileName,
    IReadOnlyList<TableAnalysisResult> Tables,
    string? ParseError = null)
{
    public bool IsSkipped => ParseError is not null;
    public bool IsPass    => !IsSkipped && Tables.All(t => t.IsPass);
    public bool IsFail    => !IsSkipped && !IsPass;
}

/// <summary>
/// Runs live and deleted record counts for every database in a SQLite Forensic Corpus
/// tree and returns per-database results for reporting.
/// </summary>
public static class CorpusAnalyser
{
    public static readonly string[] DefaultSections =
        ["01", "02", "03", "04", "05", "06", "07", "08", "09", "0A", "0B", "0C", "0D", "0E"];

    public static IEnumerable<DatabaseAnalysisResult> Analyse(
        string corpusRoot,
        string[]? sections = null,
        bool checkDeleted = true,
        IProgress<string>? progress = null)
    {
        foreach (string section in sections ?? DefaultSections)
        {
            string dir = Path.Combine(corpusRoot, section);
            if (!Directory.Exists(dir)) continue;

            foreach (string dbPath in Directory.GetFiles(dir, "*.db").OrderBy(f => f))
            {
                string xmlPath = Path.ChangeExtension(dbPath, ".xml");
                if (!File.Exists(xmlPath)) continue;

                string fileName = Path.GetFileName(dbPath);
                progress?.Report($"{section}/{fileName}");

                var expectations = TryParseXml(xmlPath);
                if (expectations is null)
                {
                    yield return new DatabaseAnalysisResult(section, fileName, [], "Malformed XML — skipped");
                    continue;
                }

                yield return AnalyseDatabase(section, fileName, dbPath, expectations, checkDeleted);
            }
        }
    }

    private static DatabaseAnalysisResult AnalyseDatabase(
        string section, string fileName, string dbPath,
        List<CorpusTableExpectation> expectations, bool checkDeleted)
    {
        var tables = new List<TableAnalysisResult>();
        try
        {
            using var db = SqliteForensicDatabase.Open(dbPath);
            var masterRows = db.ReadSqliteMaster()
                .Where(r => r.ObjectType == SqliteMasterObjectType.Table && r.RootPage is not null)
                .ToDictionary(r => r.Name ?? "", r => r);

            foreach (var exp in expectations)
            {
                if (exp.IsDeleted) continue;
                if (!masterRows.TryGetValue(exp.Name, out var master)) continue;
                if (master.RootPage == 0u) continue;

                int liveCount;
                string? error = null;
                try   { liveCount = db.ReadTableRows(master.RootPage!.Value).Count(); }
                catch (Exception ex) { liveCount = 0; error = $"{ex.GetType().Name}: {ex.Message}"; }

                int deletedCount = 0;
                if (checkDeleted && exp.RowsDeleted > 0 && error is null)
                {
                    deletedCount = db.GetTreePageNumbers(master.RootPage!.Value)
                        .Select(p => db.ReadPage(p))
                        .OfType<TableBTreeLeafPage>()
                        .Sum(p => p.DeletedCells.Count);
                }

                tables.Add(new TableAnalysisResult(
                    exp.Name,
                    exp.RowsAlive, liveCount,
                    exp.RowsDeleted, deletedCount,
                    error));
            }
        }
        catch (Exception ex)
        {
            return new DatabaseAnalysisResult(section, fileName, [], $"DB open error: {ex.Message}");
        }

        return new DatabaseAnalysisResult(section, fileName, tables);
    }

    public static List<CorpusTableExpectation>? TryParseXml(string xmlPath)
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
        catch (XmlException) { return null; }
    }
}
