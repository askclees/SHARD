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
    public string   StatusLabel { get; }
    public bool     HasStatusLabel => StatusLabel.Length > 0;
    public string   StatusTip  { get; }
    public double   RowOpacity { get; }
    public string   TableName  { get; }
    public bool     HasTable   { get; }
    public WalFrame Frame      { get; }

    public WalFrameEntryViewModel(WalFrame frame, int index, IReadOnlyDictionary<uint, string> pageTableMap)
    {
        FrameIndex = index;
        PageNumber = frame.Header.PageNumber;
        TypeLabel  = frame.Page.PageType.ToString();
        TypeBrush  = PageTypeBrushes.For(frame.Page.PageType);
        IsCommit   = frame.Header.SizeOfDatabaseInPages > 0;
        (StatusLabel, StatusTip) = frame.Status switch
        {
            WalFrameStatus.Committed         => ("", "Committed: SQLite reads this frame."),
            WalFrameStatus.Uncommitted       => ("UNCOMMITTED", "Valid, but no commit frame follows it: the transaction never committed, so SQLite ignores it."),
            WalFrameStatus.InvalidChecksum   => ("BAD CHECKSUM", "The checksum doesn't continue the chain: SQLite stops reading the WAL here."),
            WalFrameStatus.InvalidPageNumber => ("BAD PAGE", "Page number 0: SQLite stops reading the WAL here."),
            WalFrameStatus.AfterInvalid      => ("IGNORED", "After the point where SQLite stops reading the WAL, so it is ignored."),
            WalFrameStatus.OldGeneration     => ("OLD", "From an earlier generation of the WAL (before it was last reset): history SQLite ignores."),
            _                                => ("", ""),
        };
        if (frame.ChecksumValid == false && frame.Status != WalFrameStatus.InvalidChecksum)
            StatusTip += " Its checksum doesn't chain from the previous frame.";
        RowOpacity = frame.Status == WalFrameStatus.Committed ? 1.0 : 0.6;
        Frame      = frame;
        TableName  = pageTableMap.TryGetValue(frame.Header.PageNumber, out var name) ? name : string.Empty;
        HasTable   = TableName.Length > 0;
    }
}
