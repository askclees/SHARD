using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using Avalonia.Media;
using ReactiveUI;
using SHARD.Controls;
using SHARD.Core;
using SHARD.Core.Enums;
using SHARD.Core.Pages;
using SHARD.Core.Records;
using SHARD.Core.Recovery;
using SHARD.Core.Schema;
using SHARD.Core.Shadow;
using SHARD.Core.WAL;

namespace SHARD.ViewModels;

public sealed class MainWindowViewModel : ViewModelBase
{
    // ── Loaded database ───────────────────────────────────────────────────
    private string? _currentFilePath;
    public string? CurrentFilePath => _currentFilePath;

    private SqliteForensicDatabase? _database;
    public SqliteForensicDatabase? Database
    {
        get => _database;
        private set => this.RaiseAndSetIfChanged(ref _database, value);
    }

    private Dictionary<uint, string>? _pageTableMap;

    // ── Open / empty state ────────────────────────────────────────────────
    private bool _hasDatabase;
    public bool HasDatabase
    {
        get => _hasDatabase;
        private set
        {
            this.RaiseAndSetIfChanged(ref _hasDatabase, value);
            this.RaisePropertyChanged(nameof(HasNoDatabase));
        }
    }
    public bool HasNoDatabase => !HasDatabase;

    // ── Page list (left panel) ────────────────────────────────────────────
    public ObservableCollection<PageListEntryViewModel> Pages         { get; } = [];
    public ObservableCollection<PageListEntryViewModel> FilteredPages { get; } = [];

    // ── Page filters ──────────────────────────────────────────────────────
    private bool _filterHasUnallocated;
    public bool FilterHasUnallocated
    {
        get => _filterHasUnallocated;
        set { this.RaiseAndSetIfChanged(ref _filterHasUnallocated, value); RebuildFilteredPages(); }
    }

    private bool _filterMinSizeEnabled;
    public bool FilterMinSizeEnabled
    {
        get => _filterMinSizeEnabled;
        set { this.RaiseAndSetIfChanged(ref _filterMinSizeEnabled, value); RebuildFilteredPages(); }
    }

    private int _filterMinSize = 1;
    public int FilterMinSize
    {
        get => _filterMinSize;
        set { this.RaiseAndSetIfChanged(ref _filterMinSize, value); if (_filterMinSizeEnabled) RebuildFilteredPages(); }
    }

    private bool _filterMinNonZeroEnabled;
    public bool FilterMinNonZeroEnabled
    {
        get => _filterMinNonZeroEnabled;
        set { this.RaiseAndSetIfChanged(ref _filterMinNonZeroEnabled, value); RebuildFilteredPages(); }
    }

    private int _filterMinNonZero = 1;
    public int FilterMinNonZero
    {
        get => _filterMinNonZero;
        set { this.RaiseAndSetIfChanged(ref _filterMinNonZero, value); if (_filterMinNonZeroEnabled) RebuildFilteredPages(); }
    }

    private bool _filterHasDeletedPointers;
    public bool FilterHasDeletedPointers
    {
        get => _filterHasDeletedPointers;
        set { this.RaiseAndSetIfChanged(ref _filterHasDeletedPointers, value); RebuildFilteredPages(); }
    }

    private bool _filterHasDeletedRecords;
    public bool FilterHasDeletedRecords
    {
        get => _filterHasDeletedRecords;
        set { this.RaiseAndSetIfChanged(ref _filterHasDeletedRecords, value); RebuildFilteredPages(); }
    }

    private bool _useOrLogic;
    public bool UseOrLogic
    {
        get => _useOrLogic;
        set
        {
            this.RaiseAndSetIfChanged(ref _useOrLogic, value);
            this.RaisePropertyChanged(nameof(LogicModeLabel));
            RebuildFilteredPages();
        }
    }
    public string LogicModeLabel => UseOrLogic ? "OR" : "AND";

    private string _filterCountLabel = "";
    public string FilterCountLabel
    {
        get => _filterCountLabel;
        private set => this.RaiseAndSetIfChanged(ref _filterCountLabel, value);
    }

    private bool RegionMatchesFilter(int size, int nonZeroBytes)
    {
        var results = new List<bool>();
        if (FilterHasUnallocated)    results.Add(size > 0);
        if (FilterMinSizeEnabled)    results.Add(size >= FilterMinSize);
        if (FilterMinNonZeroEnabled) results.Add(nonZeroBytes >= FilterMinNonZero);
        return UseOrLogic ? results.Any(r => r) : results.All(r => r);
    }

