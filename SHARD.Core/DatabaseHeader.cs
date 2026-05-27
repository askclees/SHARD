namespace SHARD.Core;

/// <summary>
/// The 100-byte header at offset 0 of every SQLite database file.
/// Spec: https://www.sqlite.org/fileformat2.html#the_database_header
/// </summary>
public sealed class DatabaseHeader
{
    // ── Magic ────────────────────────────────────────────────────────────────
    /// <summary>"SQLite format 3\0" — 16 bytes at offset 0.</summary>
    public byte[] Magic { get; init; } = [];

    // ── Page size ────────────────────────────────────────────────────────────
    /// <summary>Raw value at offset 16 (big-endian uint16). Value 1 means 65536.</summary>
    public ushort PageSizeRaw { get; init; }

    /// <summary>Resolved page size in bytes (handles the 1 → 65536 special case).</summary>
    public int PageSize => PageSizeRaw == 1 ? 65536 : PageSizeRaw;

    // ── Format versions ──────────────────────────────────────────────────────
    /// <summary>Offset 18. 1 = legacy rollback journal, 2 = WAL.</summary>
    public byte WriteVersion { get; init; }

    /// <summary>Offset 19. 1 = legacy, 2 = WAL.</summary>
    public byte ReadVersion { get; init; }

    // ── Reserved space ───────────────────────────────────────────────────────
    /// <summary>Offset 20. Bytes of unused reserved space at the end of each page. Usually 0.</summary>
    public byte ReservedBytesPerPage { get; init; }

    // ── Payload fractions (fixed by spec) ────────────────────────────────────
    /// <summary>Offset 21. Must be 64.</summary>
    public byte MaxEmbeddedPayloadFraction { get; init; }

    /// <summary>Offset 22. Must be 32.</summary>
    public byte MinEmbeddedPayloadFraction { get; init; }

    /// <summary>Offset 23. Must be 32.</summary>
    public byte LeafPayloadFraction { get; init; }

    // ── Counters ─────────────────────────────────────────────────────────────
    /// <summary>Offset 24. Incremented on every write transaction.</summary>
    public uint FileChangeCounter { get; init; }

    /// <summary>Offset 28. Total pages in the database file (in-header size).</summary>
    public uint DatabaseSizeInPages { get; init; }

    // ── Freelist ─────────────────────────────────────────────────────────────
    /// <summary>Offset 32. Page number of the first freelist trunk page (0 if none).</summary>
    public uint FirstFreelistTrunkPage { get; init; }

    /// <summary>Offset 36. Total number of freelist pages.</summary>
    public uint TotalFreelistPages { get; init; }

    // ── Schema ───────────────────────────────────────────────────────────────
    /// <summary>Offset 40. Incremented whenever the schema changes.</summary>
    public uint SchemaCookie { get; init; }

    /// <summary>Offset 44. Schema format number (supported: 1–4).</summary>
    public uint SchemaFormat { get; init; }

    // ── Cache ────────────────────────────────────────────────────────────────
    /// <summary>Offset 48. Suggested page cache size (advisory only).</summary>
    public uint DefaultPageCacheSize { get; init; }

    // ── Auto-vacuum ──────────────────────────────────────────────────────────
    /// <summary>Offset 52. Largest root b-tree page in auto-vacuum mode (0 otherwise).</summary>
    public uint LargestRootBTreePage { get; init; }

    /// <summary>Offset 64. Non-zero when incremental-vacuum mode is enabled.</summary>
    public uint IncrementalVacuumMode { get; init; }

    // ── Encoding ─────────────────────────────────────────────────────────────
    /// <summary>Offset 56. 1 = UTF-8, 2 = UTF-16 LE, 3 = UTF-16 BE.</summary>
    public uint TextEncoding { get; init; }

    // ── User / app metadata ──────────────────────────────────────────────────
    /// <summary>Offset 60. Set via PRAGMA user_version.</summary>
    public uint UserVersion { get; init; }

    /// <summary>Offset 68. Set via PRAGMA application_id.</summary>
    public uint ApplicationId { get; init; }

    // ── Version tracking ─────────────────────────────────────────────────────
    /// <summary>Offset 92. The change-counter value when SqliteVersion was stored.</summary>
    public uint VersionValidFor { get; init; }

    /// <summary>Offset 96. SQLite library version number (e.g. 3046000 = 3.46.0).</summary>
    public uint SqliteVersionNumber { get; init; }

    // ── Derived helpers ──────────────────────────────────────────────────────
    public bool IsMagicValid => throw new NotImplementedException();
    public string TextEncodingName => throw new NotImplementedException();
    public string WriteVersionName => throw new NotImplementedException();

    /// <summary>Parse a DatabaseHeader from the first 100 bytes of the database file.</summary>
    public static DatabaseHeader Parse(ReadOnlySpan<byte> data) =>
        throw new NotImplementedException();
}
