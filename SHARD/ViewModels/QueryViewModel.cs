using System;
using System.Collections.ObjectModel;
using System.Reactive;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using ReactiveUI;
using SHARD.Core.Shadow;

namespace SHARD.ViewModels;

public sealed class QueryViewModel : ViewModelBase
{
    private string? _shadowDbPath;

    // ── Input ─────────────────────────────────────────────────────────────────

    private string _queryText = "SELECT * FROM ";
    public string QueryText
    {
        get => _queryText;
        set => this.RaiseAndSetIfChanged(ref _queryText, value);
    }

    // ── State ─────────────────────────────────────────────────────────────────

    private string? _errorMessage;
    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            this.RaiseAndSetIfChanged(ref _errorMessage, value);
            this.RaisePropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => _errorMessage is not null;

    private bool _hasRun;
    public bool HasRun
    {
        get => _hasRun;
        private set => this.RaiseAndSetIfChanged(ref _hasRun, value);
    }

    private string _summary = "";
    public string Summary
    {
        get => _summary;
        private set => this.RaiseAndSetIfChanged(ref _summary, value);
    }

    public bool HasResults => HasRun && !HasError && Results.Count > 0;

    // ── Results ───────────────────────────────────────────────────────────────

    public ObservableCollection<string> ColumnNames { get; } = [];
    public ObservableCollection<QueryResultRow> Results { get; } = [];

    /// <summary>
    /// Raised after every query run (success or failure), once <see cref="ColumnNames"/>
    /// and <see cref="Results"/> are fully populated. The DataGrid's columns can't be
    /// declared statically (the result shape is arbitrary), so the view rebuilds them
    /// from <see cref="ColumnNames"/> in response to this event rather than relying on
    /// the command's own observable, which fires before bindings have settled.
    /// </summary>
    public event EventHandler? ResultsUpdated;

    // ── Tables (for the table-list side panel) ──────────────────────────────────

    public ObservableCollection<QueryTableViewModel> TableNames { get; } = [];

    // ── Command ───────────────────────────────────────────────────────────────

    public ReactiveCommand<Unit, Unit> RunQueryCommand { get; }

    public QueryViewModel()
    {
        RunQueryCommand = ReactiveCommand.Create(RunQuery);
    }

