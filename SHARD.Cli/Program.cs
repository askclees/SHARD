using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SHARD.Core;
using SHARD.Core.CorpusAnalysis;
using SHARD.Core.Enums;
using SHARD.Core.Pages;
using SHARD.Core.Schema;

var Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

var jsonOptions = new JsonSerializerOptions
{
    WriteIndented              = true,
    DefaultIgnoreCondition     = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy       = JsonNamingPolicy.CamelCase,
};

// ── Top-level dispatch ────────────────────────────────────────────────────────

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    PrintHelp();
    return 0;
}

// Collect global options that may appear anywhere
string? outputPath = null;
string  format     = "json";
var     positional = new List<string>();

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "-o" or "--output":
            if (++i >= args.Length) Die("--output requires a value.");
            outputPath = args[i];
            break;
        case "-f" or "--format":
            if (++i >= args.Length) Die("--format requires a value.");
            format = args[i].ToLowerInvariant();
            if (format is not ("json" or "text")) Die($"Unknown format '{format}'. Use 'json' or 'text'.");
            break;
        case "-h" or "--help":
            PrintHelp();
            return 0;
        default:
            positional.Add(args[i]);
            break;
    }
}

if (positional.Count == 0) { PrintHelp(); return 0; }

string command = positional[0];

return command switch
{
    "rows"    => RunRows(positional, outputPath, format),
    "deleted" => RunDeleted(positional, outputPath, format),
    "schema"  => RunSchema(positional, outputPath, format),
    "pages"   => RunPages(positional, outputPath, format),
    "header"  => RunHeader(positional, outputPath, format),
    "corpus"  => RunCorpus(positional, outputPath, format, args),
    _         => Die($"Unknown command '{command}'. Run 'shard-cli --help' for usage."),
};

// ── rows ─────────────────────────────────────────────────────────────────────

int RunRows(List<string> pos, string? outPath, string fmt)
{
    if (pos.Count < 3) Die("Usage: shard-cli rows <db-file> <table>");
    string dbPath    = pos[1];
    string tableName = pos[2];

    using var db = OpenDb(dbPath);
    var master = db.ReadSqliteMaster()
        .FirstOrDefault(r => r.ObjectType == SqliteMasterObjectType.Table
                          && string.Equals(r.Name, tableName, StringComparison.OrdinalIgnoreCase));
    if (master is null) Die($"Table '{tableName}' not found in '{dbPath}'.");

    var schema  = db.GetTableSchema(tableName);
    var columns = BuildColumnNames(schema);
    var rows    = db.ReadTableRows(master!.RootPage!.Value).ToList();

    var doc = new
    {
        table   = tableName,
        columns,
        count   = rows.Count,
        rows    = rows.Select(r => RowToDict(r.FieldValues, r.RowId, r.PageNumber, r.CellOffset, columns, schema)),
    };

    Write(outPath, fmt, doc, rows =>
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Table: {tableName}  ({rows.count} rows)");
        foreach (var r in (IEnumerable<object>)rows.rows)
            sb.AppendLine(JsonSerializer.Serialize(r));
        return sb.ToString();
    });
    return 0;
}

// ── deleted ───────────────────────────────────────────────────────────────────

