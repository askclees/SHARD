namespace SHARD.Core.Records;

/// <summary>
/// Decodes a SQLite serial type integer into a value kind and byte length.
///
/// Serial type encoding (from the spec):
///   0        → NULL
///   1        → 8-bit signed int    (1 byte)
///   2        → 16-bit signed int   (2 bytes)
///   3        → 24-bit signed int   (3 bytes)
///   4        → 32-bit signed int   (4 bytes)
///   5        → 48-bit signed int   (6 bytes)
///   6        → 64-bit signed int   (8 bytes)
///   7        → IEEE 754 float64    (8 bytes)
///   8        → integer 0           (0 bytes, value is constant)
///   9        → integer 1           (0 bytes, value is constant)
///   10, 11   → reserved (internal use)
///   N≥12, even → BLOB,  length = (N-12)/2
///   N≥13, odd  → TEXT,  length = (N-13)/2
/// </summary>
public readonly struct SerialType
{
    /// <summary>The raw varint value read from the record header.</summary>
    public long RawValue { get; init; }

    /// <summary>The kind of value this serial type represents.</summary>
    public SerialTypeKind Kind { get; init; }

    /// <summary>Number of bytes of payload consumed by this value (0 for NULL, 8, 9).</summary>
    public int ContentLength { get; init; }

    /// <summary>Decode a raw serial type varint into a <see cref="SerialType"/>.</summary>
    public static SerialType Decode(long rawValue) =>
        throw new NotImplementedException();
}

public enum SerialTypeKind
{
    Null,
    Integer,
    Float,
    Blob,
    Text,
    Reserved,
}
