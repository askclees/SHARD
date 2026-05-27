namespace SHARD.Core.Records;

/// <summary>
/// A fully parsed SQLite record (the payload of a table leaf cell).
///
/// Record wire format:
///   [ varint: header length including itself ]
///   [ varint: serial type for column 0       ]
///   [ varint: serial type for column 1       ]
///   ...
///   [ payload bytes for column 0 ]
///   [ payload bytes for column 1 ]
///   ...
/// </summary>
public sealed class SqliteRecord
{
    /// <summary>Decoded column values, in column order.</summary>
    public IReadOnlyList<SqliteValue> Values { get; }

    /// <summary>Number of columns in this record.</summary>
    public int ColumnCount => Values.Count;

    public SqliteRecord(IReadOnlyList<SqliteValue> values)
    {
        Values = values;
    }

    /// <summary>Parse a record from raw payload bytes.</summary>
    public static SqliteRecord Parse(ReadOnlySpan<byte> payload) =>
        throw new NotImplementedException();
}
