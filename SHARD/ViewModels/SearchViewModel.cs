using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Input;
using ReactiveUI;
using SHARD.Controls;
using SHARD.Core.Pages;
using SHARD.Core.Schema;

namespace SHARD.ViewModels;

public sealed class SearchViewModel : ViewModelBase
{
    private readonly IReadOnlyList<PageListEntryViewModel> _pages;
    private readonly Func<uint, Core.Pages.SqlitePage?> _readPage;
    private readonly Func<uint, string?> _getTableName;
    private readonly Func<string, TableSchema?> _getTableSchema;

    // ── Input ─────────────────────────────────────────────────────────────────

    private string _pattern = "";
    public string Pattern
    {
        get => _pattern;
        set => this.RaiseAndSetIfChanged(ref _pattern, value);
    }

    // ── State ─────────────────────────────────────────────────────────────────

    private string? _errorMessage;
    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => this.RaiseAndSetIfChanged(ref _errorMessage, value);
    }

    private bool _hasSearched;
    public bool HasSearched
    {
        get => _hasSearched;
        private set
        {
            this.RaiseAndSetIfChanged(ref _hasSearched, value);
            this.RaisePropertyChanged(nameof(ShowPlaceholder));
            this.RaisePropertyChanged(nameof(ShowNoResults));
        }
    }

    private bool _hasResults;
    public bool HasResults
    {
        get => _hasResults;
        private set
        {
            this.RaiseAndSetIfChanged(ref _hasResults, value);
            this.RaisePropertyChanged(nameof(ShowNoResults));
        }
    }

    public bool ShowPlaceholder => !_hasSearched;
    public bool ShowNoResults   => _hasSearched && !_hasResults;

    private string _summary = "";
    public string Summary
    {
        get => _summary;
        private set => this.RaiseAndSetIfChanged(ref _summary, value);
    }

    // ── Results ───────────────────────────────────────────────────────────────

    public ObservableCollection<SearchPageGroupViewModel> Results { get; } = [];

    private SearchPageGroupViewModel? _selectedGroup;
    public SearchPageGroupViewModel? SelectedGroup
    {
        get => _selectedGroup;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedGroup, value);
            this.RaisePropertyChanged(nameof(SelectedPageBytes));
            this.RaisePropertyChanged(nameof(SelectedHighlights));
        }
    }

    public byte[]?                     SelectedPageBytes  => _selectedGroup?.PageBytes;
    public IReadOnlyList<HexHighlight>? SelectedHighlights => _selectedGroup?.Highlights;

    // ── Command ───────────────────────────────────────────────────────────────

    public ICommand SearchCommand { get; }

    // ── Constructor ───────────────────────────────────────────────────────────

    public SearchViewModel(
        IReadOnlyList<PageListEntryViewModel> pages,
        Func<uint, Core.Pages.SqlitePage?> readPage,
        Func<uint, string?> getTableName,
        Func<string, TableSchema?> getTableSchema)
    {
        _pages          = pages;
        _readPage       = readPage;
        _getTableName   = getTableName;
        _getTableSchema = getTableSchema;
        SearchCommand   = ReactiveCommand.Create(RunSearch);
    }

    // ── Search ────────────────────────────────────────────────────────────────

    private void RunSearch()
    {
        Results.Clear();
        SelectedGroup = null;
        ErrorMessage  = null;

        if (string.IsNullOrWhiteSpace(_pattern))
        {
            HasSearched = false;
            HasResults  = false;
            Summary     = "";
            return;
        }

        Regex regex;
        try
        {
            regex = new Regex(_pattern, RegexOptions.Compiled);
        }
        catch (ArgumentException ex)
        {
            ErrorMessage = $"Invalid pattern: {ex.Message}";
            HasSearched  = true;
            HasResults   = false;
            Summary      = "";
            return;
        }

        foreach (var pageVm in _pages)
        {
            var page = _readPage(pageVm.PageNumber);
            if (page is null) continue;
            var data = page.Data;
            if (data is not { Length: > 0 }) continue;

            // Latin-1 gives a 1:1 byte↔char mapping, so match indices == byte offsets
            var text    = Encoding.Latin1.GetString(data);
            var matches = regex.Matches(text);
            if (matches.Count == 0) continue;

            var tableName = _getTableName(pageVm.PageNumber);
            var schema    = tableName is not null ? _getTableSchema(tableName) : null;
            var tlp       = page as TableBTreeLeafPage;

            var hits = new List<SearchHitViewModel>(matches.Count);
            foreach (System.Text.RegularExpressions.Match m in matches)
            {
                string? context = null;
                if (tlp is not null)
                {
                    var hit = tlp.FindHitContext(m.Index);
                    if (hit.HasValue)
                    {
                        var (rowId, fieldIndex) = hit.Value;
                        string fieldLabel = fieldIndex.HasValue
                            ? (schema is not null && fieldIndex.Value < schema.Columns.Count
                                ? schema.Columns[fieldIndex.Value].Name
                                : $"field[{fieldIndex.Value}]")
                            : "header";
                        context = $"Row {rowId} · {fieldLabel}";
                    }
                }
                hits.Add(new SearchHitViewModel(m.Index, m.Length, data, context));
            }

            Results.Add(new SearchPageGroupViewModel(pageVm.PageNumber, data, hits, tableName));
        }

        HasSearched = true;
        HasResults  = Results.Count > 0;

        if (HasResults)
        {
            int totalHits = Results.Sum(g => g.Hits.Count);
            Summary = $"{totalHits} match{(totalHits == 1 ? "" : "es")} across {Results.Count} page{(Results.Count == 1 ? "" : "s")}";
        }
        else
        {
            Summary = "";
        }
    }

    // ── Reset ─────────────────────────────────────────────────────────────────

    public void Clear()
    {
        Results.Clear();
        SelectedGroup = null;
        ErrorMessage  = null;
        HasSearched   = false;
        HasResults    = false;
        Summary       = "";
    }
}
