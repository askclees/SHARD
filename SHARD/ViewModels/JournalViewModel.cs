using System.Collections.ObjectModel;
using ReactiveUI;
using SHARD.Core;
using SHARD.Core.Enums;
using SHARD.Core.Journal;
using SHARD.Core.Pages;

namespace SHARD.ViewModels;

public sealed class JournalViewModel : ViewModelBase
{
    private readonly RollbackJournalFile _journalFile;
    private readonly SqliteForensicDatabase _database;
    private readonly IReadOnlyDictionary<uint, string> _pageTableMap;

    public RollbackJournalFile JournalFile => _journalFile;
    public string JournalPath { get; }
    public string TabHeader   => $"Journal ({Records.Count} records)";

    public ObservableCollection<InfoRow>                     HeaderRows { get; } = [];
    public ObservableCollection<JournalRecordEntryViewModel> Records    { get; } = [];

    private JournalRecordEntryViewModel? _selectedRecord;
    public JournalRecordEntryViewModel? SelectedRecord
    {
        get => _selectedRecord;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedRecord, value);
            SelectedRecordDetail     = value is not null ? new PageViewModel(value.Record.Page) : null;
            SelectedRecordComparison = value is not null ? BuildComparison(value.Record) : null;
            SelectedRecordRows       = value is not null ? BuildRecordRows(value) : [];
        }
    }

    private PageViewModel? _selectedRecordDetail;
    public PageViewModel? SelectedRecordDetail
    {
        get => _selectedRecordDetail;
        private set => this.RaiseAndSetIfChanged(ref _selectedRecordDetail, value);
    }

    private IReadOnlyList<InfoRow> _selectedRecordRows = [];
    /// <summary>Journal-level facts about the selected record: status, checksums, nonces, offsets.</summary>
    public IReadOnlyList<InfoRow> SelectedRecordRows
    {
        get => _selectedRecordRows;
        private set => this.RaiseAndSetIfChanged(ref _selectedRecordRows, value);
    }

    private PageComparisonViewModel? _selectedRecordComparison;
    public PageComparisonViewModel? SelectedRecordComparison
    {
        get => _selectedRecordComparison;
        private set
        {
            this.RaiseAndSetIfChanged(ref _selectedRecordComparison, value);
            this.RaisePropertyChanged(nameof(HasComparison));
        }
    }
    public bool HasComparison => SelectedRecordComparison is not null;

    public JournalViewModel(string journalPath, RollbackJournalFile journalFile, SqliteForensicDatabase database)
    {
        JournalPath  = journalPath;
        _journalFile = journalFile;
        _database    = database;

        _pageTableMap = database.BuildPageTableMap();
        int index = 1;
        for (int s = 0; s < journalFile.Segments.Count; s++)
        {
            var segment = journalFile.Segments[s];
            foreach (var record in segment.PageRecords)
                Records.Add(new JournalRecordEntryViewModel(record, segment, index++, s + 1, _pageTableMap));
        }

        var first = journalFile.Segments[0].Header;
        int validCount = Records.Count(r => r.Record.Status == JournalRecordStatus.Valid);
        var stopRecord = Records.FirstOrDefault(r => r.Record.Status != JournalRecordStatus.Valid);

        HeaderRows.Add(new InfoRow("Segments",              $"{journalFile.Segments.Count}"));
        HeaderRows.Add(new InfoRow("Page Size",             $"{first.SizeOfJournalPages:N0} bytes"));
        HeaderRows.Add(new InfoRow("Sector Size",           $"{first.SizeOfDiskSector:N0} bytes"));
        HeaderRows.Add(new InfoRow("Initial Database Size", $"{first.DatabaseSize:N0} pages"));
        HeaderRows.Add(new InfoRow("First Segment Nonce",   $"0x{first.Nonce:X8}"));
        HeaderRows.Add(new InfoRow("Records",               $"{Records.Count:N0} ({validCount:N0} valid)"));
        HeaderRows.Add(new InfoRow("Playback Stops At",
            stopRecord is null
                ? "— (every record would be played back)"
                : $"record {stopRecord.RecordIndex} (offset {stopRecord.Record.FileOffset:N0}, {stopRecord.Record.Status})"));
        if (journalFile.StopReason is not null)
            HeaderRows.Add(new InfoRow("Parsing Stopped", journalFile.StopReason));
    }

    private static IReadOnlyList<InfoRow> BuildRecordRows(JournalRecordEntryViewModel entry)
    {
        var record = entry.Record;
        return
        [
            new InfoRow("Status",            $"{record.Status} — {DescribeStatus(record.Status)}"),
            new InfoRow("Segment",           $"{entry.SegmentIndex} (starts at offset {entry.Segment.SegmentStart:N0})"),
            new InfoRow("File Offset",       $"{record.FileOffset:N0} (0x{record.FileOffset:X})"),
            new InfoRow("Page Number",       $"{record.PageNumber}"),
            new InfoRow("Stored Checksum",   $"0x{record.Checksum:X8}"),
            new InfoRow("Computed Checksum", $"0x{record.ComputedChecksum:X8}{(record.IsChecksumValid ? "  ✓ match" : "  ✗ mismatch")}"),
            new InfoRow("Segment Nonce",     $"0x{entry.Segment.Header.Nonce:X8}"),
            new InfoRow("Implied Nonce",     $"0x{record.ImpliedNonce:X8}"),
        ];
    }

    private static string DescribeStatus(JournalRecordStatus status) => status switch
    {
        JournalRecordStatus.Valid                => "SQLite would restore this page on rollback",
        JournalRecordStatus.AfterPlaybackStopped => "checksum matches, but an earlier record stopped playback",
        JournalRecordStatus.Stale                => "checksum does not match this segment's nonce (left over from an earlier transaction?)",
        JournalRecordStatus.InvalidPageNumber    => "page 0 or the lock-byte page, which SQLite never journals",
        _                                        => string.Empty,
    };

    private PageComparisonViewModel? BuildComparison(RollbackJournalPageRecord record)
    {
        if (record.Page is not TableBTreeLeafPage journalPage)
            return null;

        if (record.PageNumber == 0 || record.PageNumber > _database.PageCount)
            return null;

        SqlitePage dbPage;
        try { dbPage = _database.ReadPage(record.PageNumber); }
        catch { return null; }
        if (dbPage is not TableBTreeLeafPage dbLeafPage)
            return null;

        // the journal holds the page as it was before the transaction, so compare journal → database
        // to show what the transaction changed
        return new PageComparisonViewModel(
            journalPage.Compare(dbLeafPage),
            "Changes since journal (journal page → current database page)",
            ColumnNamesFor(record.PageNumber));
    }

    /// <summary>Column names of the table that owns <paramref name="pageNumber"/>, or null if unknown.</summary>
    private IReadOnlyList<string>? ColumnNamesFor(uint pageNumber) =>
        _pageTableMap.TryGetValue(pageNumber, out var table)
            ? _database.GetTableSchema(table)?.Columns.Select(c => c.Name).ToList()
            : null;
}
