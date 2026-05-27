using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
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
        this.FindControl<MenuItem>("MenuOpen")!.Click  += OnOpenClick;
        this.FindControl<MenuItem>("MenuClose")!.Click += OnCloseClick;
        this.FindControl<MenuItem>("MenuExit")!.Click  += (_, _) => Close();
        this.FindControl<Button>("BtnOpen")!.Click     += OnOpenClick;

        // Drag-and-drop
        AddHandler(DragDrop.DropEvent,     OnDrop);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
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

    // ── Convenience ──────────────────────────────────────────────────────────

    private MainWindowViewModel Vm => (MainWindowViewModel)DataContext!;
}
