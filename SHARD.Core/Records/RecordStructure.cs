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

    /// <summary>
    /// Optional per-column narrowing on top of <see cref="AllowedKindsPerColumn"/>: restricts a
    /// column's <see cref="HeaderEntry.ContentLength"/> to an inclusive [Min, Max] byte range (e.g.
    /// exactly 6-byte integers via (6, 6), or text observed to run 5-40 bytes via (5, 40)). Null for
    /// a column (the default from <see cref="FromSchema"/>) means unrestricted — any content length
    /// valid for the column's allowed kinds is accepted, matching today's behavior exactly. Only
    /// applied to kinds that actually carry variable content length (Integer, Float, Text, Blob) —
    /// Null/Int0/Int1 are always exactly 0 bytes by construction and are never range-checked.
    /// </summary>
    public List<(int Min, int Max)?> AllowedContentLengthRangePerColumn { get; } = new();

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
            // A rowid-alias column's true value lives in the cell's rowid field, but SQLite
            // still reserves a header slot for it in the record payload — always serial type 0
            // (NULL), never omitted. Restrict to Null so the header entry count matches on-disk
            // records exactly.
            if (col.IsRowIdAlias)
            {
                rs.AllowedKindsPerColumn.Add([SerialTypeKind.Null]);
                rs.AllowedContentLengthRangePerColumn.Add(null);
                continue;
            }

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
            rs.AllowedContentLengthRangePerColumn.Add(null);
        }
        return rs;
    }

    /// <summary>
    /// Narrows one column's allowed kinds and/or content-length range beyond what
    /// <see cref="FromSchema"/> derived from affinity alone. Pass <c>null</c> for either parameter to
    /// leave that dimension as-is. Used when the caller knows more about a column than its declared
    /// type allows for (e.g. an INTEGER column that is always encoded in exactly 6 bytes, a TEXT
    /// column observed to run 5-40 bytes, or a column that is only ever 0/1).
    /// </summary>
    public RecordStructure NarrowColumn(int columnIndex, SerialTypeKind[]? allowedKinds = null, (int Min, int Max)? allowedContentLengthRange = null)
    {
        if (allowedKinds is not null)
            AllowedKindsPerColumn[columnIndex] = allowedKinds;
        if (allowedContentLengthRange is not null)
            AllowedContentLengthRangePerColumn[columnIndex] = allowedContentLengthRange;
        return this;
    }

    /// <summary>
    /// Builds a "tight" <see cref="RecordStructure"/> from <paramref name="schema"/>, narrowing every
    /// column to the [min, max] content-length range and kinds actually observed in
    /// <paramref name="observedRows"/> — including Text/Blob, whose byte length varies naturally row
    /// to row but is still normally bounded by real data.
    /// A column with no observed non-null values is left loose (unchanged from <see cref="FromSchema"/>),
    /// since there's nothing to narrow from. Intended to sharply reduce ambiguous/false-positive matches
    /// when many candidate tables are tried against the same unattributed page bytes.
    /// </summary>
    public static RecordStructure Tighten(TableSchema schema, IEnumerable<TableRow> observedRows)
    {
        var rs = FromSchema(schema);

        // FieldValues has one entry per declared column, in order — including a NULL entry for
        // a rowid-alias column, since SQLite reserves its header slot even though the alias's
        // true value lives in the cell's rowid field (see FromSchema).
        var payloadColumns = schema.Columns;
        var observedKinds = new HashSet<SerialTypeKind>[payloadColumns.Count];
        var observedLengths = new HashSet<int>[payloadColumns.Count];
        for (int i = 0; i < payloadColumns.Count; i++)
        {
            observedKinds[i] = new HashSet<SerialTypeKind>();
            observedLengths[i] = new HashSet<int>();
        }

        foreach (var row in observedRows)
        {
            for (int i = 0; i < payloadColumns.Count && i < row.FieldValues.Count; i++)
            {
                var value = row.FieldValues[i];
                if (value is null || value.IsNull)
                {
                    observedKinds[i].Add(SerialTypeKind.Null);
                    continue;
                }

                switch (value.StorageClass)
                {
                    case SqliteStorageClass.Integer when value.DataLength == 0 && value.IntegerValue == 0:
                        observedKinds[i].Add(SerialTypeKind.Int0);
                        break;
                    case SqliteStorageClass.Integer when value.DataLength == 0 && value.IntegerValue == 1:
                        observedKinds[i].Add(SerialTypeKind.Int1);
                        break;
                    case SqliteStorageClass.Integer:
                        observedKinds[i].Add(SerialTypeKind.Integer);
                        observedLengths[i].Add(value.DataLength);
                        break;
                    case SqliteStorageClass.Real:
                        observedKinds[i].Add(SerialTypeKind.Float);
                        observedLengths[i].Add(value.DataLength);
                        break;
                    case SqliteStorageClass.Text:
                        observedKinds[i].Add(SerialTypeKind.Text);
                        observedLengths[i].Add(value.DataLength);
                        break;
                    case SqliteStorageClass.Blob:
                        observedKinds[i].Add(SerialTypeKind.Blob);
                        observedLengths[i].Add(value.DataLength);
                        break;
                }
            }
        }

        for (int i = 0; i < payloadColumns.Count; i++)
        {
            if (observedKinds[i].Count == 0) continue; // nothing observed — leave loose

            // Nullability is a schema fact, not something that needs to be observed to be
            // trusted: a small (or unlucky) sample can easily go without a single NULL in a
            // column the schema still declares nullable, and a genuinely deleted row could well
            // have been NULL there. Keep NULL allowed regardless of the sample — at zero cost,
            // since NULL is always 0 bytes and can't loosen byte-length matching the way keeping
            // a wide Integer/Text/Blob range would.
            if (!payloadColumns[i].IsNotNull)
                observedKinds[i].Add(SerialTypeKind.Null);

            rs.AllowedKindsPerColumn[i] = observedKinds[i].ToArray();

            if (observedLengths[i].Count > 0)
                rs.AllowedContentLengthRangePerColumn[i] = (observedLengths[i].Min(), observedLengths[i].Max());
        }

        return rs;
    }
}