int RunDeleted(List<string> pos, string? outPath, string fmt)
{
    if (pos.Count < 3) Die("Usage: shard-cli deleted <db-file> <table>");
    string dbPath    = pos[1];
    string tableName = pos[2];

    using var db = OpenDb(dbPath);
    var master = db.ReadSqliteMaster()
        .FirstOrDefault(r => r.ObjectType == SqliteMasterObjectType.Table
                          && string.Equals(r.Name, tableName, StringComparison.OrdinalIgnoreCase));
    if (master is null) Die($"Table '{tableName}' not found in '{dbPath}'.");

    var schema  = db.GetTableSchema(tableName);
    var columns = BuildColumnNames(schema);

    var deleted = db.GetTreePageNumbers(master!.RootPage!.Value)
        .Select(p => db.ReadPage(p))
        .OfType<TableBTreeLeafPage>()
        .SelectMany(p => p.DeletedCells)
        .ToList();

    var doc = new
    {
        table   = tableName,
        columns,
        count   = deleted.Count,
        rows    = deleted.Select(c => RowToDict(c.FieldValues, c.RowId.Value, (uint)0, c.PageOffset, columns, schema)),
    };

    Write(outPath, fmt, doc, d =>
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Table: {tableName}  ({d.count} recovered deleted rows)");
        foreach (var r in (IEnumerable<object>)d.rows)
            sb.AppendLine(JsonSerializer.Serialize(r));
        return sb.ToString();
    });
    return 0;
}

// ── schema ────────────────────────────────────────────────────────────────────

int RunSchema(List<string> pos, string? outPath, string fmt)
{
    if (pos.Count < 2) Die("Usage: shard-cli schema <db-file>");
    using var db = OpenDb(pos[1]);

    var entries = db.ReadSqliteMaster().Select(r => new
    {
        type     = r.ObjectType.ToString().ToLowerInvariant(),
        name     = r.Name,
        table    = r.TableName,
        rootPage = r.RootPage,
        sql      = r.Sql,
        page     = r.PageNumber,
        offset   = r.CellOffset,
    }).ToList();

    var doc = new { count = entries.Count, entries };

    Write(outPath, fmt, doc, d =>
    {
        var sb = new StringBuilder();
        foreach (var e in d.entries)
            sb.AppendLine($"{e.type,-8}  {e.name,-40}  root={e.rootPage}");
        return sb.ToString();
    });
    return 0;
}

// ── pages ─────────────────────────────────────────────────────────────────────

int RunPages(List<string> pos, string? outPath, string fmt)
{
    if (pos.Count < 2) Die("Usage: shard-cli pages <db-file>");
    using var db = OpenDb(pos[1]);

    var pageMap = db.BuildPageTableMap();
    var pages = Enumerable.Range(1, (int)db.PageCount).Select(n =>
    {
        var page = db.ReadPage((uint)n);
        pageMap.TryGetValue((uint)n, out string? tableName);
        int? deleted = page is TableBTreeLeafPage tlp ? tlp.DeletedCells.Count : null;
        return new
        {
            page     = n,
            type     = page.PageType.ToString(),
            table    = tableName,
            deleted,
        };
    }).ToList();

    var doc = new
    {
        pageSize  = db.Header.PageSize,
        pageCount = db.PageCount,
        pages,
    };

    Write(outPath, fmt, doc, d =>
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Page size: {d.pageSize}  Pages: {d.pageCount}");
        foreach (var p in d.pages)
            sb.AppendLine($"  {p.page,4}  {p.type,-22}  {p.table ?? ""}");
        return sb.ToString();
    });
    return 0;
}

// ── header ────────────────────────────────────────────────────────────────────

int RunHeader(List<string> pos, string? outPath, string fmt)
{
    if (pos.Count < 2) Die("Usage: shard-cli header <db-file>");
    using var db = OpenDb(pos[1]);
    var h = db.Header;

    var doc = new
    {
        magic                  = h.Magic.TrimEnd('\0'),
        pageSize               = h.PageSize,
        writeVersion           = h.WriteVersionName,
        readVersion            = (int)h.ReadVersion,
        reservedBytesPerPage   = (int)h.ReservedBytesPerPage,
        fileChangeCounter      = h.FileChangeCounter,
        databaseSizeInPages    = h.DatabaseSizeInPages,
        firstFreelistTrunkPage = h.FirstFreelistTrunkPage,
        totalFreelistPages     = h.TotalFreelistPages,
        schemaCookie           = h.SchemaCookie,
        schemaFormat           = h.SchemaFormat,
        textEncoding           = h.TextEncodingName,
        userVersion            = h.UserVersion,
        applicationId          = h.ApplicationId,
        sqliteVersion          = $"{h.SqliteVersionNumber / 1_000_000}.{h.SqliteVersionNumber % 1_000_000 / 1_000}.{h.SqliteVersionNumber % 1_000}",
    };

    Write(outPath, fmt, doc, d =>
    {
        var sb = new StringBuilder();
        foreach (var prop in d.GetType().GetProperties())
            sb.AppendLine($"{prop.Name,-25} {prop.GetValue(d)}");
        return sb.ToString();
    });
    return 0;
}

