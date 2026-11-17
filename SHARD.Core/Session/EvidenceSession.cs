using Microsoft.Data.Sqlite;
using SHARD.Core.Enums;
using SHARD.Core.Integrity;
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
///
/// <see cref="Open"/> hashes the database and every sidecar on disk (MD5, SHA-1, SHA-256) before
/// parsing anything, giving a record of exactly what was examined; <see cref="VerifyHashes"/>
/// re-hashes later to show whether any file changed during the examination.
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

    /// <summary>
    /// The database as SQLite sees it: the file with the WAL's committed frames applied (see
    /// <see cref="SqliteForensicDatabase.OpenCommittedState"/>). Null when there's no WAL or it has
    /// no committed frames — the file alone is then the committed state.
    /// </summary>
    public SqliteForensicDatabase? CommittedDatabase { get; }

    /// <summary>Why <see cref="CommittedDatabase"/> couldn't be opened although the WAL has
    /// committed frames; shadow databases are then built from the file alone.</summary>
    public string? CommittedStateError { get; }

    /// <summary>
    /// Where a shadow database's rows and schema come from: <see cref="CommittedDatabase"/> when
    /// WAL processing is on and there is one, so live tables hold what SQLite returns; otherwise
    /// the file. The page layout always comes from the file (see <see cref="ShadowDatabaseBuilder.Create"/>).
    /// </summary>
    public SqliteForensicDatabase ShadowSource(RecoveryFlags? flags = null) =>
        (flags ?? RecoveryFlags.Default).ProcessWal && CommittedDatabase is not null ? CommittedDatabase : Database;

    /// <summary>Hashes of the database and every sidecar present, taken when the session was opened.</summary>
    public IReadOnlyList<EvidenceFileHash> Hashes { get; }

    private EvidenceSession(string evidencePath, SqliteForensicDatabase database,
        WalFile? wal, string? walError, RollbackJournalFile? journal, string? journalError, string? ignoredSidecarNote,
        IReadOnlyList<EvidenceFileHash> hashes, SqliteForensicDatabase? committedDatabase, string? committedStateError)
    {
        Hashes = hashes;
        CommittedDatabase = committedDatabase;
        CommittedStateError = committedStateError;
        EvidencePath = evidencePath;
        Database = database;
        Wal = wal;
        WalError = walError;
        Journal = journal;
        JournalError = journalError;
        IgnoredSidecarNote = ignoredSidecarNote;
    }

    /// <summary>Opens the evidence file and detects/loads its sibling WAL or journal file, if present.</summary>
    /// <param name="reportProgress">Called with (role, bytes hashed, total bytes) while hashing,
    /// so a caller can show progress on a large file.</param>
    /// <param name="inferZeroedJournalHeader">Experimental, off by default: if the sibling journal's
    /// header has been zeroed (journal_mode=PERSIST), read its records with the header's values
    /// inferred — see <see cref="ZeroedHeaderJournalReader"/>.</param>
    public static EvidenceSession Open(string evidencePath, Action<string, long, long>? reportProgress = null,
        bool inferZeroedJournalHeader = false)
    {
        // Hash before parsing, so the record describes the files as they were when examination began.
        var hashes = EvidenceHasher.HashEvidenceSet(evidencePath, reportProgress);
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
            (journal, journalError) = LoadJournal(evidencePath + "-journal", database, inferZeroedJournalHeader);

        SqliteForensicDatabase? committed = null;
        string? committedError = null;
        if (wal is { CommittedFrameCount: > 0 })
        {
            try { committed = SqliteForensicDatabase.OpenCommittedState(evidencePath, wal); }
            catch (Exception ex) { committedError = ex.Message; }
        }

        return new EvidenceSession(evidencePath, database, wal, walError, journal, journalError, ignoredSidecarNote,
            hashes, committed, committedError);
    }

    private static bool HeaderIsZeroed(string journalPath)
    {
        try
        {
            using var fs = new FileStream(journalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var header = new byte[28];
            return fs.Length >= header.Length && fs.Read(header) == header.Length && !header.AsSpan().ContainsAnyExcept((byte)0);
        }
        catch { return false; }
    }

    /// <summary>
    /// Re-hashes the evidence set and compares it with <see cref="Hashes"/>: any result other than
    /// <see cref="HashVerificationStatus.Match"/> means a file changed, disappeared or appeared
    /// since the session was opened.
    /// </summary>
    public IReadOnlyList<HashVerification> VerifyHashes(Action<string, long, long>? reportProgress = null) =>
        EvidenceHasher.Verify(EvidencePath, Hashes, reportProgress);

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
    /// <para><paramref name="inferZeroedHeader"/> (experimental, off by default): when the header can't
    /// be read because SQLite zeroed it, try <see cref="ZeroedHeaderJournalReader"/>. It's only tried
    /// after the normal parse fails, so it never changes how a journal with a valid header is read.</para>
    public static (RollbackJournalFile? Journal, string? Error) LoadJournal(
        string journalPath, SqliteForensicDatabase database, bool inferZeroedHeader = false)
    {
        string normalError;
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
            normalError = ex.Message;
        }

        if (!inferZeroedHeader)
            return (null, HeaderIsZeroed(journalPath)
                ? $"{normalError} The header is zeroed, as SQLite leaves it in journal_mode=PERSIST; " +
                  "the experimental zeroed-header option (InferZeroedJournalHeader) can read it."
                : normalError);

        var inferred = ZeroedHeaderJournalReader.TryRead(journalPath, database.Header.PageSize, database.PageCount,
            database.Header.TextEncoding, database.Header.ReservedBytesPerPage, out string? inferError);
        return inferred is not null ? (inferred, null) : (null, $"{normalError} ({inferError})");
    }

    // ── One-shot / batch path — CLI, facade, Python ─────────────────────────────

    /// <summary>
    /// Builds a fully recovered shadow database at <paramref name="shadowDbPath"/> in one call:
    /// live rows, in-tree recovery per <paramref name="flags"/>, and — if this session has a WAL
    /// and <see cref="RecoveryFlags.ProcessWal"/> is on — WAL-deleted-record recovery, or — if it
    /// has a rollback journal and <see cref="RecoveryFlags.ProcessJournal"/> is on — journal
    /// page-image recovery, against the just-built output. Orphan-page carving is deliberately not included here; call
    /// <see cref="CarveUnknownPages"/> separately if wanted, same as before this refactor.
    /// </summary>
    public (IReadOnlyList<string> Warnings, int WalRecordsInserted, int JournalRecordsInserted) BuildShadowDatabase(
        string shadowDbPath, RecoveryFlags? flags = null, Action<string>? reportProgress = null)
    {
        flags ??= RecoveryFlags.Default;
        var source = ShadowSource(flags);
        var warnings = ShadowDatabaseBuilder.Create(shadowDbPath, source, flags, reportProgress, Hashes,
            layoutDatabase: Database).ToList();
        if (flags.ProcessWal && Wal is not null)
            warnings.AddRange(DescribeWalProblems(Wal).Select(p => $"WAL: {p}"));
        if (flags.ProcessWal && CommittedStateError is not null)
            warnings.Add($"WAL: couldn't apply committed frames, so live tables show the database file only: {CommittedStateError}");

        int walInserted = 0;
        if (flags.ProcessWal && Wal is not null)
        {
            using var connection = new SqliteConnection($"Data Source={shadowDbPath}");
            connection.Open();
            walInserted = RecoverWalHistory(connection, Wal, source, flags);
        }

        int journalInserted = 0;
        if (flags.ProcessJournal && Journal is not null)
        {
            using var connection = new SqliteConnection($"Data Source={shadowDbPath}");
            connection.Open();
            journalInserted = ShadowDatabaseBuilder.InsertJournalDeletedRows(connection, Database, Journal);
        }

        UpdateRowStates(shadowDbPath, flags);
        return (warnings, walInserted, journalInserted);
    }

    /// <summary>
    /// Computes the row-state columns (<see cref="RowStates"/>) for every live and recovered table in
    /// the shadow database at <paramref name="shadowDbPath"/>. Run after any recovery step that adds
    /// rows; it recomputes everything, so running it again is harmless. <paramref name="wal"/>
    /// defaults to this session's own WAL; pass a WAL loaded separately (e.g. the GUI's "Load WAL
    /// File…") to describe rows against that one instead.
    /// </summary>
    public void UpdateRowStates(string shadowDbPath, RecoveryFlags? flags = null, WalFile? wal = null)
    {
        flags ??= RecoveryFlags.Default;
        wal ??= Wal;
        using var connection = new SqliteConnection($"Data Source={shadowDbPath}");
        connection.Open();

        if (flags.ProcessWal && wal is { CommittedFrameCount: > 0 })
        {
            SqliteForensicDatabase? opened = null;
            try
            {
                var committed = ReferenceEquals(wal, Wal) && CommittedDatabase is not null
                    ? CommittedDatabase
                    : opened = SqliteForensicDatabase.OpenCommittedState(EvidencePath, wal);
                RowStates.Update(connection, Database, wal, committed, "wal");
            }
            finally { opened?.Dispose(); }
            return;
        }

        string log = flags.ProcessWal && wal is not null ? "wal"
                   : flags.ProcessJournal && Journal is not null ? "journal"
                   : "none";
        RowStates.Update(connection, Database, null, null, log);
    }

    /// <summary>
    /// Things about a WAL worth reporting: a partial final frame, frames SQLite rejects (bad
    /// checksum or page number, or an invalid header), and frames that never committed.
    /// Old-generation frames aren't reported — they're normal after a checkpoint.
    /// </summary>
    public static IReadOnlyList<string> DescribeWalProblems(WalFile wal)
    {
        var problems = new List<string>();
        if (!wal.HeaderChecksumValid)
            problems.Add("the header checksum is invalid, so SQLite would ignore every frame");
        if (wal.Frames.Any(f => f.Status is WalFrameStatus.InvalidChecksum or WalFrameStatus.InvalidPageNumber))
        {
            int ignored = wal.Frames.Count(f => f.Status == WalFrameStatus.AfterInvalid);
            problems.Add($"{wal.ValidationStopReason}; SQLite stops reading there, ignoring it and {ignored} later frame{(ignored == 1 ? "" : "s")}");
        }
        int uncommitted = wal.Frames.Count(f => f.Status == WalFrameStatus.Uncommitted);
        if (uncommitted > 0)
            problems.Add($"{uncommitted} valid frame{(uncommitted == 1 ? "" : "s")} after the last commit never committed; SQLite ignores {(uncommitted == 1 ? "it" : "them")}");
        if (wal.StopReason is { } partial)
            problems.Add(partial);
        return problems;
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

        // This session's own WAL, and the project was built from CommittedDatabase: its live tables
        // already hold the committed state, so there's nothing to sync — only history to recover.
        if (ReferenceEquals(wal, Wal) && CommittedDatabase is not null && project.LiveTablesIncludeWal)
        {
            try
            {
                using var connection = new SqliteConnection($"Data Source={project.ShadowDatabasePath}");
                connection.Open();
                recovered = RecoverWalHistory(connection, wal, CommittedDatabase, flags);
            }
            catch (Exception ex) { recoveryError = ex.Message; }
            return new WalSyncResult(0, recovered, null, recoveryError);
        }

        // A WAL loaded separately (e.g. the GUI's "Load WAL File…"), or a project built from the file
        // alone: add the WAL's committed rows to the live tables.
        try { synced = project.SyncWalFramesToShadow(wal, Database); }
        catch (Exception ex) { syncError = ex.Message; }
        try { recovered = project.RecoverWalDeletedRows(wal, Database); }
        catch (Exception ex) { recoveryError = ex.Message; }
        return new WalSyncResult(synced, recovered, syncError, recoveryError);
    }

    /// <summary>
    /// WAL history into a shadow built from <paramref name="source"/>: when that's the committed
    /// state, first the database file's versions of pages the WAL replaced (rows the WAL deleted or
    /// changed), then every frame SQLite ignores. Both skip rows already recovered.
    /// </summary>
    private int RecoverWalHistory(SqliteConnection connection, WalFile wal, SqliteForensicDatabase source, RecoveryFlags flags)
    {
        int inserted = 0;
        if (source.IncludesWal)
            inserted += ShadowDatabaseBuilder.InsertSupersededFilePageRows(connection, source, Database, flags);
        inserted += ShadowDatabaseBuilder.InsertWalDeletedRows(connection, source, wal, flags);
        return inserted;
    }

    /// <summary>
    /// Recovers deleted and previous-version records from <paramref name="journal"/>'s page images
    /// into <paramref name="project"/>. Takes the journal explicitly for the same reason as
    /// <see cref="SyncAndRecoverWal"/>: the GUI may have loaded one manually from an arbitrary path.
    /// Returns (0, null) if <see cref="RecoveryFlags.ProcessJournal"/> is off; a failure is returned
    /// as its message rather than thrown.
    /// </summary>
    public (int Recovered, string? Error) RecoverJournal(ShadowProject project, RollbackJournalFile journal, RecoveryFlags? flags = null)
    {
        flags ??= RecoveryFlags.Default;
        if (!flags.ProcessJournal) return (0, null);

        try { return (project.RecoverJournalDeletedRows(journal, Database), null); }
        catch (Exception ex) { return (0, ex.Message); }
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

    public void Dispose()
    {
        CommittedDatabase?.Dispose();
        Database.Dispose();
    }
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
