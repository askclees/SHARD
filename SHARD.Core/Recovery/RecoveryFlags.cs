namespace SHARD.Core.Recovery;

/// <summary>
/// Per-recovery-type opt-out flags for building a shadow database. All default true, matching
/// the historical always-on behavior — a caller that doesn't specify anything gets exactly what
/// it always got. Orphan-page carving deliberately isn't here: it has different semantics (a
/// <see cref="Recovery.CarveMode"/> plus an optional table filter, not just on/off) and is
/// already its own opt-in concept in <see cref="RecoveryOptions"/>.
/// </summary>
public sealed record RecoveryFlags(
    bool DeletedCells        = true,
    bool InTreeCarving       = true,
    bool FreeblockCarving    = true,
    bool DroppedTableSchema  = true,
    bool DroppedTableCarving = true,
    bool ProcessWal          = true)
{
    public static readonly RecoveryFlags Default = new();
}
