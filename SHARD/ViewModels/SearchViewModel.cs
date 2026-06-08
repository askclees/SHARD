using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Input;
using ReactiveUI;
using SHARD.Controls;

namespace SHARD.ViewModels;

public sealed class SearchViewModel : ViewModelBase
{
    private readonly IReadOnlyList<PageViewModel> _pages;

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

    public SearchViewModel(IReadOnlyList<PageViewModel> pages)
    {
        _pages        = pages;
        SearchCommand = ReactiveCommand.Create(RunSearch);
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
            var data = pageVm.PageBytes;
            if (data is not { Length: > 0 }) continue;

            // Latin-1 gives a 1:1 byte↔char mapping, so match indices == byte offsets
            var text    = Encoding.Latin1.GetString(data);
            var matches = regex.Matches(text);
            if (matches.Count == 0) continue;

            var hits  = matches.Select(m => new SearchHitViewModel(m.Index, m.Length, data)).ToList();
            Results.Add(new SearchPageGroupViewModel(pageVm.PageNumber, data, hits));
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
