namespace SHARD.Core.Records;

/// <summary>One page's worth of an overflow chain for a record that spilled past its local payload.</summary>
public sealed record OverflowFragment(int Sequence, uint PageNumber, uint NextPageNumber, int PayloadLength);
