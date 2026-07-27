using SHARD.Core.Enums;
using SHARD.Core.Schema;

namespace SHARD.Core.Records;

/// <summary>
/// Describes the expected column layout of a table for use during record carving.
/// Each column carries a set of permitted <see cref="SerialTypeKind"/> values rather
/// than a single exact kind, so that NULL values and integer sub-types (Int0/Int1)
/// do not cause valid deleted records to be incorrectly rejected.
/// </summary>
public class RecordStructure
{
    /// <summary>Allowed serial-type kinds per payload column (rowid-alias columns are excluded).</summary>
    public List<SerialTypeKind[]> AllowedKindsPerColumn { get; } = new();

    public int NumColumns => AllowedKindsPerColumn.Count;

    // Base allowed kinds per affinity — Null excluded; added conditionally based on nullability.
    private static readonly SerialTypeKind[] IntegerKinds =
        [SerialTypeKind.Integer, SerialTypeKind.Int0, SerialTypeKind.Int1, SerialTypeKind.Float];

    private static readonly SerialTypeKind[] RealKinds =
        [SerialTypeKind.Float, SerialTypeKind.Integer, SerialTypeKind.Int0, SerialTypeKind.Int1];

    private static readonly SerialTypeKind[] TextKinds =
        [SerialTypeKind.Text];

    // BLOB and NUMERIC affinities impose no type preference — accept any valid kind.
    private static readonly SerialTypeKind[] AnyKind =
        [SerialTypeKind.Integer, SerialTypeKind.Int0, SerialTypeKind.Int1,
         SerialTypeKind.Float, SerialTypeKind.Text, SerialTypeKind.Blob];

    /// <summary>
    /// Builds a <see cref="RecordStructure"/> from a parsed <see cref="TableSchema"/>.
    /// Rowid-alias columns are skipped because they are stored in the cell's rowid
    /// field, not in the record payload. Null is added to the allowed kinds only for
    /// columns that are not declared NOT NULL.
    /// </summary>
    public static RecordStructure FromSchema(TableSchema schema)
    {
        var rs = new RecordStructure();
        foreach (var col in schema.Columns)
        {
            if (col.IsRowIdAlias) continue;

            SerialTypeKind[] baseKinds = col.Affinity switch
            {
                TypeAffinity.Integer => IntegerKinds,
                TypeAffinity.Real    => RealKinds,
                TypeAffinity.Text    => TextKinds,
                _                    => AnyKind,   // Blob, Numeric, or unknown
            };

            // Column can hold NULL values — prepend Null to the allowed set.
            SerialTypeKind[] allowed = col.IsNotNull
                ? baseKinds
                : [SerialTypeKind.Null, ..baseKinds];

            rs.AllowedKindsPerColumn.Add(allowed);
        }
        return rs;
    }
}
