using SHARD.Core.Enums;
using SHARD.Core.Pages;
using SHARD.Core.Records;
using SHARD.Core.Schema;

namespace SHARD.Core.Recovery;

/// <summary>How strictly a candidate table's columns must match observed byte content.</summary>
public enum CarveMode
{
    /// <summary>Match on schema-declared affinity only (today's <see cref="RecordStructure.FromSchema"/> behavior).</summary>
    Loose,

    /// <summary>Narrow every candidate to the (kind, content-length) pairs actually observed in that table's live rows (<see cref="RecordStructure.Tighten"/>).</summary>
    Tight,
}

/// <summary>A B-tree leaf cell carved from a page with no known owning table, attributed to one by content matching.</summary>
public readonly record struct CarvedOrphanRecord(TableSchema Schema, BTreeLeafCell Cell, uint PageNumber);

/// <summary>
/// Explicit, user-triggered scan of pages that <see cref="SqliteForensicDatabase.GetUnclaimedPageNumbers"/>
/// reports as having no known owning table, trying every candidate live table's <see cref="RecordStructure"/>
/// against each page's raw bytes. This never runs automatically as part of building a shadow database —
/// callers (the CLI's <c>carve</c> command, or a UI command) invoke it explicitly. Read-only: it never
/// writes to a shadow database itself.
/// </summary>
/// <remarks>Persisting results into a shadow database is a separate, explicit step — see <c>ShadowDatabaseBuilder.PersistCarvedOrphanRecords</c> in <c>SHARD.Core.Shadow</c>.</remarks>
public static class OrphanPageCarver
{
    /// <summary>
    /// Builds one carving candidate per live table in <paramref name="database"/>, using the same
    /// exclusions <see cref="Shadow.ShadowDatabaseBuilder.Create"/> applies (skip sqlite_* tables,
    /// virtual tables, and any table whose CREATE TABLE SQL can't be parsed), optionally restricted
    /// to <paramref name="tableFilter"/> (case-insensitive table names).
    /// </summary>
    public static IReadOnlyList<(TableSchema Schema, RecordStructure Structure)> BuildCandidates(
        SqliteForensicDatabase database, CarveMode mode, IEnumerable<string>? tableFilter = null)
    {
        HashSet<string>? filter = tableFilter is null
            ? null
            : new HashSet<string>(tableFilter, StringComparer.OrdinalIgnoreCase);

        var candidates = new List<(TableSchema Schema, RecordStructure Structure)>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in database.ReadSqliteMaster())
        {
            if (row.ObjectType != SqliteMasterObjectType.Table) continue;
            if (row.Sql is null || row.RootPage is null) continue;
            if (row.Name.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase)) continue;
            if (row.Sql.Contains("VIRTUAL TABLE", StringComparison.OrdinalIgnoreCase)) continue;
            if (filter is not null && !filter.Contains(row.Name)) continue;

            var schema = CreateTableParser.ExtractTableSchema(row.Sql);
            if (schema is null || schema.Columns.Count == 0) continue;

            var structure = mode == CarveMode.Tight
                ? RecordStructure.Tighten(schema, database.ReadTableRows(row.RootPage.Value))
                : RecordStructure.FromSchema(schema);

            candidates.Add((schema, structure));
            seenNames.Add(row.Name);
        }

        // Dropped tables: a non-root leaf page belonging to a dropped table's b-tree has no
        // sqlite_master trace of its own (only the table's root page, if recovered, does) — content
        // matching against its schema is often the only way such pages are ever recovered. Skip any
        // name a live table already claims (live wins — more likely current/correct).
        try
        {
            foreach (var deleted in database.ReadDeletedSqliteMaster())
            {
                if (deleted.Row.ObjectType != SqliteMasterObjectType.Table) continue;
                if (deleted.Row.Sql is null) continue;
                if (!seenNames.Add(deleted.Row.Name)) continue; // already live, or already added from another deleted-master hit
                if (filter is not null && !filter.Contains(deleted.Row.Name)) continue;

                var schema = CreateTableParser.ExtractTableSchema(deleted.Row.Sql);
                if (schema is null || schema.Columns.Count == 0) continue;

                RecordStructure structure = RecordStructure.FromSchema(schema);
                if (mode == CarveMode.Tight && deleted.RootPageStatus == RootPageStatus.Valid && deleted.Row.RootPage is not null)
                {
                    try
                    {
                        structure = RecordStructure.Tighten(schema, database.ReadTableRows(deleted.Row.RootPage.Value));
                    }
                    catch { /* root page not actually walkable — fall back to loose for this candidate */ }
                }

                candidates.Add((schema, structure));
            }
        }
        catch { /* dropped-table detection is a forensic aid, not required for correctness */ }

        return candidates;
    }

    /// <summary>
    /// Scans every page <see cref="SqliteForensicDatabase.GetUnclaimedPageNumbers"/> reports against
    /// all of <paramref name="candidates"/>, returning every uniquely-matched record found. Does not
    /// write to any database — callers persist the results themselves.
    /// </summary>
    public static IReadOnlyList<CarvedOrphanRecord> Carve(
        SqliteForensicDatabase database,
        IReadOnlyList<(TableSchema Schema, RecordStructure Structure)> candidates,
        out int ambiguousSkipped)
    {
        ambiguousSkipped = 0;
        var results = new List<CarvedOrphanRecord>();
        if (candidates.Count == 0) return results;

        var candidateTuples = candidates
            .Select(c => (c.Schema.TableName, c.Structure))
            .ToList();
        var schemaByName = candidates
            .GroupBy(c => c.Schema.TableName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Schema, StringComparer.OrdinalIgnoreCase);

        int reserved = database.Header.ReservedBytesPerPage;

        foreach (uint pageNumber in database.GetUnclaimedPageNumbers())
        {
            SqlitePage page;
            try { page = database.ReadPage(pageNumber); }
            catch { continue; }

            ReadOnlySpan<byte> scanArea = reserved > 0 && reserved < page.Data.Length
                ? page.Data.AsSpan(0, page.Data.Length - reserved)
                : page.Data;

            var matches = DeletedRecordParser.CarveRawBytesAnySchema(
                scanArea, database.Header.TextEncoding, candidateTuples, out int pageAmbiguous);
            ambiguousSkipped += pageAmbiguous;

            foreach (var (tableName, cell) in matches)
                results.Add(new CarvedOrphanRecord(schemaByName[tableName], cell, pageNumber));
        }

        return results;
    }
}
