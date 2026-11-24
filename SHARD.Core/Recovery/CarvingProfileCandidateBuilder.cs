using SHARD.Core.Enums;
using SHARD.Core.Records;
using SHARD.Core.Schema;

namespace SHARD.Core.Recovery;

/// <summary>
/// Reconstructs carving candidates directly from a <see cref="CarvingProfile"/> alone — the mirror
/// image of <see cref="OrphanPageCarver.BuildCandidates"/>: that method builds candidates by reading
/// an open database's schema, this rebuilds the same shape purely from a previously-exported
/// profile's saved <see cref="CarvingProfileTableEntry.CreateTableSql"/> and per-column narrowing.
/// Used when the source being carved has no readable schema of its own to fall back on (its header/
/// schema page was overwritten, or eventually a raw non-SQLite memory image).
/// </summary>
public static class CarvingProfileCandidateBuilder
{
    public static IReadOnlyList<(TableSchema Schema, RecordStructure Structure)> BuildCandidates(CarvingProfile profile)
    {
        var candidates = new List<(TableSchema, RecordStructure)>();

        foreach (var entry in profile.Tables)
        {
            if (!entry.Included) continue;
            if (string.IsNullOrWhiteSpace(entry.CreateTableSql)) continue;

            var schema = CreateTableParser.ExtractTableSchema(entry.CreateTableSql);
            if (schema is null || schema.Columns.Count == 0) continue;

            var structure = RecordStructure.FromSchema(schema);
            var columnsByName = entry.Columns.ToDictionary(c => c.ColumnName, StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < schema.Columns.Count; i++)
            {
                var col = schema.Columns[i];
                if (col.IsRowIdAlias) continue;
                if (!columnsByName.TryGetValue(col.Name, out var saved)) continue;

                SerialTypeKind[]? kinds = null;
                if (saved.AllowedKinds.Count > 0)
                {
                    var parsed = saved.AllowedKinds
                        .Select(k => Enum.TryParse<SerialTypeKind>(k, out var kind) ? kind : (SerialTypeKind?)null)
                        .Where(k => k is not null)
                        .Select(k => k!.Value)
                        .ToArray();
                    if (parsed.Length > 0) kinds = parsed;
                }

                structure.NarrowColumn(i, kinds, (saved.MinLength, saved.MaxLength));
            }

            candidates.Add((schema, structure));
        }

        return candidates;
    }
}
