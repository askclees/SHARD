using System.Collections.ObjectModel;
using ReactiveUI;
using SHARD.Core;
using SHARD.Core.Pages;
using SHARD.Core.WAL;

namespace SHARD.ViewModels;

public sealed class WalViewModel : ViewModelBase
{
    private readonly WalFile _walFile;
    private readonly SqliteForensicDatabase _database;

    public WalFile WalFile  => _walFile;
    public string WalPath   { get; }
    public string TabHeader => $"WAL ({Frames.Count} frames)";

    public ObservableCollection<InfoRow>               HeaderRows { get; } = [];
    public ObservableCollection<WalFrameEntryViewModel> Frames    { get; } = [];

    private WalFrameEntryViewModel? _selectedFrame;
    public WalFrameEntryViewModel? SelectedFrame
    {
        get => _selectedFrame;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedFrame, value);
            SelectedFrameDetail     = value is not null ? new PageViewModel(value.Frame.Page) : null;
            SelectedFrameComparison = value is not null ? BuildComparison(value.Frame, _walFile.Frames.IndexOf(value.Frame)) : null;
            RefreshTransactionView();
        }
    }

    private PageViewModel? _selectedFrameDetail;
    public PageViewModel? SelectedFrameDetail
    {
        get => _selectedFrameDetail;
        private set => this.RaiseAndSetIfChanged(ref _selectedFrameDetail, value);
    }

    private IWalPageComparisonViewModel? _selectedFrameComparison;
    public IWalPageComparisonViewModel? SelectedFrameComparison
    {
        get => _selectedFrameComparison;
        private set
        {
            this.RaiseAndSetIfChanged(ref _selectedFrameComparison, value);
            this.RaisePropertyChanged(nameof(HasComparison));
            this.RaisePropertyChanged(nameof(ShowSingleFrameView));
            this.RaisePropertyChanged(nameof(ShowNoComparisonPlaceholder));
        }
    }
    public bool HasComparison => SelectedFrameComparison is not null;

    /// <summary>Toggle for the Changes tab: aggregate every page touched by the selected
    /// frame's whole transaction, instead of just the selected frame's own page.</summary>
    private bool _showWholeTransaction;
    public bool ShowWholeTransaction
    {
        get => _showWholeTransaction;
        set
        {
            this.RaiseAndSetIfChanged(ref _showWholeTransaction, value);
            this.RaisePropertyChanged(nameof(ShowSingleFrameView));
            this.RaisePropertyChanged(nameof(ShowNoComparisonPlaceholder));
            RefreshTransactionView();
        }
    }

    /// <summary>True while a frame is selected and the per-frame (not whole-transaction) view should render.</summary>
    public bool ShowSingleFrameView => HasComparison && !ShowWholeTransaction;

    /// <summary>True when neither the single-frame nor the whole-transaction view has anything
    /// to show — the selected frame's own page type isn't comparable, and we're not in
    /// whole-transaction mode (which can still have something to show even then).</summary>
    public bool ShowNoComparisonPlaceholder => !HasComparison && !ShowWholeTransaction;

    public ObservableCollection<WalTransactionPageEntryViewModel> TransactionPages { get; } = [];
    public bool HasAnyTransactionChanges { get; private set; }

    public WalViewModel(string walPath, WalFile walFile, SqliteForensicDatabase database)
    {
        WalPath   = walPath;
        _walFile  = walFile;
        _database = database;

        var h = walFile.Header;
        HeaderRows.Add(new InfoRow("Magic Number",        $"0x{h.MagicNumber:X8}"));
        HeaderRows.Add(new InfoRow("File Format Version", $"{h.FileFormatVersion}"));
        HeaderRows.Add(new InfoRow("Database Page Size",  $"{h.DatabasePageSize:N0} bytes"));
        HeaderRows.Add(new InfoRow("Checkpoint Sequence", $"{h.CheckpointSequenceNumber}"));
        HeaderRows.Add(new InfoRow("Salt-1",              $"0x{h.VerificationData.Salt1:X8}"));
        HeaderRows.Add(new InfoRow("Salt-2",              $"0x{h.VerificationData.Salt2:X8}"));
        HeaderRows.Add(new InfoRow("Checksum-1",          $"0x{h.VerificationData.Checksum1:X8}"));
        HeaderRows.Add(new InfoRow("Checksum-2",          $"0x{h.VerificationData.Checksum2:X8}"));
        HeaderRows.Add(new InfoRow("Frame Count",         $"{walFile.Frames.Count}"));

        var pageTableMap = database.BuildPageTableMap();
        for (int i = 0; i < walFile.Frames.Count; i++)
            Frames.Add(new WalFrameEntryViewModel(walFile.Frames[i], i + 1, pageTableMap));
    }

    /// <summary>
    /// Compares <paramref name="frame"/>'s page against the last WAL write of that same page
    /// strictly before <paramref name="beforeIndex"/> — or, if there is none, the corresponding
    /// page in the main database. Used both for the single-frame view (beforeIndex = the
    /// frame's own index, i.e. "immediately before this frame") and the whole-transaction view
    /// (beforeIndex = the transaction's start index, i.e. "before this transaction began" — so a
    /// page written twice within the same transaction still diffs against its pre-transaction
    /// state, not just its own previous write).
    /// </summary>
    private IWalPageComparisonViewModel? BuildComparison(WalFrame frame, int beforeIndex)
    {
        var baselineFrame = _walFile.GetLastFrameForPage(frame.Header.PageNumber, beforeIndex);

        if (frame.Page is TableBTreeLeafPage walLeafPage)
        {
            if (baselineFrame?.Page is TableBTreeLeafPage baselineLeafPage)
            {
                int baselineFrameNumber = _walFile.Frames.IndexOf(baselineFrame) + 1;
                return new WalPageComparisonViewModel(
                    baselineLeafPage.Compare(walLeafPage),
                    $"Changes vs. frame {baselineFrameNumber}");
            }

            return BuildComparisonAgainstDatabase(frame, dbPage =>
                dbPage is TableBTreeLeafPage dbLeafPage
                    ? new WalPageComparisonViewModel(dbLeafPage.Compare(walLeafPage), "Changes vs. database page")
                    : null);
        }

        if (frame.Page is TableBTreeInteriorPage walInteriorPage)
        {
            if (baselineFrame?.Page is TableBTreeInteriorPage baselineInteriorPage)
            {
                int baselineFrameNumber = _walFile.Frames.IndexOf(baselineFrame) + 1;
                return new WalInteriorPageComparisonViewModel(
                    baselineInteriorPage.Compare(walInteriorPage),
                    $"Changes vs. frame {baselineFrameNumber}");
            }

            return BuildComparisonAgainstDatabase(frame, dbPage =>
                dbPage is TableBTreeInteriorPage dbInteriorPage
                    ? new WalInteriorPageComparisonViewModel(dbInteriorPage.Compare(walInteriorPage), "Changes vs. database page")
                    : null);
        }

        // TODO: index leaf/interior, overflow, and freelist pages still have no Compare()
        // implementation, so they fall back to "No comparison available for this page type"
        // here — see https://github.com/askclees/SHARD/issues/23.
        return null;
    }

    private IWalPageComparisonViewModel? BuildComparisonAgainstDatabase(
        WalFrame frame, Func<SqlitePage, IWalPageComparisonViewModel?> compareAgainstDbPage)
    {
        if (frame.Header.PageNumber > _database.PageCount)
            return null;

        return compareAgainstDbPage(_database.ReadPage(frame.Header.PageNumber));
    }

    private void RefreshTransactionView()
    {
        TransactionPages.Clear();
        HasAnyTransactionChanges = false;

        if (ShowWholeTransaction && SelectedFrame is not null)
        {
            var frame = SelectedFrame.Frame;
            int transactionStart = _walFile.GetTransactionStartIndex(frame);
            var transactionFrames = _walFile.GetTransactionFrames(frame);
            var pageTableMap = _database.BuildPageTableMap();

            // A page can (rarely) be written more than once within the same transaction —
            // only its last write within the transaction reflects its state at commit time.
            var lastFramePerPage = new Dictionary<uint, WalFrame>();
            foreach (var f in transactionFrames)
                lastFramePerPage[f.Header.PageNumber] = f;

            foreach (var f in lastFramePerPage.Values.OrderBy(f => f.Header.PageNumber))
            {
                pageTableMap.TryGetValue(f.Header.PageNumber, out var tableName);
                var comparison = BuildComparison(f, transactionStart);
                TransactionPages.Add(new WalTransactionPageEntryViewModel(f.Header.PageNumber, tableName, comparison));
                if (comparison?.HasAnyChanges == true) HasAnyTransactionChanges = true;
            }
        }

        this.RaisePropertyChanged(nameof(HasAnyTransactionChanges));
    }
}
