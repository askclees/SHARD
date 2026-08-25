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
    
    public SqliteValue(double value, int length)
    {
        StorageClass = SqliteStorageClass.Real;
        RealValue = value;
        DataLength = length;
    }

    public SqliteValue(byte[] byteData, int length)
    {
        StorageClass = SqliteStorageClass.Blob;
        BlobValue = byteData;
        DataLength = length;
    }

    public bool Equals(SqliteValue? obj)
    {
        if (!(StorageClass == obj?.StorageClass))
        {
            return false;
        }

        switch (StorageClass)
        {
            case SqliteStorageClass.Integer:
                return IntegerValue == obj?.IntegerValue;
            case SqliteStorageClass.Real:
                return RealValue == obj.RealValue;
            case SqliteStorageClass.Null:
                return true;
            case SqliteStorageClass.Text:
                return String.Equals(TextValue, obj?.TextValue);
            case SqliteStorageClass.Blob:
                return BlobValue.SequenceEqual(obj?.BlobValue);
        }
        return true;
    }

}

public enum SqliteStorageClass
{
    Null,
    Integer,
    Real,
    Text,
    Blob,
}
