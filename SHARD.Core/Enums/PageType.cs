namespace SHARD.Core.Enums;

/// <summary>
/// SQLite page type. The type byte lives at offset 0 of the page header
/// (offset 100 for page 1, which also carries the database header).
/// Freelist/overflow types are synthetic — identified via pointers, not a type byte.
/// </summary>
public enum PageType : byte
{
    Unknown              = 0x00,
    BTreeInteriorIndex   = 0x02,
    BTreeInteriorTable   = 0x05,
    BTreeLeafIndex       = 0x0A,
    BTreeLeafTable       = 0x0D,

    // Synthetic types (no header byte in the file)
    Overflow             = 0xFD,
    FreelistTrunk        = 0xFE,
    FreelistLeaf         = 0xFF,
}