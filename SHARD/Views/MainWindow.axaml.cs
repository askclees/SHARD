using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using SHARD.Controls;
using SHARD.Core.Enums;
using SHARD.Core.Records;
using SHARD.Core.Schema;
using SHARD.ViewModels;

namespace SHARD.Views;

public partial class MainWindow : Window
{
    // SQLite file-type filter used by the open dialog
    private static readonly FilePickerFileType SqliteFilter = new("SQLite Database")
    {
        Patterns           = ["*.db", "*.sqlite", "*.sqlite3", "*.db3"],
        MimeTypes          = ["application/x-sqlite3", "application/vnd.sqlite3"],
        AppleUniformTypeIdentifiers = ["com.apple.sqlite3"],
    };

    private static readonly FilePickerFileType WalFilter = new("SQLite WAL File")
    {
        Patterns = ["*.db-wal", "*.sqlite-wal", "*.wal"],
    };

    public MainWindow()
    {
        InitializeComponent();

        // Wire up named controls
        this.FindControl<MenuItem>("MenuOpen")!.Click          += OnOpenClick;
        this.FindControl<MenuItem>("MenuClose")!.Click         += OnCloseClick;
        this.FindControl<MenuItem>("MenuSaveProject")!.Click   += OnSaveProjectClick;
        this.FindControl<MenuItem>("MenuOpenProject")!.Click   += OnOpenProjectClick;
        this.FindControl<MenuItem>("MenuLoadWal")!.Click       += OnLoadWalClick;
        this.FindControl<MenuItem>("MenuExit")!.Click          += (_, _) => Close();
        this.FindControl<Button>("BtnOpen")!.Click             += OnOpenClick;

        // Wire PageHexView cursor offset → ViewModel
        this.FindControl<HexView>("PageHexView")!.PropertyChanged += (_, e) =>
        {
            if (e.Property == HexView.CursorOffsetProperty && DataContext is MainWindowViewModel vm)
                vm.SelectedByteOffset = (int)(e.NewValue ?? -1);
        };

        // Drag-and-drop
        AddHandler(DragDrop.DropEvent,     OnDrop);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);