    public void SetShadowDatabasePath(string? path)
    {
        _shadowDbPath = path;
        TableNames.Clear();
        if (path is null) return;

        try
        {
            using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                string name = reader.GetString(0);
                if (!name.StartsWith(ShadowDatabaseBuilder.InternalTablePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    TableNames.Add(new QueryTableViewModel(name, name));
                }
                else if (name.StartsWith(ShadowDatabaseBuilder.DeletedTablePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    string bare = name[ShadowDatabaseBuilder.DeletedTablePrefix.Length..];
                    TableNames.Add(new QueryTableViewModel(name, $"{bare} (deleted)"));
                }
            }
        }
        catch
        {
            // Best-effort table list; any real query error is surfaced when the user runs a query.
        }
    }

    // ── Options ───────────────────────────────────────────────────────────────

    private bool _includeDeletedRecords;
    public bool IncludeDeletedRecords
    {
        get => _includeDeletedRecords;
        set => this.RaiseAndSetIfChanged(ref _includeDeletedRecords, value);
    }

    /// <summary>Set the query text to a default "SELECT * FROM ..." for the given table and run it.</summary>
    public void RunQueryForTable(string tableName)
    {
        QueryText = $"SELECT * FROM {QuoteIdentifier(tableName)}";
        RunQueryCommand.Execute().Subscribe();
    }

    private static string QuoteIdentifier(string name) => $"\"{name.Replace("\"", "\"\"")}\"";

    /// <summary>
    /// If <see cref="IncludeDeletedRecords"/> is on, wraps the user's SQL in a CTE and
    /// UNIONs the matching recovered table, so the original <see cref="QueryText"/> is
    /// never modified. Returns the original SQL unchanged if no known table is detected.
    /// </summary>
    private string BuildRuntimeSql()
    {
        if (!_includeDeletedRecords) return QueryText;

        // Find the first known table referenced after FROM (quoted or bare identifier).
        string? matched = null;
        foreach (var t in TableNames)
        {
            string pattern = @"\bFROM\s+(" + Regex.Escape($"\"{t.ActualName}\"") + "|" + Regex.Escape(t.ActualName) + @"\b)";
            if (Regex.IsMatch(QueryText, pattern, RegexOptions.IgnoreCase))
            {
                matched = t.ActualName;
                break;
            }
        }

        if (matched is null) return QueryText;

        string recovered = ShadowDatabaseBuilder.RecoveredTablePrefix + matched;

        // Use the recovered table's actual column list on both sides of the UNION so the
        // column counts always match, even when the live shadow table has extra columns
        // that an older project's recovered table doesn't (e.g. _overflow_page).
        var cols = GetTableColumns(recovered);
        if (cols.Count == 0) return QueryText;

        string colList = string.Join(", ", cols.Select(QuoteIdentifier));

        return $"WITH _shard_q AS ({QueryText})\n" +
               $"SELECT {colList}, 0 AS _is_recovered FROM _shard_q\n" +
               $"UNION ALL\n" +
               $"SELECT {colList}, 1 AS _is_recovered FROM {QuoteIdentifier(recovered)}";
    }

    private List<string> GetTableColumns(string tableName)
    {
        if (_shadowDbPath is null) return [];
        try
        {
            using var connection = new SqliteConnection($"Data Source={_shadowDbPath};Mode=ReadOnly");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA table_info({QuoteIdentifier(tableName)})";
            using var reader = command.ExecuteReader();
            var cols = new List<string>();
            while (reader.Read())
                cols.Add(reader.GetString(1)); // column index 1 = name
            return cols;
        }
        catch
        {
            return [];
        }
    }

    // ── Run ───────────────────────────────────────────────────────────────────

    private void RunQuery()
    {
        Results.Clear();
        ColumnNames.Clear();
        ErrorMessage = null;
        Summary = "";

        if (_shadowDbPath is null)
        {
            ErrorMessage = "Create a project first to query its shadow database.";
            HasRun = true;
            return;
        }

        try
        {
            using var connection = new SqliteConnection($"Data Source={_shadowDbPath};Mode=ReadOnly");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = BuildRuntimeSql();
            using var reader = command.ExecuteReader();

            for (int i = 0; i < reader.FieldCount; i++)
                ColumnNames.Add(reader.GetName(i));

            while (reader.Read())
            {
                var row = new QueryResultRow(reader.FieldCount);
                for (int i = 0; i < reader.FieldCount; i++)
                    row[i] = reader.IsDBNull(i) ? "NULL" : Convert.ToString(reader.GetValue(i)) ?? "";
                Results.Add(row);
            }

            Summary = $"{Results.Count} row{(Results.Count == 1 ? "" : "s")}";
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            HasRun = true;
            this.RaisePropertyChanged(nameof(HasResults));
            ResultsUpdated?.Invoke(this, EventArgs.Empty);
        }
    }

    public string BuildCsv()
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", ColumnNames.Select(CsvEscape)));
        foreach (var row in Results)
        {
            sb.AppendLine(string.Join(",",
                Enumerable.Range(0, ColumnNames.Count).Select(i => CsvEscape(row[i]))));
        }
        return sb.ToString();
    }

    private static string CsvEscape(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }

    // ── Reset ─────────────────────────────────────────────────────────────────

    public void Clear()
    {
        QueryText = "SELECT * FROM ";
        ErrorMessage = null;
        Summary = "";
        HasRun = false;
        IncludeDeletedRecords = false;
        Results.Clear();
        ColumnNames.Clear();
        TableNames.Clear();
        _shadowDbPath = null;
        this.RaisePropertyChanged(nameof(HasResults));
    }
}
