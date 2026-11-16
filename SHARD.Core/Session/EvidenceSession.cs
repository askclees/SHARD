using Microsoft.Data.Sqlite;
using SHARD.Core.Enums;
using SHARD.Core.Journal;
using SHARD.Core.Recovery;
using SHARD.Core.Records;
using SHARD.Core.Schema;
using SHARD.Core.Shadow;
using SHARD.Core.WAL;

namespace SHARD.Core.Session;

/// <summary>
/// A single evidence file plus its sibling <c>-wal</c> or <c>-journal</c> file (if any), opened
/// together. Consolidates what used to be independent, ad hoc "does a sidecar exist?" checks (in
/// <see cref="Recovery.SqliteRecoveryFacade.Recover"/> and the GUI's own load flow) into one place,
/// and exposes each recovery type as an independently toggleable step via <see cref="RecoveryFlags"/>
/// instead of the previous all-or-nothing build.
///
/// A database is in either WAL or rollback-journal mode, so at most one sidecar is loaded. Both
/// can exist on disk after the journal mode was switched; the header's write version (2 = WAL,
/// 1 = rollback journal) decides which one belongs to the database's current mode, and the other
/// is reported via <see cref="IgnoredSidecarNote"/>.
/// </summary>
public sealed class EvidenceSession : IDisposable
{
    public string EvidencePath { get; }
    public SqliteForensicDatabase Database { get; }

    public string WalPath     => EvidencePath + "-wal";
    public string JournalPath => EvidencePath + "-journal";

    /// <summary>The sibling WAL file, or null if there isn't one, it was ignored in favour of a
    /// journal, or it failed to parse (see <see cref="WalError"/>) — the evidence database itself
    /// is still perfectly usable without it.</summary>
    public WalFile? Wal { get; }
    public bool HasWal => Wal is not null;

    /// <summary>Why the sibling WAL file exists but couldn't be loaded; null otherwise.</summary>
    public string? WalError { get; }

    /// <summary>The sibling rollback journal, or null if there isn't one, it was ignored in favour
    /// of a WAL, or it failed to load (see <see cref="JournalError"/>).</summary>
    public RollbackJournalFile? Journal { get; }
    public bool HasJournal => Journal is not null;

    /// <summary>Why the sibling journal file exists but couldn't be loaded; null otherwise.</summary>
    public string? JournalError { get; }

    /// <summary>Set when both sidecars exist and one was ignored because it doesn't match the
    /// database's current journal mode.</summary>
    public string? IgnoredSidecarNote { get; }

    private EvidenceSession(string evidencePath, SqliteForensicDatabase database,
        WalFile? wal, string? walError, RollbackJournalFile? journal, string? journalError, string? ignoredSidecarNote)
    {
        EvidencePath = evidencePath;
        Database = database;
        Wal = wal;
        WalError = walError;
        Journal = journal;
        JournalError = journalError;
        IgnoredSidecarNote = ignoredSidecarNote;
    }

    /// <summary>Opens the evidence file and detects/loads its sibling WAL or journal file, if present.</summary>
    public static EvidenceSession Open(string evidencePath)
    {
        var database = SqliteForensicDatabase.Open(evidencePath);

        bool hasWal     = File.Exists(evidencePath + "-wal");
        bool hasJournal = File.Exists(evidencePath + "-journal");
        string? ignoredSidecarNote = null;
        if (hasWal && hasJournal)
        {
            bool walMode = database.Header.WriteVersion == 2;
            ignoredSidecarNote = walMode ? "journal file ignored (database is in WAL mode)"
                                         : "WAL file ignored (database is in rollback-journal mode)";
            hasWal     = walMode;
            hasJournal = !walMode;
        }

        WalFile? wal = null;
        string? walError = null;
        if (hasWal)
            (wal, walError) = LoadWal(evidencePath + "-wal", database);

        RollbackJournalFile? journal = null;
        string? journalError = null;
        if (hasJournal)
            (journal, journalError) = LoadJournal(evidencePath + "-journal", database);

        return new EvidenceSession(evidencePath, database, wal, walError, journal, journalError, ignoredSidecarNote);
    }

