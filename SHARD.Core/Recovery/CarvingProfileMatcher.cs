using SHARD.Core.Enums;
using SHARD.Core.Records;
using SHARD.Core.Schema;

namespace SHARD.Core.Recovery;

/// <summary>
/// Reconciles a previously-exported <see cref="CarvingProfile"/> against the current database's
/// carve candidates (i.e. <see cref="OrphanPageCarver.BuildCandidates"/>'s output) — pure data
/// in/out, no UI or ViewModel dependency, so it's testable with hand-built schema fixtures alone.
/// </summary>
public static class CarvingProfileMatcher
{
    public readonly record struct ColumnRange(int Min, int Max);

    /// <summary>One column's reconciled saved state — a byte-length range plus whatever serial-type
    /// kinds the profile had for it (empty if the profile didn't narrow kinds beyond the default).</summary>
    public sealed record ColumnMatch(ColumnRange Range, IReadOnlyList<SerialTypeKind> AllowedKinds);

    /// <summary>One current candidate table's reconciled state after matching against a profile.</summary>
    public sealed record TableMatch(
        string TableName,
        bool Included,
        IReadOnlyDictionary<string, ColumnMatch> Columns,
        IReadOnlyList<string> ColumnsIgnored);

    public sealed record Result(
        IReadOnlyList<TableMatch> Matches,
        IReadOnlyList<string> TablesMissingFromDatabase,
        IReadOnlyList<string> NewTablesNotInProfile);

    /// <summary>
    /// For each of <paramref name="currentCandidates"/>, looks up the matching
    /// <see cref="CarvingProfileTableEntry"/> by table name (case-insensitive). A candidate with
    /// no matching profile entry is reported in <see cref="Result.NewTablesNotInProfile"/> rather
    /// than being defaulted to included or excluded. Columns are matched by name
    /// (case-insensitive); a profile column absent from the current schema is reported in that
    /// table's <see cref="TableMatch.ColumnsIgnored"/>. A current schema column absent from the
    /// profile simply has no entry in <see cref="TableMatch.Columns"/> — the caller is expected to
    /// leave such a column at whatever default it already computed, not to invent a new default
    /// here. An unrecognized kind name (e.g. from a corrupted file) is silently skipped rather
    /// than failing the whole match. Any profile table name never matched against a current
    /// candidate is reported in <see cref="Result.TablesMissingFromDatabase"/>.
    /// </summary>
    public static Result Match(
        CarvingProfile profile,
        IReadOnlyList<(TableSchema Schema, RecordStructure Structure)> currentCandidates)
    {
        var profileByTable = profile.Tables.ToDictionary(t => t.TableName, StringComparer.OrdinalIgnoreCase);
        var matchedProfileTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var matches = new List<TableMatch>();
        var newTables = new List<string>();

        foreach (var (schema, _) in currentCandidates)
        {
            if (!profileByTable.TryGetValue(schema.TableName, out var profileEntry))
            {
                newTables.Add(schema.TableName);
                continue;
            }

            matchedProfileTables.Add(schema.TableName);

            var currentColumnNames = new HashSet<string>(
                schema.Columns.Where(c => !c.IsRowIdAlias).Select(c => c.Name),
                StringComparer.OrdinalIgnoreCase);

            var columns = new Dictionary<string, ColumnMatch>(StringComparer.OrdinalIgnoreCase);
            var columnsIgnored = new List<string>();
            foreach (var col in profileEntry.Columns)
            {
                if (!currentColumnNames.Contains(col.ColumnName))
                {
                    columnsIgnored.Add(col.ColumnName);
                    continue;
                }

                var kinds = new List<SerialTypeKind>();
                foreach (var kindName in col.AllowedKinds)
                    if (Enum.TryParse<SerialTypeKind>(kindName, out var kind))
                        kinds.Add(kind);

                columns[col.ColumnName] = new ColumnMatch(new ColumnRange(col.MinLength, col.MaxLength), kinds);
            }

            matches.Add(new TableMatch(schema.TableName, profileEntry.Included, columns, columnsIgnored));
        }

        var missingTables = profile.Tables
            .Select(t => t.TableName)
            .Where(name => !matchedProfileTables.Contains(name))
            .ToList();

        return new Result(matches, missingTables, newTables);
    }
}
