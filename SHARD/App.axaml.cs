using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using SHARD.ViewModels;
using SHARD.Views;

namespace SHARD;

public partial class App : Application
{
    public override void Initialize() =>
        AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel()
            };
            desktop.MainWindow = mainWindow;

            // A file passed on the command line — e.g. double-clicking a .db file, or "Open
            // with SHARD" from a file manager's context menu, both of which invoke the app as
            // `SHARD <path>`. Opened once the window exists, since a large file may prompt
            // via a dialog owned by it.
            string? filePath = desktop.Args?.FirstOrDefault(File.Exists);
            if (filePath is not null)
                mainWindow.Opened += async (_, _) => await mainWindow.OpenDatabaseFileAsync(filePath);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