    private void RebuildFilteredPages()
    {
        FilteredPages.Clear();
        bool anyTypeSelected    = PageTypeFilters.Any(f => f.IsSelected);
        bool anyUnallocActive       = FilterHasUnallocated || FilterMinSizeEnabled || FilterMinNonZeroEnabled;
        bool deletedActive          = FilterHasDeletedPointers;
        bool deletedRecordsActive   = FilterHasDeletedRecords;
        bool hasTableFilter     = !string.IsNullOrEmpty(_filterTableName);

        foreach (var page in Pages)
        {
            if (anyTypeSelected && !PageTypeFilters.Any(f => f.IsSelected && f.PageType == page.PageType))
                continue;
            if (hasTableFilter && page.TableName != _filterTableName)
                continue;
            if (anyUnallocActive && !page.UnallocatedRegions.Any(r => RegionMatchesFilter(r.Size, r.NonZeroBytes)))
                continue;
            if (deletedActive && !page.HasDeletedPointers)
                continue;
            if (deletedRecordsActive && !page.HasDeletedRecords)
                continue;
            FilteredPages.Add(page);
        }

        FilterCountLabel = FilteredPages.Count == Pages.Count
            ? $"{Pages.Count} pages"
            : $"{FilteredPages.Count} of {Pages.Count} pages";

        RebuildFilteredUnallocatedSections();
    }

    public ObservableCollection<UnallocatedRegionSectionViewModel> FilteredUnallocatedSections { get; } = [];
    public bool HasFilteredUnallocatedSections => FilteredUnallocatedSections.Count > 0;
    public string FilteredUnallocatedTabHeader => FilteredUnallocatedSections.Count > 0
        ? $"Unallocated ({FilteredUnallocatedSections.Count})"
        : "Unallocated";

    // ── Page type filter ──────────────────────────────────────────────────
    public IReadOnlyList<PageTypeToggleViewModel> PageTypeFilters { get; } = [];

    // ── Table filter ──────────────────────────────────────────────────────
    private string? _filterTableName;
    public string? FilterTableName
    {
        get => _filterTableName;
        set
        {
            var actual = value == "All tables" ? null : value;
            this.RaiseAndSetIfChanged(ref _filterTableName, actual);
            RebuildFilteredPages();
        }
    }
    public ObservableCollection<string> AvailableTableNames { get; } = [];
    public bool HasAvailableTableNames => AvailableTableNames.Count > 0;

    private void RebuildFilteredUnallocatedSections()
    {
        FilteredUnallocatedSections.Clear();
        bool anyActive = FilterHasUnallocated || FilterMinSizeEnabled || FilterMinNonZeroEnabled;

        if (SelectedPageDetail is null) return;

        foreach (var section in SelectedPageDetail.UnallocatedRegionSections)
        {
            if (!anyActive || RegionMatchesFilter(section.Size, section.NonZeroBytes))
                FilteredUnallocatedSections.Add(section);
        }

        this.RaisePropertyChanged(nameof(HasFilteredUnallocatedSections));
        this.RaisePropertyChanged(nameof(FilteredUnallocatedTabHeader));
    }

    private void RefreshAvailableTableNames()
    {
        AvailableTableNames.Clear();
        AvailableTableNames.Add("All tables");
        foreach (var name in Pages.Select(p => p.TableName).Where(n => n != null).Distinct().OrderBy(n => n))
            AvailableTableNames.Add(name!);
        this.RaisePropertyChanged(nameof(HasAvailableTableNames));
    }

    private static PageListEntryViewModel MakePageListEntry(SqlitePage page, string? tableName = null)
    {
        var regions = new List<(int Size, int NonZeroBytes)>();
        if (page is TableBTreeLeafPage tlp)
        {
            foreach (var r in tlp.UnallocatedRegions)
                regions.Add((r.Size, r.NonZeroBytes));
        }
        bool hasDeletedPointers = page is BTreePage bp && bp.DeletedCellPointers.Count > 0;
        bool hasDeletedRecords  = page is TableBTreeLeafPage tlp2 && tlp2.DeletedCells.Count > 0;
        return new PageListEntryViewModel(page.PageNumber, page.PageType, tableName, regions, hasDeletedPointers, hasDeletedRecords);
    }

