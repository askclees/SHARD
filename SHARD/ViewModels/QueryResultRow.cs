namespace SHARD.ViewModels;

public sealed class QueryResultRow
{
    private readonly string[] _values;

    /// <summary>Raw bytes for a BLOB-typed column, indexed in parallel with <see cref="this[int]"/>'s
    /// display string — null for any column that isn't a BLOB or whose value is NULL.</summary>
    private readonly byte[]?[] _blobValues;

    public QueryResultRow(int columnCount)
    {
        _values = new string[columnCount];
        _blobValues = new byte[columnCount][];
    }

    public string this[int index]
    {
        get => _values[index];
        set => _values[index] = value;
    }

    public byte[]? GetBlob(int index) => _blobValues[index];
    public void SetBlob(int index, byte[]? value) => _blobValues[index] = value;
}
