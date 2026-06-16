using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace SHARD.Views;

/// <summary>
/// Prompts the user for a project folder path. Offers a native folder picker via
/// "Browse…" on a best-effort basis (it depends on a desktop portal that may not be
/// available in every environment), but manual path entry always works.
/// </summary>
public partial class CreateProjectWindow : Window
{
    public CreateProjectWindow()
    {
        InitializeComponent();

        this.FindControl<Button>("BrowseButton")!.Click += OnBrowseClick;
        this.FindControl<Button>("CreateButton")!.Click += OnCreateClick;
        this.FindControl<Button>("CancelButton")!.Click += (_, _) => Close(null);
    }

    private async void OnBrowseClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title         = "Select Project Folder",
                AllowMultiple = false,
            });

            if (folders is [var folder])
                this.FindControl<TextBox>("PathBox")!.Text = folder.Path.LocalPath;
        }
        catch
        {
            // Native picker unavailable (e.g. no desktop portal) — user can type the path instead.
        }
    }

    private void OnCreateClick(object? sender, RoutedEventArgs e)
    {
        string path = this.FindControl<TextBox>("PathBox")!.Text?.Trim() ?? "";
        var errorText = this.FindControl<TextBlock>("ErrorText")!;

        if (path.Length == 0)
        {
            errorText.Text = "Enter a folder path.";
            errorText.IsVisible = true;
            return;
        }

        Close(path);
    }
}