// ── corpus ────────────────────────────────────────────────────────────────────

int RunCorpus(List<string> pos, string? outPath, string fmt, string[] allArgs)
{
    if (pos.Count < 2) Die("Usage: shard-cli corpus <corpus-path> [-s sections] [--no-deleted]");
    string corpusPath = pos[1];
    if (!Directory.Exists(corpusPath)) Die($"Corpus path not found: {corpusPath}");

    string[]? sections  = null;
    bool      noDeleted = false;
    for (int i = 0; i < allArgs.Length; i++)
    {
        if (allArgs[i] is "-s" or "--sections" && i + 1 < allArgs.Length)
            sections = allArgs[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (allArgs[i] == "--no-deleted")
            noDeleted = true;
    }

    var progress = new Progress<string>(msg => Console.Error.Write($"\r  {msg}...{new string(' ', 10)}"));
    var results  = CorpusAnalyser.Analyse(corpusPath, sections, !noDeleted, progress).ToList();
    Console.Error.Write($"\r{new string(' ', 60)}\r");

    int passed  = results.Count(r => r.IsPass);
    int failed  = results.Count(r => r.IsFail);
    int skipped = results.Count(r => r.IsSkipped);

    var doc = new
    {
        corpus   = corpusPath,
        sections = sections ?? CorpusAnalyser.DefaultSections,
        summary  = new { total = results.Count, passed, failed, skipped },
        results  = results.Select(r => new
        {
            section    = r.Section,
            file       = r.FileName,
            status     = r.IsSkipped ? "skip" : r.IsPass ? "pass" : "fail",
            parseError = r.ParseError,
            tables     = r.Tables.Select(t => new
            {
                name            = t.TableName,
                expectedLive    = t.ExpectedLive,
                actualLive      = t.ActualLive,
                expectedDeleted = t.ExpectedDeleted,
                actualDeleted   = t.ActualDeleted,
                error           = t.Error,
                pass            = t.IsPass,
            }),
        }),
    };

    Write(outPath, fmt, doc, d =>
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Corpus: {d.corpus}");
        string? sec = null;
        foreach (var r in d.results)
        {
            if (r.section != sec) { sec = r.section; sb.AppendLine($"── Section {sec} ──"); }
            if (r.status == "skip") { sb.AppendLine($"  [SKIP] {r.file}  ({r.parseError})"); continue; }
            if (r.status == "pass") { sb.AppendLine($"  [PASS] {r.file}"); continue; }
            sb.AppendLine($"  [FAIL] {r.file}");
            foreach (var t in r.tables.Where(t => !t.pass))
            {
                string live = t.actualLive == t.expectedLive ? $"live {t.actualLive}/{t.expectedLive} ✓" : $"live {t.actualLive}/{t.expectedLive} ✗";
                string del  = t.expectedDeleted == 0 ? "" : t.actualDeleted == t.expectedDeleted ? $"  deleted {t.actualDeleted}/{t.expectedDeleted} ✓" : $"  deleted {t.actualDeleted}/{t.expectedDeleted} ✗";
                sb.AppendLine($"    {t.name}: {live}{del}{(t.error is not null ? $"  ERROR: {t.error}" : "")}");
            }
        }
        sb.AppendLine(new string('─', 60));
        sb.AppendLine($"Total: {d.summary.total}  Passed: {d.summary.passed}  Failed: {d.summary.failed}  Skipped: {d.summary.skipped}");
        return sb.ToString();
    });

    return failed > 0 ? 1 : 0;
}

