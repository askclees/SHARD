using System.Buffers.Binary;
using SHARD.Core.Enums;

namespace SHARD.Core.Pages;

/// <summary>One pointer-map entry: what <see cref="PageNumber"/> is and which page points to it.</summary>
/// <param name="PageNumber">The page this entry describes.</param>
/// <param name="TypeByte">The raw type byte; see <see cref="Type"/>.</param>
/// <param name="ParentPage">The page that points to it (0 for root and free pages).</param>
/// <param name="Offset">The entry's byte offset within the pointer-map page.</param>
public sealed record PointerMapEntry(uint PageNumber, byte TypeByte, uint ParentPage, int Offset)
{
    /// <summary>The entry type, or null if the type byte isn't one SQLite writes (1–5).</summary>
    public PointerMapEntryType? Type =>
        TypeByte is >= 1 and <= 5 ? (PointerMapEntryType)TypeByte : null;
}

/// <summary>
/// A pointer-map page of an auto-vacuum database, identified by its page number (see
/// <see cref="PageLayout"/>). It's an array of 5-byte entries — a type byte and a big-endian
/// parent page number — one for each page that follows it, up to the next pointer-map page.
/// <para>Its first byte is the first entry's type, which can be 0x02 or 0x05 and so look like a
/// B-tree interior page's type byte; that's why it can't be classified from its contents.</para>
/// </summary>
public sealed class PointerMapPage : SqlitePage
{
    public override PageType PageType => PageType.PointerMap;

    /// <summary>
    /// The entries that hold anything (a non-zero type or parent), in page order. Slots past the
    /// end of the database are normally zero; any that aren't are kept, since they may be left
    /// over from before the database shrank.
    /// </summary>
    public IReadOnlyList<PointerMapEntry> Entries { get; }

    public PointerMapPage(uint pageNumber, int pageSize, byte[] data, PageLayout layout)
        : base(pageNumber, pageSize, data)
    {
        var entries = new List<PointerMapEntry>();
        for (int i = 0; i < layout.PointerMapEntriesPerPage; i++)
        {
            int offset = i * 5;
            byte type = data[offset];
            uint parent = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset + 1, 4));
            if (type != 0 || parent != 0)
                entries.Add(new PointerMapEntry(pageNumber + 1 + (uint)i, type, parent, offset));
        }
        Entries = entries;
    }
}

/// <summary>
/// The page holding file offset 1 GiB, which SQLite uses only for file locks and never writes
/// (see <see cref="PageLayout"/>). Whatever it holds wasn't put there by SQLite.
/// </summary>
public sealed class LockBytePage : SqlitePage
{
    public override PageType PageType => PageType.LockByte;

    public LockBytePage(uint pageNumber, int pageSize, byte[] data)
        : base(pageNumber, pageSize, data) { }
}
