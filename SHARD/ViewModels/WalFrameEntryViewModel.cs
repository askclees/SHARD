using Avalonia.Media;
using SHARD.Core.WAL;

namespace SHARD.ViewModels;

public sealed class WalFrameEntryViewModel
{
    public int      FrameIndex { get; }
    public uint     PageNumber { get; }
    public string   TypeLabel  { get; }
    public IBrush   TypeBrush  { get; }
    public bool     IsCommit   { get; }
    public WalFrame Frame      { get; }

    public WalFrameEntryViewModel(WalFrame frame, int index)
    {
        FrameIndex = index;
        PageNumber = frame.Header.PageNumber;
        TypeLabel  = frame.Page.PageType.ToString();
        TypeBrush  = PageTypeBrushes.For(frame.Page.PageType);
        IsCommit   = frame.Header.SizeOfDatabaseInPages > 0;
        Frame      = frame;
    }
}
