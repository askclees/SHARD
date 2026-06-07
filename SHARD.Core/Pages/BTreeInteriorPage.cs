using System.Buffers.Binary;
using SHARD.Core.Enums;

namespace SHARD.Core.Pages;

/// <summary>
/// Abstract base for interior B-Tree pages (table and index).
/// Adds the rightmost child pointer present in all interior page headers.
/// </summary>
public abstract class BTreeInteriorPage : BTreePage
{
    /// <summary>Page number of the rightmost child page.</summary>
    public uint RightmostPointer { get; }

    protected BTreeInteriorPage(uint pageNumber, int pageSize, byte[] data)
        : base(pageNumber, pageSize, data, (pageNumber == 1 ? 100 : 0) + 12)
    {
        RightmostPointer = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(HeaderOffset + 8, 4));
    }
}
