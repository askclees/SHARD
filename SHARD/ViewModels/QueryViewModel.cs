using System;
using System.Collections.ObjectModel;
using System.Reactive;
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

    public ObservableCollection<string> TableNames { get; } = [];

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
                    TableNames.Add(name);
            }
        }
        catch
        {
            // Best-effort table list; any real query error is surfaced when the user runs a query.
        }
    }

    /// <summary>Set the query text to a default "SELECT * FROM ..." for the given table and run it.</summary>
    public void RunQueryForTable(string tableName)
    {
        QueryText = $"SELECT * FROM {QuoteIdentifier(tableName)}";
        RunQueryCommand.Execute().Subscribe();
    }

    private static string QuoteIdentifier(string name) => $"\"{name.Replace("\"", "\"\"")}\"";

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
            command.CommandText = QueryText;
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
            ResultsUpdated?.Invoke(this, EventArgs.Empty);
        }
    }

    // ── Reset ─────────────────────────────────────────────────────────────────

    public void Clear()
    {
        QueryText = "SELECT * FROM ";
        ErrorMessage = null;
        Summary = "";
        HasRun = false;
        Results.Clear();
        ColumnNames.Clear();
        TableNames.Clear();
        _shadowDbPath = null;
    }
}