    // ── Selected page (left panel selection; right panel detail) ─────────
    private PageListEntryViewModel? _selectedPage;
    public PageListEntryViewModel? SelectedPage
    {
        get => _selectedPage;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedPage, value);
            SelectedPageDetail = value is not null && Database is not null
                ? new PageViewModel(Database.ReadPage(value.PageNumber))
                : null;
            LastRecoveryResult = null;
            this.RaisePropertyChanged(nameof(CanTryRecoverRecord));
        }
    }

    private PageViewModel? _selectedPageDetail;
    public PageViewModel? SelectedPageDetail
    {
        get => _selectedPageDetail;
        private set
        {
            this.RaiseAndSetIfChanged(ref _selectedPageDetail, value);
            RebuildFilteredUnallocatedSections();
        }
    }

    // ── Overview panel info rows ──────────────────────────────────────────
    public ObservableCollection<InfoRow> DatabaseInfoRows { get; } = [];

    // ── Raw bytes + highlights for the HexView ────────────────────────────
    private byte[] _headerBytes = [];
    public byte[] HeaderBytes
    {
        get => _headerBytes;
        private set => this.RaiseAndSetIfChanged(ref _headerBytes, value);
    }

    private IReadOnlyList<HexHighlight> _headerHighlights = [];
    public IReadOnlyList<HexHighlight> HeaderHighlights
    {
        get => _headerHighlights;
        private set => this.RaiseAndSetIfChanged(ref _headerHighlights, value);
    }

    // ── Schema (sqlite_master) rows + selection ───────────────────────────
    public ObservableCollection<SqliteMasterRow> SchemaRows { get; } = [];

    private SqliteMasterRow? _selectedSchemaRow;
    public SqliteMasterRow? SelectedSchemaRow
    {
        get => _selectedSchemaRow;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedSchemaRow, value);
            this.RaisePropertyChanged(nameof(HasSchemaSelection));
            if (value is not null && Database is not null)
            {
                SchemaPageBytes  = Database.ReadPage(value.PageNumber).Data;
                SchemaHighlights = [new HexHighlight(value.CellOffset, value.CellLength, Color.FromRgb(78, 201, 176), value.Name)];
            }
            else if (_selectedDeletedSchemaRow is null)
            {
                SchemaPageBytes  = [];
                SchemaHighlights = [];
            }
        }
    }

    // ── Deleted schema rows + selection ───────────────────────────────────
    public ObservableCollection<DeletedSchemaRowViewModel> DeletedSchemaRows { get; } = [];
    public bool HasDeletedSchemaRows => DeletedSchemaRows.Count > 0;
    public string DeletedSchemaHeader => DeletedSchemaRows.Count > 0
        ? $"Deleted Tables ({DeletedSchemaRows.Count})"
        : "Deleted Tables";

    private DeletedSchemaRowViewModel? _selectedDeletedSchemaRow;
    public DeletedSchemaRowViewModel? SelectedDeletedSchemaRow
    {
        get => _selectedDeletedSchemaRow;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedDeletedSchemaRow, value);
            this.RaisePropertyChanged(nameof(HasSchemaSelection));
            if (value is not null && Database is not null)
            {
                SchemaPageBytes  = Database.ReadPage(value.PageNumber).Data;
                SchemaHighlights = [new HexHighlight(value.CellOffset, value.CellLength, Color.FromRgb(220, 140, 40), value.Name ?? "deleted")];
                LoadRecoveredRecords(value);
            }
            else
            {
                if (_selectedSchemaRow is null)
                {
                    SchemaPageBytes  = [];
                    SchemaHighlights = [];
                }
                ClearRecoveredRecords();
            }
        }
    }

    public bool HasSchemaSelection => _selectedSchemaRow is not null || _selectedDeletedSchemaRow is not null;

    // ── Recovered records from a valid deleted table ───────────────────────
    public ObservableCollection<RecoveredDeletedRecordViewModel> RecoveredDeletedRecords { get; } = [];
    public bool HasRecoveredDeletedRecords => RecoveredDeletedRecords.Count > 0;
    public string RecoveredDeletedRecordsHeader => RecoveredDeletedRecords.Count > 0
        ? $"Records ({RecoveredDeletedRecords.Count})"
        : "Records";

    private void LoadRecoveredRecords(DeletedSchemaRowViewModel vm)
    {
        ClearRecoveredRecords();
        if (vm.RootPageStatus != RootPageStatus.Valid) return;
        if (!vm.RootPage.HasValue || Database is null) return;

        TableSchema? schema = vm.Sql is not null
            ? CreateTableParser.ExtractTableSchema(vm.Sql)
            : null;

        try
        {
            foreach (var row in Database.ReadTableRows(vm.RootPage.Value))
                RecoveredDeletedRecords.Add(new RecoveredDeletedRecordViewModel(row, schema));
        }
        catch { /* non-fatal — show whatever was read */ }

        this.RaisePropertyChanged(nameof(HasRecoveredDeletedRecords));
        this.RaisePropertyChanged(nameof(RecoveredDeletedRecordsHeader));
    }

    private void ClearRecoveredRecords()
    {
        RecoveredDeletedRecords.Clear();
        this.RaisePropertyChanged(nameof(HasRecoveredDeletedRecords));
        this.RaisePropertyChanged(nameof(RecoveredDeletedRecordsHeader));
    }

    private byte[] _schemaPageBytes = [];
    public byte[] SchemaPageBytes
    {
        get => _schemaPageBytes;
        private set => this.RaiseAndSetIfChanged(ref _schemaPageBytes, value);
    }

    private IReadOnlyList<HexHighlight> _schemaHighlights = [];
    public IReadOnlyList<HexHighlight> SchemaHighlights
    {
        get => _schemaHighlights;
        private set => this.RaiseAndSetIfChanged(ref _schemaHighlights, value);
    }

    // ── Search tab ────────────────────────────────────────────────────────────
    public SearchViewModel SearchTab { get; }

    // ── Query tab ─────────────────────────────────────────────────────────────
    public QueryViewModel QueryTab { get; }

    // ── WAL file ──────────────────────────────────────────────────────────
    private WalViewModel? _walTab;
    public WalViewModel? WalTab
    {
        get => _walTab;
        private set
        {
            this.RaiseAndSetIfChanged(ref _walTab, value);
            this.RaisePropertyChanged(nameof(HasWal));
        }
    }
    public bool HasWal => WalTab is not null;

    // ── Shadow project ───────────────────────────────────────────────────
    private ShadowProject? _project;
    public ShadowProject? Project
    {
        get => _project;
        private set
        {
            this.RaiseAndSetIfChanged(ref _project, value);
            this.RaisePropertyChanged(nameof(HasProject));
            this.RaisePropertyChanged(nameof(ProjectFolderPath));
            this.RaisePropertyChanged(nameof(CanTryRecoverRecord));
        }
    }
    public bool HasProject => Project is not null;
    public string? ProjectFolderPath => Project?.IsUnsaved == true ? "(unsaved)" : Project?.ProjectFolder;

    // ── Record recovery ────────────────────────────────────────────────────
    private int _selectedByteOffset = -1;
    public int SelectedByteOffset
    {
        get => _selectedByteOffset;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedByteOffset, value);
            this.RaisePropertyChanged(nameof(CanTryRecoverRecord));
        }
    }

    public bool CanTryRecoverRecord =>
        Project is not null &&
        SelectedByteOffset >= 0 &&
        SelectedPage?.PageType == PageType.BTreeLeafTable;

    public bool CanSaveRecoveryToProject =>
        HasProject &&
        LastRecoveryResult?.IsValid == true &&
        SelectedPage?.TableName is not null;

    private DeletedBTreeLeafCellResult? _lastRecoveryResult;
    public DeletedBTreeLeafCellResult? LastRecoveryResult
    {
        get => _lastRecoveryResult;
        private set
        {
            this.RaiseAndSetIfChanged(ref _lastRecoveryResult, value);
            this.RaisePropertyChanged(nameof(CanSaveRecoveryToProject));
        }
    }

    /// <summary>
    /// Returns the live cell whose byte range contains <paramref name="offset"/>,
    /// or null if the offset does not fall inside any cell on the current page.
    /// </summary>
    public BTreeLeafCell? FindLiveCellAtOffset(int offset)
    {
        if (SelectedPageDetail?.Page is not TableBTreeLeafPage leafPage) return null;
        return leafPage.Cells.FirstOrDefault(c =>
            offset >= c.PageOffset && offset < c.PageOffset + c.CellByteLengthOnPage);
    }

    /// <summary>
    /// Attempts to decode a deleted B-tree leaf record at the current cursor position.
    /// Returns an error string if preconditions are not met, or null on success.
    /// On success, <see cref="LastRecoveryResult"/> is populated.
    /// </summary>
    public string? TryRecoverRecordAtOffset()
    {
        if (SelectedPage?.PageType != PageType.BTreeLeafTable)
                                 return "Record recovery is only available on table leaf pages.";
        if (SelectedPageDetail is null || SelectedByteOffset < 0 || Database is null)
                                 return "No page or byte offset selected.";

        LastRecoveryResult = DeletedRecordParser.RecoverBTreeLeafRecord(
            SelectedPageDetail.PageBytes,
            SelectedByteOffset,
            Database.Header.TextEncoding,
            null);
        return null;
    }

    /// <summary>
    /// Saves the last successful recovery result to the shadow database.
    /// Returns true on success; sets <see cref="StatusText"/> in both cases.
    /// </summary>
    public bool SaveRecoveryToProject()
    {
        if (Project is null || LastRecoveryResult?.IsValid != true || Database is null) return false;
        string? tableName = SelectedPage?.TableName;
        if (tableName is null)
        {
            StatusText = "Cannot save: this page is not associated with a known table.";
            return false;
        }

        var schema = Database.GetTableSchema(tableName);
        if (schema is null)
        {
            StatusText = $"Cannot save: schema for table '{tableName}' could not be read.";
            return false;
        }

        try
        {
            Project.SaveRecoveredRecord(schema, LastRecoveryResult.Cell!, SelectedPage!.PageNumber, SelectedByteOffset);
            StatusText = $"Record saved — RowId {LastRecoveryResult.Cell!.RowId.Value} → " +
                         $"{ShadowDatabaseBuilder.RecoveredTablePrefix}{tableName}";
            return true;
        }
        catch (Exception ex)
        {
            StatusText = $"Save failed: {ex.Message}";
            return false;
        }
    }

    // ── Status bar ────────────────────────────────────────────────────────
    private string _statusText = "Open a SQLite database to begin.";
    public string StatusText
    {
        get => _statusText;
        private set => this.RaiseAndSetIfChanged(ref _statusText, value);
    }

    public MainWindowViewModel()
    {
        SearchTab = new SearchViewModel(
            Pages,
            pageNumber => Database?.ReadPage(pageNumber),
            pageNumber => _pageTableMap?.GetValueOrDefault(pageNumber),
            tableName => Database?.GetTableSchema(tableName));
        QueryTab  = new QueryViewModel();

        PageTypeFilters = new List<PageTypeToggleViewModel>
        {
            new(PageType.BTreeLeafTable,     RebuildFilteredPages),
            new(PageType.BTreeLeafIndex,     RebuildFilteredPages),
            new(PageType.BTreeInteriorTable, RebuildFilteredPages),
            new(PageType.BTreeInteriorIndex, RebuildFilteredPages),
            new(PageType.Overflow,           RebuildFilteredPages),
            new(PageType.FreelistTrunk,      RebuildFilteredPages),
            new(PageType.FreelistLeaf,       RebuildFilteredPages),
            new(PageType.Unknown,            RebuildFilteredPages),
        };
    }

    // ── Actions ───────────────────────────────────────────────────────────

    /// <summary>
    /// Load a SQLite file by path.  Called from the view after the file picker resolves.
    /// Populates <see cref="DatabaseInfoRows"/> and (once the forensic library is
    /// implemented) <see cref="Pages"/>.
    /// </summary>
    public void LoadFile(string path)
    {
        try
        {
            CloseFile();

            var info = new FileInfo(path);

            var db = SqliteForensicDatabase.Open(path);
            Database = db;
            _currentFilePath = path;
            _pageTableMap = db.BuildPageTableMap();

            var page1 = db.ReadPage(1);
            HeaderBytes      = page1.Data[..100];
            HeaderHighlights = BuildHeaderHighlights();

            var header = db.Header;

            // ── File info ────────────────────────────────────────────────────
            DatabaseInfoRows.Add(new InfoRow("File",                        info.Name));
            DatabaseInfoRows.Add(new InfoRow("Path",                        path));
            DatabaseInfoRows.Add(new InfoRow("Size",                        FormatBytes(info.Length)));

            // ── Header fields in byte-offset order ───────────────────────────
            // Offset 0
            DatabaseInfoRows.Add(new InfoRow("Magic (0)",                   header.Magic.TrimEnd('\0')));
            // Offset 16
            DatabaseInfoRows.Add(new InfoRow("Page Size (16)",              $"{header.PageSize:N0} bytes  (raw: {header.PageSizeRaw})"));
            // Offset 18
            DatabaseInfoRows.Add(new InfoRow("Write Version (18)",          $"{header.WriteVersion}  —  {header.WriteVersionName}"));
            // Offset 19
            DatabaseInfoRows.Add(new InfoRow("Read Version (19)",           $"{header.ReadVersion}"));
            // Offset 20
            DatabaseInfoRows.Add(new InfoRow("Reserved Per Page (20)",      $"{header.ReservedBytesPerPage} bytes"));
            // Offset 21
            DatabaseInfoRows.Add(new InfoRow("Max Payload Fraction (21)",   $"{header.MaxEmbeddedPayloadFraction}"));
            // Offset 22
            DatabaseInfoRows.Add(new InfoRow("Min Payload Fraction (22)",   $"{header.MinEmbeddedPayloadFraction}"));
            // Offset 23
            DatabaseInfoRows.Add(new InfoRow("Leaf Payload Fraction (23)",  $"{header.LeafPayloadFraction}"));
            // Offset 24
            DatabaseInfoRows.Add(new InfoRow("File Change Counter (24)",    $"{header.FileChangeCounter}"));
            // Offset 28
            DatabaseInfoRows.Add(new InfoRow("DB Size in Pages (28)",       $"{header.DatabaseSizeInPages:N0}"));
            // Offset 32
            DatabaseInfoRows.Add(new InfoRow("First Freelist Page (32)",    $"{header.FirstFreelistTrunkPage}"));
            // Offset 36
            DatabaseInfoRows.Add(new InfoRow("Total Freelist Pages (36)",   $"{header.TotalFreelistPages:N0}"));
            // Offset 40
            DatabaseInfoRows.Add(new InfoRow("Schema Cookie (40)",          $"{header.SchemaCookie}"));
            // Offset 44
            DatabaseInfoRows.Add(new InfoRow("Schema Format (44)",          $"{header.SchemaFormat}"));
            // Offset 48
            DatabaseInfoRows.Add(new InfoRow("Default Cache Size (48)",     $"{header.DefaultPageCacheSize:N0}"));
            // Offset 52
            DatabaseInfoRows.Add(new InfoRow("Largest Root Page (52)",      $"{header.LargestRootBTreePage}"));
            // Offset 56
            DatabaseInfoRows.Add(new InfoRow("Text Encoding (56)",          $"{header.TextEncoding}  —  {header.TextEncodingName}"));
            // Offset 60
            DatabaseInfoRows.Add(new InfoRow("User Version (60)",           $"{header.UserVersion}"));
            // Offset 64
            DatabaseInfoRows.Add(new InfoRow("Incremental Vacuum (64)",     $"{header.IncrementalVacuumMode}"));
            // Offset 68
            DatabaseInfoRows.Add(new InfoRow("Application ID (68)",         $"0x{header.ApplicationId:X8}"));
            // Offset 72–91: reserved (not shown)
            // Offset 92
            DatabaseInfoRows.Add(new InfoRow("Version Valid For (92)",      $"{header.VersionValidFor}"));
            // Offset 96
            DatabaseInfoRows.Add(new InfoRow("SQLite Version (96)",         $"{header.SqliteVersionNumber}  —  {FormatSqliteVersion(header.SqliteVersionNumber)}"));

            foreach (var row in db.ReadSqliteMaster())
                SchemaRows.Add(row);

            try
            {
                foreach (var deleted in db.ReadDeletedSqliteMaster())
                    DeletedSchemaRows.Add(new DeletedSchemaRowViewModel(deleted));
            }
            catch { /* non-fatal — proceed without deleted table data */ }
            this.RaisePropertyChanged(nameof(HasDeletedSchemaRows));
            this.RaisePropertyChanged(nameof(DeletedSchemaHeader));

            foreach (var page in db.ReadAllPages())
                Pages.Add(MakePageListEntry(page));
            RefreshAvailableTableNames();
            RebuildFilteredPages();

            HasDatabase = true;
            StatusText = $"{info.Name}  ·  {header.PageSize:N0} bytes/page  ·  {header.TextEncodingName}  ·  {db.PageCount:N0} pages";

            // Build shadow DB immediately so Query and recovery work without a saved project.
            try
            {
                var (project, warnings) = ShadowProject.CreateTemporary(_currentFilePath!, Database);
                Project = project;

                foreach (var deletedVm in DeletedSchemaRows.Where(d => d.RootPageStatus == RootPageStatus.Valid
                                                                     && d.RootPage.HasValue
                                                                     && d.Sql is not null))
                {
                    var schema = CreateTableParser.ExtractTableSchema(deletedVm.Sql!);
                    if (schema is null) continue;
                    try
                    {
                        var pageNums = Database.GetTreePageNumbers(deletedVm.RootPage!.Value).ToList();
                        project.AddDeletedTableRecords(schema, Database.ReadTableRows(deletedVm.RootPage!.Value));
                        project.TagDeletedTablePages(schema.TableName, pageNums);
                    }
                    catch { }
                }

                QueryTab.SetShadowDatabasePath(Project.ShadowDatabasePath);
                RefreshPagesFromShadowDatabase();
                if (warnings.Count > 0)
                {
                    string logPath = path + ".warnings.log";
                    File.WriteAllText(logPath, string.Join("\n\n", warnings));
                    StatusText += $"  ·  {warnings.Count} table(s) skipped";
                }
            }
            catch (Exception ex)
            {
                StatusText += $"  ·  Shadow DB failed: {ex.Message}";
            }

            string walPath = path + "-wal";
            if (File.Exists(walPath))
                LoadWalFile(walPath);
        }
        catch (InvalidDataException ex)
        {
            StatusText = $"Not a valid SQLite file: {ex.Message}";
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
        }
    }

    /// <summary>Close the current database and reset the UI state.</summary>
    public void CloseFile()
    {
        Database?.Dispose();
        Database = null;
        _currentFilePath = null;
        _pageTableMap = null;
        Pages.Clear();
        FilteredPages.Clear();
        FilterCountLabel = "";
        AvailableTableNames.Clear();
        FilterTableName = null;
        foreach (var f in PageTypeFilters) f.IsSelected = false;
        DatabaseInfoRows.Clear();
        SchemaRows.Clear();
        SelectedSchemaRow = null;
        DeletedSchemaRows.Clear();
        SelectedDeletedSchemaRow = null;
        this.RaisePropertyChanged(nameof(HasDeletedSchemaRows));
        this.RaisePropertyChanged(nameof(DeletedSchemaHeader));
        ClearRecoveredRecords();
        HeaderBytes      = [];
        HeaderHighlights = [];
        SelectedPage = null;
        HasDatabase  = false;
        WalTab       = null;
        _project?.Dispose();
        Project      = null;
        StatusText   = "Open a SQLite database to begin.";
        SearchTab.Clear();
        QueryTab.Clear();
    }

    /// <summary>
    /// Save the current temporary project to <paramref name="folderPath"/> on disk,
    /// writing the manifest and persisting the shadow database.
    /// </summary>
    public void SaveProject(string folderPath)
    {
        if (Project is null) return;
        try
        {
            Project.SaveTo(folderPath);
            this.RaisePropertyChanged(nameof(ProjectFolderPath));
            StatusText = $"Project saved to {folderPath}";
        }
        catch (Exception ex)
        {
            StatusText = $"Error saving project: {ex.Message}";
        }
    }

    /// <summary>
    /// Open an existing project folder: loads its evidence file (re-parsing it byte-level,
    /// same as <see cref="LoadFile"/>) and points the Query tab at the already-built shadow
    /// database, without rebuilding it.
    /// </summary>
    public void OpenProject(string projectFolder)
    {
        try
        {
            var project = ShadowProject.Open(projectFolder);
            LoadFile(project.EvidenceFilePath);

            if (Database is null)
            {
                StatusText = $"Error opening project: failed to load evidence file '{project.EvidenceFilePath}'.";
                return;
            }

            // Replace the temporary shadow project created by LoadFile with the saved one.
            _project?.Dispose();
            Project = project;
            QueryTab.SetShadowDatabasePath(project.ShadowDatabasePath);
            RefreshPagesFromShadowDatabase();
            StatusText = $"Project opened from {projectFolder}";
            SyncWalToProject();
        }
        catch (Exception ex)
        {
            StatusText = $"Error opening project: {ex.Message}";
        }
    }

    /// <summary>
    /// Replace the live-swept <see cref="Pages"/> list with the persisted, potentially more
    /// accurate classifications (e.g. overflow/freelist) recorded in the shadow database.
    /// </summary>
    private void RefreshPagesFromShadowDatabase()
    {
        if (Project is null) return;

        try
        {
            var pageTypes = Project.ReadPageTypes();
            Pages.Clear();
            foreach (var (pageNumber, type, tableName) in pageTypes)
            {
                var page = Database?.ReadPage(pageNumber);
                var entry = page is not null
                    ? MakePageListEntry(page, tableName)
                    : new PageListEntryViewModel(pageNumber, type, tableName);
                Pages.Add(entry);
            }
            RefreshAvailableTableNames();
            RebuildFilteredPages();
        }
        catch (Exception ex)
        {
            StatusText = $"Error reading persisted page classifications: {ex.Message}";
        }
    }

    public void LoadWalFile(string walPath)
    {
        if (Database is null) return;
        try
        {
            var wal = new WalFile(walPath, Database.Header.TextEncoding, Database.Header.ReservedBytesPerPage);
            WalTab = new WalViewModel(walPath, wal, Database);
            StatusText += $"  ·  WAL: {wal.Frames.Count} frames";

            // If a project is already open sync immediately; if not, CreateProject/OpenProject
            // will call SyncWalToProject once the project is set (WAL loads before the project
            // is created/opened in both flows).
            SyncWalToProject();
        }
        catch (Exception ex)
        {
            StatusText = $"WAL file could not be loaded: {ex.Message}";
        }
    }

    private void SyncWalToProject()
    {
        if (Project is null || WalTab is null || Database is null) return;
        try
        {
            int added = Project.SyncWalFramesToShadow(WalTab.WalFile, Database);
            if (added > 0)
                StatusText += $"  ·  {added} WAL record{(added == 1 ? "" : "s")} synced";
        }
        catch (Exception ex)
        {
            StatusText += $"  ·  WAL sync failed: {ex.Message}";
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024                => $"{bytes} B",
        < 1024 * 1024         => $"{bytes / 1024.0:F1} KB",
        < 1024 * 1024 * 1024  => $"{bytes / (1024.0 * 1024):F1} MB",
        _                     => $"{bytes / (1024.0 * 1024 * 1024):F2} GB",
    };

    /// <summary>
    /// Highlights for every field in the 100-byte SQLite database header,
    /// in byte-offset order. Colours are chosen to be distinct and group
    /// related fields visually.
    /// </summary>
    private static IReadOnlyList<HexHighlight> BuildHeaderHighlights() =>
    [
        // Offset  0 — Magic string
        new(  0, 16, Color.FromRgb( 86, 156, 214), "Magic"),
        // Offset 16 — Page size
        new( 16,  2, Color.FromRgb( 78, 201, 176), "Page Size"),
        // Offset 18 — Write version
        new( 18,  1, Color.FromRgb(220, 220, 170), "Write Version"),
        // Offset 19 — Read version
        new( 19,  1, Color.FromRgb(206, 145, 120), "Read Version"),
        // Offset 20 — Reserved bytes per page
        new( 20,  1, Color.FromRgb(155, 155, 155), "Reserved Per Page"),
        // Offset 21-23 — Payload fractions (fixed values; group with same colour)
        new( 21,  1, Color.FromRgb(106, 153, 85),  "Max Payload Fraction"),
        new( 22,  1, Color.FromRgb(106, 153, 85),  "Min Payload Fraction"),
        new( 23,  1, Color.FromRgb(106, 153, 85),  "Leaf Payload Fraction"),
        // Offset 24 — File change counter
        new( 24,  4, Color.FromRgb(255, 215,   0), "File Change Counter"),
        // Offset 28 — Database size in pages
        new( 28,  4, Color.FromRgb(218, 165,  32), "DB Size in Pages"),
        // Offset 32 — First freelist trunk page
        new( 32,  4, Color.FromRgb(205,  92,  92), "First Freelist Page"),
        // Offset 36 — Total freelist pages
        new( 36,  4, Color.FromRgb(178,  34,  34), "Total Freelist Pages"),
        // Offset 40 — Schema cookie
        new( 40,  4, Color.FromRgb(147, 112, 219), "Schema Cookie"),
        // Offset 44 — Schema format
        new( 44,  4, Color.FromRgb(123,  91, 196), "Schema Format"),
        // Offset 48 — Default page cache size
        new( 48,  4, Color.FromRgb(255, 160, 122), "Default Cache Size"),
        // Offset 52 — Largest root b-tree page
        new( 52,  4, Color.FromRgb(255, 127,  80), "Largest Root Page"),
        // Offset 56 — Text encoding
        new( 56,  4, Color.FromRgb( 79, 193, 255), "Text Encoding"),
        // Offset 60 — User version
        new( 60,  4, Color.FromRgb(  0, 191, 255), "User Version"),
        // Offset 64 — Incremental vacuum mode
        new( 64,  4, Color.FromRgb(255,  99,  71), "Incremental Vacuum"),
        // Offset 68 — Application ID
        new( 68,  4, Color.FromRgb(255, 140,   0), "Application ID"),
        // Offset 72-91 — Reserved (not highlighted)
        // Offset 92 — Version valid for
        new( 92,  4, Color.FromRgb(189, 183, 107), "Version Valid For"),
        // Offset 96 — SQLite version number
        new( 96,  4, Color.FromRgb(240, 230, 140), "SQLite Version"),
    ];

    private static string FormatSqliteVersion(uint v)
    {
        // e.g. 3046000 → "3.46.0"
        int major = (int)(v / 1_000_000);
        int minor = (int)(v % 1_000_000 / 1_000);
        int patch = (int)(v % 1_000);
        return $"{major}.{minor}.{patch}";
    }
}
