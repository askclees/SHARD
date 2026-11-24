using System.Text.Json;
using SHARD.Core.Enums;
using SHARD.Core.Records;
using SHARD.Core.Schema;

namespace SHARD.Core.Recovery;

/// <summary>
/// One narrowable column's saved [Min, Max] byte-length range and allowed serial-type kinds
/// (<see cref="SerialTypeKind"/> names, e.g. ["Integer"] or ["Int0","Int1"] for a column
/// <see cref="RecordStructure.Tighten"/> found to be always exactly 0 or 1) within a table entry.
/// </summary>
public sealed class CarvingProfileColumnEntry
{
    public string ColumnName { get; set; } = "";
    public int MinLength { get; set; }
    public int MaxLength { get; set; }
    public List<string> AllowedKinds { get; set; } = new();
}

/// <summary>
/// One candidate table's saved carving state — always present for every table known at export
/// time, whether included or not, so a later load can tell "excluded" apart from "never seen."
/// </summary>
public sealed class CarvingProfileTableEntry
{
    public string TableName { get; set; } = "";
    public bool Included { get; set; } = true;
    public List<CarvingProfileColumnEntry> Columns { get; set; } = new();

    /// <summary>
    /// The table's original CREATE TABLE statement, if it was available at export time (it always
    /// is today, since export only happens from an already-open database). Lets a full
    /// <see cref="TableSchema"/> — column order, declared types, rowid-alias detection — be
    /// reconstructed later via <see cref="CreateTableParser.ExtractTableSchema"/> without needing
    /// that database open again, e.g. to carve a raw source (a memory image) that has no
    /// sqlite_master of its own to read a schema from.
    /// </summary>
    public string? CreateTableSql { get; set; }
}

/// <summary>
/// A saved snapshot of the "Carve Unknown Pages" tab's Focused-mode tuning — per-table
/// include/exclude state and per-column byte-length ranges — exportable to JSON and re-loadable
/// against a later (possibly schema-varied) database via <see cref="CarvingProfileMatcher"/>.
/// </summary>
public sealed class CarvingProfile
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; set; } = CurrentFormatVersion;
    public DateTime ExportedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Display-only metadata for the load-summary UI — never used to gate or validate loading.</summary>
    public string? SourceDatabaseFileName { get; set; }

    /// <summary>
    /// The source database's <see cref="Enums.TextEncoding"/> name (e.g. "Utf8") at export time.
    /// Needed to decode TEXT columns when carving raw bytes from a source with no readable header
    /// of its own left to read it from — the whole point of exporting a profile in the first place.
    /// Null for a profile exported before this field existed. Use <see cref="ResolveTextEncoding"/>
    /// rather than parsing this directly, so an old or corrupted value falls back sensibly instead
    /// of failing outright.
    /// </summary>
    public string? TextEncoding { get; set; }

    public List<CarvingProfileTableEntry> Tables { get; set; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static CarvingProfile FromJson(string json)
    {
        CarvingProfile? profile;
        try
        {
            profile = JsonSerializer.Deserialize<CarvingProfile>(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Carving profile is not valid JSON: {ex.Message}", ex);
        }

        if (profile is null)
            throw new InvalidDataException("Carving profile is empty or malformed.");

        if (profile.FormatVersion > CurrentFormatVersion)
            throw new InvalidDataException(
                $"Carving profile format version {profile.FormatVersion} is newer than this version of SHARD understands (supports up to {CurrentFormatVersion}).");

        return profile;
    }

    /// <summary>
    /// Parses <see cref="TextEncoding"/> into an actual <see cref="Enums.TextEncoding"/>, falling
    /// back to <see cref="Enums.TextEncoding.Utf8"/> — the overwhelmingly common case — if it's
    /// null (a profile exported before this field existed) or unrecognized, rather than throwing.
    /// </summary>
    public Enums.TextEncoding ResolveTextEncoding() =>
        TextEncoding is not null && Enum.TryParse<Enums.TextEncoding>(TextEncoding, out var encoding)
            ? encoding
            : Enums.TextEncoding.Utf8;
}
