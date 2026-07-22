namespace SHARD.ViewModels;

public sealed class RecoveryResultViewModel
{
    public bool IsValid { get; init; }
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public List<RecoveryFieldRow> Fields { get; init; } = [];
    public IReadOnlyList<string> Errors { get; init; } = [];
    public bool CanAdd { get; init; }
}

public record RecoveryFieldRow(string Column, string Value);
