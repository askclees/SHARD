using Microsoft.Data.Sqlite;
using SHARD.Core.Enums;
using SHARD.Core.Recovery;
using SHARD.Core.Records;
using SHARD.Core.Schema;
using SHARD.Core.Shadow;
using SHARD.Core.WAL;

namespace SHARD.Core.Session;

/// <summary>
/// A single evidence file plus its sibling WAL file (if any), opened together. Consolidates what
/// used to be two independent, ad hoc "does a -wal sibling exist?" checks (in
/// <see cref="Recovery.SqliteRecoveryFacade.Recover"/> and the GUI's own load flow) into one place,
/// and exposes each recovery type as an independently toggleable step via <see cref="RecoveryFlags"/>
/// instead of the previous all-or-nothing build.
///
/// Rollback-journal (<c>-journal</c>) support doesn't exist yet — <see cref="Open"/>'s sibling
/// detection is structured as one small per-format step (<see cref="DetectWal"/>) rather than a
/// single inlined check, so a <c>DetectJournal</c> counterpart can slot in later without a redesign.
/// </summary>
public sealed class EvidenceSession : IDisposable
{
    public string EvidencePath { get; }
    public SqliteForensicDatabase Database { get; }

    /// <summary>Null if no sibling <c>-wal</c> file was found (or it failed to parse — the evidence
    /// database itself is still perfectly usable without it).</summary>
    public WalFile? Wal { get; }

    public bool HasWal => Wal is not null;

    private EvidenceSession(string evidencePath, SqliteForensicDatabase database, WalFile? wal)
    {
        EvidencePath = evidencePath;
        Database = database;
        Wal = wal;
    }

    /// <summary>Opens the evidence file and detects/loads its sibling WAL file, if present.</summary>
    public static EvidenceSession Open(string evidencePath)
    {
        var database = SqliteForensicDatabase.Open(evidencePath);
        var wal = DetectWal(evidencePath, database);
        return new EvidenceSession(evidencePath, database, wal);
    }

    private static WalFile? DetectWal(string evidencePath, SqliteForensicDatabase database)
    {
        string walPath = evidencePath + "-wal";
        if (!File.Exists(walPath)) return null;
        try
        {
            return new WalFile(walPath, database.Header.TextEncoding, database.Header.ReservedBytesPerPage);
        }
        catch
        {
            return null;
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
    /// Syncs live WAL-only records and recovers WAL-deleted records into <paramref name="project"/>.
    /// A no-op returning (0, 0) if this session has no WAL or <see cref="RecoveryFlags.ProcessWal"/>
    /// is off. Each half fails independently (matching this operation's previous behavior) — a sync
    /// failure doesn't prevent the deleted-record recovery half from still running, and vice versa.
    /// </summary>
    public (int Synced, int Recovered) SyncAndRecoverWal(ShadowProject project, RecoveryFlags? flags = null)
    {
        flags ??= RecoveryFlags.Default;
        if (Wal is null || !flags.ProcessWal) return (0, 0);

        int synced = 0, recovered = 0;
        try { synced = project.SyncWalFramesToShadow(Wal, Database); } catch { }
        try { recovered = project.RecoverWalDeletedRows(Wal, Database); } catch { }
        return (synced, recovered);
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
