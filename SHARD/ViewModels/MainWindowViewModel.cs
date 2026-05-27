using System.Collections.ObjectModel;
using ReactiveUI;
using SHARD.Core;

namespace SHARD.ViewModels;

public sealed class MainWindowViewModel : ViewModelBase
{
    // ── Loaded database ───────────────────────────────────────────────────
    private SqliteForensicDatabase? _database;
    public SqliteForensicDatabase? Database
    {
        get => _database;
        private set => this.RaiseAndSetIfChanged(ref _database, value);
    }

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
    public ObservableCollection<PageViewModel> Pages { get; } = [];

    // ── Selected page (right panel) ───────────────────────────────────────
    private PageViewModel? _selectedPage;
    public PageViewModel? SelectedPage
    {
        get => _selectedPage;
        set => this.RaiseAndSetIfChanged(ref _selectedPage, value);
    }

    // ── Overview panel info rows ──────────────────────────────────────────
    public ObservableCollection<InfoRow> DatabaseInfoRows { get; } = [];

    // ── Status bar ────────────────────────────────────────────────────────
    private string _statusText = "Open a SQLite database to begin.";
    public string StatusText
    {
        get => _statusText;
        private set => this.RaiseAndSetIfChanged(ref _statusText, value);
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

            // Always-available file info
            DatabaseInfoRows.Add(new InfoRow("File",   info.Name));
            DatabaseInfoRows.Add(new InfoRow("Path",   path));
            DatabaseInfoRows.Add(new InfoRow("Size",   FormatBytes(info.Length)));

            // Try the forensic library (works once SqliteForensicDatabase.Open() is implemented)
            try
            {
                Database = SqliteForensicDatabase.Open(path);

                DatabaseInfoRows.Add(new InfoRow("Page Size",    $"{Database.Header.PageSize:N0} bytes"));
                DatabaseInfoRows.Add(new InfoRow("Page Count",   $"{Database.PageCount:N0}"));
                DatabaseInfoRows.Add(new InfoRow("Encoding",     Database.Header.TextEncodingName));
                DatabaseInfoRows.Add(new InfoRow("Write Mode",   Database.Header.WriteVersionName));
                DatabaseInfoRows.Add(new InfoRow("Schema Cookie",$"{Database.Header.SchemaCookie}"));
                DatabaseInfoRows.Add(new InfoRow("User Version", $"{Database.Header.UserVersion}"));
                DatabaseInfoRows.Add(new InfoRow("App ID",       $"0x{Database.Header.ApplicationId:X8}"));
                DatabaseInfoRows.Add(new InfoRow("SQLite Ver",   FormatSqliteVersion(Database.Header.SqliteVersionNumber)));
                DatabaseInfoRows.Add(new InfoRow("Free Pages",   $"{Database.Header.TotalFreelistPages:N0}"));

                foreach (var page in Database.ReadAllPages())
                    Pages.Add(new PageViewModel(page));

                StatusText = $"{info.Name}  ·  {Database.PageCount:N0} pages  ·  {Database.Header.PageSize} bytes/page  ·  {Database.Header.TextEncodingName}";
            }
            catch (NotImplementedException)
            {
                DatabaseInfoRows.Add(new InfoRow("Parser", "Not yet implemented — implement SqliteForensicDatabase.Open()"));
                StatusText = $"{info.Name}  ({FormatBytes(info.Length)})  —  awaiting parser implementation";
            }

            HasDatabase = true;
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
        Pages.Clear();
        DatabaseInfoRows.Clear();
        SelectedPage = null;
        HasDatabase  = false;
        StatusText   = "Open a SQLite database to begin.";
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024                => $"{bytes} B",
        < 1024 * 1024         => $"{bytes / 1024.0:F1} KB",
        < 1024 * 1024 * 1024  => $"{bytes / (1024.0 * 1024):F1} MB",
        _                     => $"{bytes / (1024.0 * 1024 * 1024):F2} GB",
    };

    private static string FormatSqliteVersion(uint v)
    {
        // e.g. 3046000 → "3.46.0"
        int major = (int)(v / 1_000_000);
        int minor = (int)(v % 1_000_000 / 1_000);
        int patch = (int)(v % 1_000);
        return $"{major}.{minor}.{patch}";
    }
}
