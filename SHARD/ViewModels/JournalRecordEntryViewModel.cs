using Avalonia.Media;
using SHARD.Core.Enums;
using SHARD.Core.Journal;

namespace SHARD.ViewModels;

public sealed class JournalRecordEntryViewModel
{
    public int    RecordIndex  { get; }
    public int    SegmentIndex { get; }
    public uint   PageNumber   { get; }
    public string TypeLabel    { get; }
    public IBrush TypeBrush    { get; }
    public string TableName    { get; }
    public bool   HasTable     { get; }
    // blank for Valid records, so only records SQLite would not play back stand out
    public string StatusLabel    { get; }
    public bool   HasStatusLabel { get; }
    public double RowOpacity     { get; }
    public RollbackJournalPageRecord Record  { get; }
    public RollbackJournalSegment    Segment { get; }

    public JournalRecordEntryViewModel(RollbackJournalPageRecord record, RollbackJournalSegment segment,
        int index, int segmentIndex, IReadOnlyDictionary<uint, string> pageTableMap)
    {
        RecordIndex  = index;
        SegmentIndex = segmentIndex;
        PageNumber   = record.PageNumber;
        TypeLabel    = record.Page.PageType.ToString();
        TypeBrush    = PageTypeBrushes.For(record.Page.PageType);
        TableName    = pageTableMap.TryGetValue(record.PageNumber, out var name) ? name : string.Empty;
        HasTable     = TableName.Length > 0;
        StatusLabel  = ShortStatusLabel(record.Status);
        HasStatusLabel = StatusLabel.Length > 0;
        RowOpacity   = record.Status == JournalRecordStatus.Valid ? 1.0 : 0.55;
        Record       = record;
        Segment      = segment;
    }

    public static string ShortStatusLabel(JournalRecordStatus status) => status switch
    {
        JournalRecordStatus.Valid                => string.Empty,
        JournalRecordStatus.AfterPlaybackStopped => "AFTER STOP",
        JournalRecordStatus.Stale                => "STALE",
        JournalRecordStatus.InvalidPageNumber    => "BAD PAGE#",
        _                                        => status.ToString(),
    };
}