        // Scroll hex view to selected schema row after bindings settle
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
            {
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(MainWindowViewModel.SelectedSchemaRow) && vm.SelectedSchemaRow is { } row)
                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                            this.FindControl<HexView>("SchemaHexView")?.ScrollToByteOffset(row.CellOffset));
                };

                vm.QueryTab.ResultsUpdated += (_, _) => RebuildQueryResultColumns(vm.QueryTab);
            }
        };
    }

    // ── Query ────────────────────────────────────────────────────────────────

    private void RebuildQueryResultColumns(QueryViewModel queryTab)
    {
        var grid = this.FindControl<DataGrid>("ResultsGrid");
        if (grid is null) return;

        grid.Columns.Clear();
        for (int i = 0; i < queryTab.ColumnNames.Count; i++)
        {
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = queryTab.ColumnNames[i],
                Binding = new Binding($"[{i}]"),
            });
        }
    }

    private void OnQueryBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        if (e.KeyModifiers == KeyModifiers.Shift)
        {
            // Insert a newline at the caret for multi-line SQL
            if (sender is TextBox tb)
            {
                int pos = tb.CaretIndex;
                tb.Text = (tb.Text ?? string.Empty).Insert(pos, "\n");
                tb.CaretIndex = pos + 1;
            }
        }
        else
        {
            Vm.QueryTab.RunQueryCommand.Execute(default).Subscribe();
        }
        e.Handled = true;
    }

    private async void OnExportCsvClick(object? sender, RoutedEventArgs e)
    {
        var file = await (TopLevel.GetTopLevel(this)?.StorageProvider.SaveFilePickerAsync(
            new FilePickerSaveOptions
            {
                Title = "Export Query Results as CSV",
                SuggestedFileName = "query_results.csv",
                FileTypeChoices = [new FilePickerFileType("CSV") { Patterns = ["*.csv"] }]
            }) ?? Task.FromResult<IStorageFile?>(null));

        if (file is null) return;

        string csv = Vm.QueryTab.BuildCsv();
        await using var stream = await file.OpenWriteAsync();
        await using var writer = new System.IO.StreamWriter(stream, System.Text.Encoding.UTF8);
        await writer.WriteAsync(csv);
    }

    private void OnTableDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: QueryTableViewModel table })
            Vm.QueryTab.RunQueryForTable(table.ActualName);
    }

    // ── File open ────────────────────────────────────────────────────────────

    private async void OnOpenClick(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title             = "Open SQLite Database",
            AllowMultiple     = false,
            FileTypeFilter    = [SqliteFilter, FilePickerFileTypes.All],
        });

        if (files is [var file])
            Vm.LoadFile(file.Path.LocalPath);
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) =>
        Vm.CloseFile();

    private async void OnLoadWalClick(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title          = "Open WAL File",
            AllowMultiple  = false,
            FileTypeFilter = [WalFilter, FilePickerFileTypes.All],
        });

        if (files is [var file])
            Vm.LoadWalFile(file.Path.LocalPath);
    }

    private async void OnSaveProjectClick(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title         = "Save Project To Folder",
            AllowMultiple = false,
        });

        if (folders is [var folder])
            Vm.SaveProject(folder.Path.LocalPath);
    }

    private async void OnOpenProjectClick(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title         = "Open Project Folder",
            AllowMultiple = false,
        });

        if (folders is [var folder])
            Vm.OpenProject(folder.Path.LocalPath);
    }

    // ── Drag-and-drop ────────────────────────────────────────────────────────

    private static void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.Data.Contains(DataFormats.Files)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        var file = e.Data.GetFiles()?.FirstOrDefault();
        if (file is not null)
            Vm.LoadFile(file.Path.LocalPath);
        e.Handled = true;
    }

    // ── Cell section expand → scroll hex ─────────────────────────────────────

    private void OnCellSectionExpanded(object? sender, RoutedEventArgs e)
    {
        if (sender is not Expander { DataContext: CellSectionViewModel vm }) return;
        var hexView = this.FindControl<HexView>("PageHexView");
        hexView?.ScrollToByteOffset(vm.ByteOffset);
        hexView?.SetCursorOffset(vm.ByteOffset);
    }

    private void OnFreeBlockSectionExpanded(object? sender, RoutedEventArgs e)
    {
        if (sender is not Expander { DataContext: FreeBlockSectionViewModel vm }) return;
        this.FindControl<HexView>("PageHexView")?.ScrollToByteOffset(vm.ByteOffset);
    }

    private void OnWalCellSectionExpanded(object? sender, RoutedEventArgs e)
    {
        if (sender is not Expander { DataContext: CellSectionViewModel vm }) return;
        var hexView = this.FindControl<HexView>("WalHexView");
        hexView?.ScrollToByteOffset(vm.ByteOffset);
        hexView?.SetCursorOffset(vm.ByteOffset);
    }

    private void OnWalFreeBlockSectionExpanded(object? sender, RoutedEventArgs e)
    {
        if (sender is not Expander { DataContext: FreeBlockSectionViewModel vm }) return;
        this.FindControl<HexView>("WalHexView")?.ScrollToByteOffset(vm.ByteOffset);
    }

    private void OnFreeBlockRecordClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: FreeBlockRecordEntry entry }) return;
        var hexView = this.FindControl<HexView>("PageHexView");
        hexView?.ScrollToByteOffset(entry.ByteOffset);
        hexView?.SetCursorOffset(entry.ByteOffset);
    }

    private void OnWalFreeBlockRecordClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: FreeBlockRecordEntry entry }) return;
        var hexView = this.FindControl<HexView>("WalHexView");
        hexView?.ScrollToByteOffset(entry.ByteOffset);
        hexView?.SetCursorOffset(entry.ByteOffset);
    }

    private void OnUnallocatedRegionSectionExpanded(object? sender, RoutedEventArgs e)
    {
        if (sender is not Expander { DataContext: UnallocatedRegionSectionViewModel vm }) return;
        this.FindControl<HexView>("PageHexView")?.ScrollToByteOffset(vm.ByteOffset);
    }

    private void OnWalUnallocatedRegionSectionExpanded(object? sender, RoutedEventArgs e)
    {
        if (sender is not Expander { DataContext: UnallocatedRegionSectionViewModel vm }) return;
        this.FindControl<HexView>("WalHexView")?.ScrollToByteOffset(vm.ByteOffset);
    }

    // ── Search ───────────────────────────────────────────────────────────────

    private void OnSearchBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            Vm.SearchTab.SearchCommand.Execute(null);
    }

    private void OnSearchGroupExpanded(object? sender, RoutedEventArgs e)
    {
        if (sender is not Expander { DataContext: SearchPageGroupViewModel group }) return;
        Vm.SearchTab.SelectedGroup = group;
    }

    private void OnSearchHitClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: SearchHitViewModel hit }) return;
        this.FindControl<HexView>("SearchHexView")?.ScrollToByteOffset(hit.Offset);
    }

    // ── Record recovery ──────────────────────────────────────────────────────

    private async void OnTryRecoverRecordClicked(object? sender, RoutedEventArgs e)
    {
        var overlap = Vm.FindLiveCellAtOffset(Vm.SelectedByteOffset);
        if (overlap is not null)
        {
            bool proceed = await ShowLiveRecordWarning(overlap);
            if (!proceed) return;
        }

        string? preconditionError = Vm.TryRecoverRecordAtOffset();
        if (preconditionError is not null)
        {
            var errDlg = new RecoveryResultWindow(new RecoveryResultViewModel
            {
                IsValid = false,
                Title   = "Cannot decode record",
                Errors  = [preconditionError],
            });
            await errDlg.ShowDialog(this);
            return;
        }

        var result = Vm.LastRecoveryResult!;

        TableSchema? schema = null;
        if (Vm.SelectedPage?.TableName is { } tableName && Vm.Database is not null)
            schema = Vm.Database.GetTableSchema(tableName);

        var vm = new RecoveryResultViewModel
        {
            IsValid  = result.IsValid,
            Title    = result.IsValid
                ? $"Valid record — RowId: {result.Cell!.RowId.Value}"
                : "Could not decode record at this location",
            Subtitle = result.IsValid
                ? $"Offset {Vm.SelectedByteOffset} on page {Vm.SelectedPage?.PageNumber}" +
                  (Vm.SelectedPage?.TableName is { } tn ? $", table: {tn}" : "")
                : $"Offset {Vm.SelectedByteOffset} on page {Vm.SelectedPage?.PageNumber}",
            Fields   = result.IsValid ? BuildFieldRows(result.Cell!, schema) : [],
            Errors   = result.ValidationErrors,
            CanAdd   = Vm.CanSaveRecoveryToProject,
        };

        bool save = await new RecoveryResultWindow(vm).ShowDialog<bool>(this);
        if (save) Vm.SaveRecoveryToProject();
    }

    private static List<RecoveryFieldRow> BuildFieldRows(BTreeLeafCell cell, TableSchema? schema)
    {
        var rows = new List<RecoveryFieldRow>();
        if (schema is not null)
        {
            for (int i = 0; i < schema.Columns.Count; i++)
            {
                var col = schema.Columns[i];
                string value = col.IsRowIdAlias
                    ? cell.RowId.Value.ToString()
                    : (i < cell.FieldValues.Count ? cell.FieldValues[i]?.Value?.ToString() : null) ?? "NULL";
                rows.Add(new RecoveryFieldRow(col.Name, value));
            }
        }
        else
        {
            rows.Add(new RecoveryFieldRow("RowId", cell.RowId.Value.ToString()));
            for (int i = 0; i < cell.FieldValues.Count; i++)
                rows.Add(new RecoveryFieldRow($"Field {i}", cell.FieldValues[i]?.Value?.ToString() ?? "NULL"));
        }
        return rows;
    }

    // ── Dialogs ───────────────────────────────────────────────────────────────

    private Task<bool> ShowLiveRecordWarning(BTreeLeafCell overlap)
    {
        var dlg = new Window
        {
            Title                  = "Offset inside live record",
            Width                  = 440,
            SizeToContent          = SizeToContent.Height,
            WindowStartupLocation  = WindowStartupLocation.CenterOwner,
            ShowInTaskbar          = false,
            CanResize              = false,
        };

        var text = new TextBlock
        {
            Text = $"The selected offset falls inside live record RowId {overlap.RowId.Value} " +
                   $"(bytes {overlap.PageOffset}–{overlap.PageOffset + overlap.CellByteLengthOnPage - 1}).\n\n" +
                   "Decoding at this position is unlikely to produce a valid deleted record. " +
                   "Do you want to proceed anyway?",
            TextWrapping = TextWrapping.Wrap,
            FontSize     = 12,
            Margin       = new Thickness(0, 0, 0, 16),
        };

        var proceedBtn = new Button { Content = "Proceed anyway", Margin = new Thickness(0, 0, 8, 0) };
        var cancelBtn  = new Button { Content = "Cancel" };
        proceedBtn.Click += (_, _) => dlg.Close(true);
        cancelBtn.Click  += (_, _) => dlg.Close(false);

        var buttons = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttons.Children.Add(proceedBtn);
        buttons.Children.Add(cancelBtn);

        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(text);
        root.Children.Add(buttons);
        dlg.Content = root;

        return dlg.ShowDialog<bool>(this);
    }

    // ── Corrupt record annotation ────────────────────────────────────────────

    // Hardcoded sqlite_master schema used when annotating records on page 1.
    private static readonly TableSchema SqliteMasterSchema = BuildSqliteMasterSchema();
    private static TableSchema BuildSqliteMasterSchema()
    {
        var s = new TableSchema { TableName = "sqlite_master" };
        s.Columns.Add(new SHARD.Core.Schema.ColumnDefinition { Name = "type",     Affinity = TypeAffinity.Text });
        s.Columns.Add(new SHARD.Core.Schema.ColumnDefinition { Name = "name",     Affinity = TypeAffinity.Text });
        s.Columns.Add(new SHARD.Core.Schema.ColumnDefinition { Name = "tbl_name", Affinity = TypeAffinity.Text });
        s.Columns.Add(new SHARD.Core.Schema.ColumnDefinition { Name = "rootpage", Affinity = TypeAffinity.Integer });
        s.Columns.Add(new SHARD.Core.Schema.ColumnDefinition { Name = "sql",      Affinity = TypeAffinity.Text });
        return s;
    }

    private async void OnAnnotateCorruptRecordClicked(object? sender, RoutedEventArgs e)
    {
        if (Vm.SelectedPage?.PageType != PageType.BTreeLeafTable ||
            Vm.SelectedPage.TableName is null ||
            Vm.SelectedByteOffset < 0 ||
            Vm.SelectedPageDetail is null ||
            Vm.Database is null)
            return;

        var schema = Vm.Database.GetTableSchema(Vm.SelectedPage.TableName)
                  ?? (Vm.SelectedPage.TableName == "sqlite_master" ? SqliteMasterSchema : null);
        if (schema is null) return;

        var vm = new CorruptRecordAnnotationViewModel(
            Vm.SelectedByteOffset,
            Vm.SelectedPageDetail.PageBytes,
            Vm.Database.Header.TextEncoding,
            schema);

        bool save = await new CorruptRecordAnnotationWindow(vm).ShowDialog<bool>(this);
        if (save) Vm.SaveCorruptRecordAnnotation(vm);
        if (vm.WantToRegisterSchema && vm.ExtractedSchema is not null)
            Vm.RegisterManualSchema(
                vm.ExtractedSchema,
                vm.ExtractedRootPage,
                vm.ExtractedSql,
                Vm.SelectedPage!.PageNumber,
                vm.AnchorOffset,
                vm.DecodedCell?.CellByteLengthOnPage ?? 0);
    }

    // ── Convenience ──────────────────────────────────────────────────────────

    private MainWindowViewModel Vm => (MainWindowViewModel)DataContext!;
}
