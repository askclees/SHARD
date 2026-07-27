namespace SHARD.ViewModels;

public sealed class QueryTableViewModel
{
    public string DisplayName { get; }
    public string ActualName  { get; }

    public QueryTableViewModel(string actualName, string displayName)
    {
        ActualName  = actualName;
        DisplayName = displayName;
    }
}
