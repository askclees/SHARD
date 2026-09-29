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

    /// <summary>
    /// Raised when the user double-clicks a query result row that has page-location
    /// metadata. Both the main window and any floating query window subscribe so the
    /// main window can switch to the correct page and tab.
    /// </summary>
    public event EventHandler<QueryNavigationEventArgs>? NavigationRequested;

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
                else if (name.StartsWith(ShadowDatabaseBuilder.RecoveredTablePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    string bare = name[ShadowDatabaseBuilder.RecoveredTablePrefix.Length..];
                    TableNames.Add(new QueryTableViewModel(name, $"{bare} (recovered)"));
                }
            }
        }
        catch
        {
            // Best-effort table list; any real query error is surfaced when the user runs a query.
        }
    }

    // ── Options ───────────────────────────────────────────────────────────────

    /// <summary>Which of a table's rows a query runs against: only its live shadow table,
    /// only its <c>_shard_recovered_*</c> table, or both (unioned, tagged with an
    /// <c>_is_recovered</c> column). See <see cref="BuildRuntimeSql"/>.</summary>
    public enum RecordScope { Live, Recovered, LiveAndRecovered }

    private RecordScope _recordScope = RecordScope.Live;
    public RecordScope SelectedRecordScope
    {
        get => _recordScope;
        private set
        {
            this.RaiseAndSetIfChanged(ref _recordScope, value);
            this.RaisePropertyChanged(nameof(IsLiveOnly));
            this.RaisePropertyChanged(nameof(IsRecoveredOnly));
            this.RaisePropertyChanged(nameof(IsLiveAndRecovered));
        }
    }

    // Exposed as three mutually-exclusive bools rather than SelectedRecordScope directly so the
    // view can bind them straight to a RadioButton group without a value converter.
    public bool IsLiveOnly
    {
        get => _recordScope == RecordScope.Live;
        set { if (value) SelectedRecordScope = RecordScope.Live; }
    }

    public bool IsRecoveredOnly
    {
        get => _recordScope == RecordScope.Recovered;
        set { if (value) SelectedRecordScope = RecordScope.Recovered; }
    }

    public bool IsLiveAndRecovered
    {
        get => _recordScope == RecordScope.LiveAndRecovered;
        set { if (value) SelectedRecordScope = RecordScope.LiveAndRecovered; }
    }

    /// <summary>Set the query text to a default "SELECT * FROM ..." for the given table and run it.</summary>
    public void RunQueryForTable(string tableName)
    {
        QueryText = $"SELECT * FROM {QuoteIdentifier(tableName)}";
        RunQueryCommand.Execute().Subscribe();
    }

    private static string QuoteIdentifier(string name) => $"\"{name.Replace("\"", "\"\"")}\"";

    /// <summary>
    /// Builds the regex that matches "FROM &lt;table&gt;" for either a quoted or bare
    /// reference to <paramref name="tableName"/>, as it would appear in user-typed SQL.
    /// </summary>
    private static string BuildFromMatchPattern(string tableName) =>
        @"\bFROM\s+(" + Regex.Escape($"\"{tableName}\"") + "|" + Regex.Escape(tableName) + @"\b)";

    /// <summary>
    /// Finds the first *live* user table (never an internal <c>_shard_*</c> table — those
    /// are already scope-specific, so they're not candidates for further substitution)
    /// referenced after FROM in <see cref="QueryText"/>, quoted or bare.
    /// </summary>
    private string? FindMatchedLiveTableName()
    {
        foreach (var t in TableNames)
        {
            if (t.ActualName.StartsWith(ShadowDatabaseBuilder.InternalTablePrefix, StringComparison.OrdinalIgnoreCase))
                continue;
            if (Regex.IsMatch(QueryText, BuildFromMatchPattern(t.ActualName), RegexOptions.IgnoreCase))
                return t.ActualName;
        }
        return null;
    }

    /// <summary>
    /// Rewrites the user's SQL to match <see cref="SelectedRecordScope"/>: unchanged for
    /// <see cref="RecordScope.Live"/>; its FROM table swapped for the matching
    /// <c>_shard_recovered_*</c> table for <see cref="RecordScope.Recovered"/> (preserving
    /// any WHERE/ORDER BY/etc. as-is); or wrapped in a CTE and UNIONed with the recovered
    /// table, tagged with an <c>_is_recovered</c> column, for
    /// <see cref="RecordScope.LiveAndRecovered"/>. Returns the original SQL unchanged if no
    /// known live table is detected.
    /// </summary>
    private string BuildRuntimeSql()
    {
        if (_recordScope == RecordScope.Live) return QueryText;

        string? matched = FindMatchedLiveTableName();
        if (matched is null) return QueryText;

        string recovered = ShadowDatabaseBuilder.RecoveredTablePrefix + matched;

        if (_recordScope == RecordScope.Recovered)
        {
            return Regex.Replace(
                QueryText, BuildFromMatchPattern(matched), "FROM " + QuoteIdentifier(recovered),
                RegexOptions.IgnoreCase);
        }

        // LiveAndRecovered — use the recovered table's actual column list on both sides of the
        // UNION so the column counts always match, even when the live shadow table has extra
        // columns that an older project's recovered table doesn't (e.g. _overflow_page).
        var cols = GetTableColumns(recovered);
        if (cols.Count == 0) return QueryText;

        // Live shadow table has all recovered-table columns except _recovery_method.
        // Substitute NULL on the live side so the UNION column counts match and the
        // live SELECT doesn't fail with "no such column".
        string colList     = string.Join(", ", cols.Select(QuoteIdentifier));
        string liveColList = string.Join(", ", cols.Select(c =>
            c == ShadowDatabaseBuilder.RecoveryMethodColumn
                ? $"NULL AS {QuoteIdentifier(c)}"
                : QuoteIdentifier(c)));

        return $"WITH _shard_q AS ({QueryText})\n" +
               $"SELECT {liveColList}, 0 AS _is_recovered FROM _shard_q\n" +
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

    public record QueryNavigationEventArgs(uint PageNumber, string? RecoveryMethod, int CellOffset);

    /// <summary>
    /// Parses a double-clicked result row and fires <see cref="NavigationRequested"/> if the
    /// row contains page-location metadata columns.
    /// </summary>
    public void RequestNavigationFromRow(QueryResultRow row)
    {
        int pageNumIdx = -1, offsetIdx = -1, methodIdx = -1;
        for (int i = 0; i < ColumnNames.Count; i++)
        {
            if      (ColumnNames[i] == "_page_number")    pageNumIdx = i;
            else if (ColumnNames[i] == "_cell_offset")    offsetIdx  = i;
            else if (ColumnNames[i] == "_recovery_method") methodIdx = i;
        }

        if (pageNumIdx < 0) return;
        if (!uint.TryParse(row[pageNumIdx], out uint pageNumber) || pageNumber == 0) return;

        string? recoveryMethod = methodIdx >= 0 ? row[methodIdx] : null;
        int cellOffset = offsetIdx >= 0 && int.TryParse(row[offsetIdx], out int off) ? off : -1;

        NavigationRequested?.Invoke(this, new QueryNavigationEventArgs(pageNumber, recoveryMethod, cellOffset));
    }

    // ── Reset ─────────────────────────────────────────────────────────────────

    public void Clear()
    {
        QueryText = "SELECT * FROM ";
        ErrorMessage = null;
        Summary = "";
        HasRun = false;
        SelectedRecordScope = RecordScope.Live;
        Results.Clear();
        ColumnNames.Clear();
        TableNames.Clear();
        _shadowDbPath = null;
        this.RaisePropertyChanged(nameof(HasResults));
    }
}
