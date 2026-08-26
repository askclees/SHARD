namespace SHARD.ViewModels;

/// <summary>Backs the modal dialog shown after loading a carving profile — summarizes which
/// tables were applied, which are new (not in the profile at all), which the profile mentioned
/// but this database doesn't have, and any columns the profile had that no longer exist.</summary>
public sealed class CarvingProfileLoadResultViewModel
{
    public IReadOnlyList<string> AppliedTables { get; init; } = [];
    public IReadOnlyList<string> NewTables { get; init; } = [];
    public IReadOnlyList<string> MissingTables { get; init; } = [];
    public IReadOnlyList<string> ColumnWarnings { get; init; } = [];

    public bool HasAppliedTables  => AppliedTables.Count > 0;
    public bool HasNewTables      => NewTables.Count > 0;
    public bool HasMissingTables  => MissingTables.Count > 0;
    public bool HasColumnWarnings => ColumnWarnings.Count > 0;

    public string AppliedHeader => $"Applied ({AppliedTables.Count})";
    public string NewTablesHeader => $"New tables — not in this profile ({NewTables.Count})";
    public string MissingTablesHeader => $"In profile, not in this database ({MissingTables.Count})";
    public string ColumnWarningsHeader => $"Columns in profile no longer present ({ColumnWarnings.Count})";
}
