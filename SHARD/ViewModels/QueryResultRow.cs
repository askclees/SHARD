namespace SHARD.ViewModels;

public sealed class QueryResultRow
{
    private readonly string[] _values;

    public QueryResultRow(int columnCount) => _values = new string[columnCount];

    public string this[int index]
    {
        get => _values[index];
        set => _values[index] = value;
    }
}
