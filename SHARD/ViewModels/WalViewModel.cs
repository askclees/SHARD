using System.Collections.ObjectModel;
using ReactiveUI;
using SHARD.Core.WAL;

namespace SHARD.ViewModels;

public sealed class WalViewModel : ViewModelBase
{
    public string WalPath  { get; }
    public string TabHeader => $"WAL ({Frames.Count} frames)";

    public ObservableCollection<InfoRow>              HeaderRows { get; } = [];
    public ObservableCollection<WalFrameEntryViewModel> Frames   { get; } = [];

    private WalFrameEntryViewModel? _selectedFrame;
    public WalFrameEntryViewModel? SelectedFrame
    {
        get => _selectedFrame;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedFrame, value);
            SelectedFrameDetail = value is not null
                ? new PageViewModel(value.Frame.Page)
                : null;
        }
    }

    private PageViewModel? _selectedFrameDetail;
    public PageViewModel? SelectedFrameDetail
    {
        get => _selectedFrameDetail;
        private set => this.RaiseAndSetIfChanged(ref _selectedFrameDetail, value);
    }

    public WalViewModel(string walPath, WalFile walFile)
    {
        WalPath = walPath;

        var h = walFile.Header;
        HeaderRows.Add(new InfoRow("Magic Number",         $"0x{h.MagicNumber:X8}"));
        HeaderRows.Add(new InfoRow("File Format Version",  $"{h.FileFormatVersion}"));
        HeaderRows.Add(new InfoRow("Database Page Size",   $"{h.DatabasePageSize:N0} bytes"));
        HeaderRows.Add(new InfoRow("Checkpoint Sequence",  $"{h.CheckpointSequenceNumber}"));
        HeaderRows.Add(new InfoRow("Salt-1",               $"0x{h.VerificationData.Salt1:X8}"));
        HeaderRows.Add(new InfoRow("Salt-2",               $"0x{h.VerificationData.Salt2:X8}"));
        HeaderRows.Add(new InfoRow("Checksum-1",           $"0x{h.VerificationData.Checksum1:X8}"));
        HeaderRows.Add(new InfoRow("Checksum-2",           $"0x{h.VerificationData.Checksum2:X8}"));
        HeaderRows.Add(new InfoRow("Frame Count",          $"{walFile.Frames.Count}"));

        for (int i = 0; i < walFile.Frames.Count; i++)
            Frames.Add(new WalFrameEntryViewModel(walFile.Frames[i], i + 1));
    }
}
