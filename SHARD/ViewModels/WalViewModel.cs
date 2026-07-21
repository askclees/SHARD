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
            SelectedFrameComparison = value is not null ? BuildComparison(value.Frame) : null;
        }
    }

    private PageViewModel? _selectedFrameDetail;
    public PageViewModel? SelectedFrameDetail
    {
        get => _selectedFrameDetail;
        private set => this.RaiseAndSetIfChanged(ref _selectedFrameDetail, value);
    }

    private WalPageComparisonViewModel? _selectedFrameComparison;
    public WalPageComparisonViewModel? SelectedFrameComparison
    {
        get => _selectedFrameComparison;
        private set
        {
            this.RaiseAndSetIfChanged(ref _selectedFrameComparison, value);
            this.RaisePropertyChanged(nameof(HasComparison));
        }
    }
    public bool HasComparison => SelectedFrameComparison is not null;

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

        for (int i = 0; i < walFile.Frames.Count; i++)
            Frames.Add(new WalFrameEntryViewModel(walFile.Frames[i], i + 1));
    }

    private WalPageComparisonViewModel? BuildComparison(WalFrame frame)
    {
        if (frame.Page is not TableBTreeLeafPage walPage)
            return null;

        var previousFrame = _walFile.GetPreviousFrame(frame);
        if (previousFrame?.Page is TableBTreeLeafPage previousWalPage)
        {
            int prevIndex = _walFile.Frames.IndexOf(previousFrame) + 1;
            return new WalPageComparisonViewModel(
                previousWalPage.Compare(walPage),
                $"Changes vs. frame {prevIndex}");
        }

        if (frame.Header.PageNumber > _database.PageCount)
            return null;

        var dbPage = _database.ReadPage(frame.Header.PageNumber);
        if (dbPage is not TableBTreeLeafPage dbLeafPage)
            return null;

        return new WalPageComparisonViewModel(
            dbLeafPage.Compare(walPage),
            "Changes vs. database page");
    }
}