    private static (WalFile? Wal, string? Error) LoadWal(string walPath, SqliteForensicDatabase database)
    {
        try
        {
            return (new WalFile(walPath, database.Header.TextEncoding, database.Header.ReservedBytesPerPage), null);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>
    /// Loads a rollback journal for <paramref name="database"/>. Rejects one whose page size differs
    /// from the database's, since that journal was written for a different database. Public so a
    /// manually chosen journal (e.g. the GUI's "Load Journal File…" action) gets the same check.
    /// </summary>
    public static (RollbackJournalFile? Journal, string? Error) LoadJournal(string journalPath, SqliteForensicDatabase database)
    {
        try
        {
            var journal = new RollbackJournalFile(journalPath, database.Header.TextEncoding, database.Header.ReservedBytesPerPage);
            int journalPageSize = journal.Segments[0].Header.SizeOfJournalPages;
            if (journalPageSize != database.Header.PageSize)
                return (null, $"its page size ({journalPageSize:N0}) does not match the database's ({database.Header.PageSize:N0})");
            return (journal, null);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    // ── One-shot / batch path — CLI, facade, Python ─────────────────────────────

    /// <summary>
    /// Builds a fully recovered shadow database at <paramref name="shadowDbPath"/> in one call:
    /// live rows, in-tree recovery per <paramref name="flags"/>, and — if this session has a WAL
    /// and <see cref="RecoveryFlags.ProcessWal"/> is on — WAL-deleted-record recovery against the
    /// just-built output. Orphan-page carving is deliberately not included here; call
    /// <see cref="CarveUnknownPages"/> separately if wanted, same as before this refactor.
    /// </summary>
    public (IReadOnlyList<string> Warnings, int WalRecordsInserted) BuildShadowDatabase(
        string shadowDbPath, RecoveryFlags? flags = null, Action<string>? reportProgress = null)
    {
        flags ??= RecoveryFlags.Default;
        var warnings = ShadowDatabaseBuilder.Create(shadowDbPath, Database, flags, reportProgress);

        int walInserted = 0;
        if (flags.ProcessWal && Wal is not null)
        {
            using var connection = new SqliteConnection($"Data Source={shadowDbPath}");
            connection.Open();
            walInserted = ShadowDatabaseBuilder.InsertWalDeletedRows(connection, Database, Wal);
        }

        return (warnings, walInserted);
    }

    // ── Progressive path — GUI drives these against its own already-created ShadowProject ──────

    /// <summary>
    /// Recovers dropped tables found via <c>ReadDeletedSqliteMaster</c>: table data for one whose
    /// root page is still a valid B-tree (<see cref="RootPageStatus.Valid"/>, gated by
    /// <see cref="RecoveryFlags.DroppedTableSchema"/>), and carved bytes for one whose root page is
    /// now a freelist page (<see cref="RootPageStatus.Freed"/>, gated by
    /// <see cref="RecoveryFlags.DroppedTableCarving"/>). A per-table failure is skipped, not fatal —
    /// matches this operation's previous behavior. Returns the entries actually processed, so the
    /// caller can do its own GUI-side bookkeeping (e.g. registering freed-page schemas for lazy
    /// carving on navigation) without this type needing to know about GUI view-models.
    /// </summary>
    public IReadOnlyList<(TableSchema Schema, uint RootPage, RootPageStatus Status)> RecoverDroppedTables(
        ShadowProject project,
        IEnumerable<(TableSchema Schema, uint RootPage, RootPageStatus Status)> droppedTables,
        RecoveryFlags? flags = null)
    {
        flags ??= RecoveryFlags.Default;
        var processed = new List<(TableSchema Schema, uint RootPage, RootPageStatus Status)>();
        var all = droppedTables.ToList();

        if (flags.DroppedTableSchema)
        {
            foreach (var entry in all.Where(d => d.Status == RootPageStatus.Valid))
            {
                try
                {
                    var pageNums = Database.GetTreePageNumbers(entry.RootPage).ToList();
                    project.AddDeletedTableRecords(entry.Schema, Database.ReadTableRows(entry.RootPage));
                    project.TagDeletedTablePages(entry.Schema.TableName, pageNums);
                    processed.Add(entry);
                }
                catch { /* best-effort per table, matching this operation's previous behavior */ }
            }
        }

        if (flags.DroppedTableCarving)
        {
            foreach (var entry in all.Where(d => d.Status == RootPageStatus.Freed))
            {
                try
                {
                    var recordStructure = RecordStructure.FromSchema(entry.Schema);
                    var pageData = Database.ReadPage(entry.RootPage).Data;
                    var carved = DeletedRecordParser.CarveRawBytes(pageData, Database.Header.TextEncoding, recordStructure);
                    if (carved.Count > 0)
                    {
                        project.AddFreedPageCarvedRecords(entry.Schema, carved, entry.RootPage);
                        project.TagDeletedTablePages(entry.Schema.TableName, [entry.RootPage]);
                    }
                    processed.Add(entry);
                }
                catch { /* best-effort per table, matching this operation's previous behavior */ }
            }
        }

        return processed;
    }

    /// <summary>
    /// Syncs live WAL-only records and recovers WAL-deleted records from <paramref name="wal"/>
    /// into <paramref name="project"/>. Takes the WAL explicitly rather than always using this
    /// session's own auto-detected <see cref="Wal"/> — a caller may be syncing a manually-loaded
    /// WAL file from an arbitrary path instead (e.g. the GUI's "Load WAL File…" action), which
    /// doesn't have to be this evidence file's sibling. A no-op returning (0, 0) if
    /// <see cref="RecoveryFlags.ProcessWal"/> is off. Each half fails independently (matching
    /// this operation's previous behavior) — a sync failure doesn't prevent the deleted-record
    /// recovery half from still running, and vice versa — and each half's failure message is
    /// returned so the caller can report it rather than it being silently dropped.
    /// </summary>
    public WalSyncResult SyncAndRecoverWal(ShadowProject project, WalFile wal, RecoveryFlags? flags = null)
    {
        flags ??= RecoveryFlags.Default;
        if (!flags.ProcessWal) return new WalSyncResult(0, 0, null, null);

        int synced = 0, recovered = 0;
        string? syncError = null, recoveryError = null;
        try { synced = project.SyncWalFramesToShadow(wal, Database); }
        catch (Exception ex) { syncError = ex.Message; }
        try { recovered = project.RecoverWalDeletedRows(wal, Database); }
        catch (Exception ex) { recoveryError = ex.Message; }
        return new WalSyncResult(synced, recovered, syncError, recoveryError);
    }

    /// <summary>Explicit, user-triggered scan of pages with no known owning table. Never runs
    /// automatically — same opt-in-only semantics as before this refactor.</summary>
    public (int Carved, int AmbiguousSkipped) CarveUnknownPages(
        ShadowProject project, CarveMode mode, IReadOnlyList<string>? tableFilter = null)
    {
        var candidates = OrphanPageCarver.BuildCandidates(Database, mode, tableFilter);
        int carved = project.CarveUnknownPages(Database, candidates, out int ambiguousSkipped);
        return (carved, ambiguousSkipped);
    }

    public void Dispose() => Database.Dispose();
}

/// <summary>Outcome of <see cref="EvidenceSession.SyncAndRecoverWal"/>. Deconstructs to
/// (Synced, Recovered) for callers that only want the counts.</summary>
public sealed record WalSyncResult(int Synced, int Recovered, string? SyncError, string? RecoveryError)
{
    public void Deconstruct(out int synced, out int recovered)
    {
        synced = Synced;
        recovered = Recovered;
    }
}
