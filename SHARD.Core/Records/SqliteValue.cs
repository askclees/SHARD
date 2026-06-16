namespace SHARD.Core.Records;

/// <summary>
/// A single typed value decoded from a SQLite record.
/// Mirrors the SQLite storage classes: NULL, INTEGER, REAL, TEXT, BLOB.
/// </summary>
public sealed class SqliteValue
{
    public SqliteStorageClass StorageClass { get; init; }
    //note for string data, will be length in bytes not characters (for UTF16)
    public int DataLength { get; init; }

    // ── Typed payloads (only one is set per instance) ─────────────────────
    public long?   IntegerValue { get; init; }
    public double? RealValue    { get; init; }
    public string? TextValue    { get; init; }
    public byte[]? BlobValue { get; init; }

    // ── Convenience ──────────────────────────────────────────────────────────
    public bool IsNull => StorageClass == SqliteStorageClass.Null;

    /// <summary>Returns the underlying value boxed as <see cref="object"/>.</summary>
    public object? Value => StorageClass switch
    {
        SqliteStorageClass.Null    => null,
        SqliteStorageClass.Integer => IntegerValue,
        SqliteStorageClass.Real    => RealValue,
        SqliteStorageClass.Text    => TextValue,
        SqliteStorageClass.Blob    => BlobValue,
        _ => null
    };

    public SqliteValue(int value, int length)
    {
        StorageClass = SqliteStorageClass.Integer;
        IntegerValue = value;
        DataLength = length;
    }
    
    public SqliteValue(long value, int length)
    {
        StorageClass = SqliteStorageClass.Integer;
        IntegerValue = value;
        DataLength = length;
    }

    public SqliteValue(string stringData, int length)
    {
        StorageClass = SqliteStorageClass.Text;
        TextValue = stringData;
        DataLength = length;
    }

    // ── Static factories ─────────────────────────────────────────────────────
    //public static readonly SqliteValue Null = new() { StorageClass = SqliteStorageClass.Null };

    public static SqliteValue FromInteger(long value) =>
        throw new NotImplementedException();

    public static SqliteValue FromReal(double value) =>
        throw new NotImplementedException();

    public static SqliteValue FromText(string value) =>
        throw new NotImplementedException();

    public static SqliteValue FromBlob(byte[] value) =>
        throw new NotImplementedException();

    /// <summary>Decode a value from payload bytes given its <see cref="HeaderEntry"/>.</summary>
    public static SqliteValue Decode(HeaderEntry headerEntry, ReadOnlySpan<byte> payload) =>
        throw new NotImplementedException();
}

public enum SqliteStorageClass
{
    Null,
    Integer,
    Real,
    Text,
    Blob,
}