// ── Helpers ───────────────────────────────────────────────────────────────────

SqliteForensicDatabase OpenDb(string path)
{
    if (!File.Exists(path)) Die($"File not found: {path}");
    try   { return SqliteForensicDatabase.Open(path); }
    catch (Exception ex) { Die($"Cannot open '{path}': {ex.Message}"); throw; }
}

void Write<T>(string? outPath, string fmt, T doc, Func<T, string> textRenderer)
{
    string content = fmt == "text"
        ? textRenderer(doc)
        : JsonSerializer.Serialize(doc, jsonOptions);

    if (outPath is not null)
    {
        File.WriteAllText(outPath, content, Utf8NoBom);
        Console.Error.WriteLine($"Written to {outPath}");
    }
    else
    {
        using var stdout = new StreamWriter(Console.OpenStandardOutput(), Utf8NoBom, leaveOpen: true);
        stdout.Write(content);
        if (!content.EndsWith('\n')) stdout.WriteLine();
    }
}

List<string> BuildColumnNames(TableSchema? schema)
{
    if (schema is null) return [];
    return schema.Columns.Select(c => c.Name).ToList();
}

Dictionary<string, object?> RowToDict(
    IList<SHARD.Core.Records.SqliteValue?> fields,
    long rowId, uint pageNumber, int cellOffset,
    List<string> columns, TableSchema? schema)
{
    var dict = new Dictionary<string, object?>();
    int fieldIdx = 0;
    for (int i = 0; i < (schema?.Columns.Count ?? fields.Count); i++)
    {
        string colName = i < columns.Count ? columns[i] : $"col{i}";
        bool isRowIdAlias = schema?.Columns[i].IsRowIdAlias ?? false;
        dict[colName] = isRowIdAlias ? rowId : fields.ElementAtOrDefault(fieldIdx++)?.Value;
        if (!isRowIdAlias) { /* fieldIdx already advanced above */ }
    }
    if (fields.Count > (schema?.Columns.Count ?? 0))
        for (int i = schema?.Columns.Count ?? 0; i < fields.Count; i++)
            dict[$"col{i}"] = fields[i]?.Value;

    dict["_rowid"]  = rowId;
    dict["_page"]   = pageNumber;
    dict["_offset"] = cellOffset;
    return dict;
}

int Die(string message)
{
    Console.Error.WriteLine($"error: {message}");
    Environment.Exit(2);
    return 2;
}

// ── Help ──────────────────────────────────────────────────────────────────────

static void PrintHelp() => Console.WriteLine("""
    shard-cli — SHARD forensic SQLite inspector

    Usage:
      shard-cli <command> [args] [options]

    Commands:
      rows    <db> <table>   Dump live rows from a table
      deleted <db> <table>   Dump recovered deleted rows from a table
      schema  <db>           List all sqlite_master entries
      pages   <db>           List all pages with type and table assignment
      header  <db>           Dump database header fields
      corpus  <path>         Run SQLite Forensic Corpus regression tests
                             [-s 01,02,0C]  sections to include
                             [--no-deleted] skip deleted record checks

    Global options:
      -f, --format json|text   Output format (default: json)
      -o, --output <file>      Write to file instead of stdout
      -h, --help               Show this help

    Exit codes:
      0  Success (or all corpus checks passed)
      1  Corpus checks failed
      2  Usage or file error

    Examples:
      shard-cli rows    mydb.sqlite users
      shard-cli deleted mydb.sqlite users
      shard-cli schema  mydb.sqlite
      shard-cli pages   mydb.sqlite -f text
      shard-cli header  mydb.sqlite
      shard-cli corpus  /data/corpus -f text -s 0C,0D,0E
      shard-cli rows    mydb.sqlite users -o rows.json
    """);
