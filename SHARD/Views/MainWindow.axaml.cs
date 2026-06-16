using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using SHARD.Controls;
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

    public MainWindow()
    {
        InitializeComponent();

        // Wire up named controls
        this.FindControl<MenuItem>("MenuOpen")!.Click          += OnOpenClick;
        this.FindControl<MenuItem>("MenuClose")!.Click         += OnCloseClick;
        this.FindControl<MenuItem>("MenuCreateProject")!.Click += OnCreateProjectClick;
        this.FindControl<MenuItem>("MenuOpenProject")!.Click   += OnOpenProjectClick;
        this.FindControl<MenuItem>("MenuExit")!.Click          += (_, _) => Close();
        this.FindControl<Button>("BtnOpen")!.Click             += OnOpenClick;

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
        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.Control)
            Vm.QueryTab.RunQueryCommand.Execute(default).Subscribe();
    }

    private void OnTableDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: string table })
            Vm.QueryTab.RunQueryForTable(table);
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

    private async void OnCreateProjectClick(object? sender, RoutedEventArgs e)
    {
        var dialog = new CreateProjectWindow();
        var path = await dialog.ShowDialog<string?>(this);

        if (path is not null)
            Vm.CreateProject(path);
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
        this.FindControl<HexView>("PageHexView")?.ScrollToByteOffset(vm.ByteOffset);
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

    // ── Convenience ──────────────────────────────────────────────────────────

    private MainWindowViewModel Vm => (MainWindowViewModel)DataContext!;
}
