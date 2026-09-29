using System.Text.Json.Serialization;

namespace SHARD.Core.BlobViewers;

/// <summary>
/// The category a decoded blob-viewer result falls into. Each kind maps to exactly one
/// canonical byte format SHARD's own renderer understands — a script is responsible for any
/// transcoding needed to reach it (e.g. a HEIC image plugin still declares <see cref="Image"/>
/// but must itself convert to PNG bytes), so the host renderer never has to guess a format.
/// </summary>
public enum BlobViewerKind
{
    /// <summary>Payload is PNG-encoded image bytes.</summary>
    Image,

    /// <summary>Payload is WAV-encoded audio bytes.</summary>
    Audio,

    /// <summary>Payload is UTF-8 plain text.</summary>
    Text,

    /// <summary>Payload is UTF-8 HTML.</summary>
    Html,

    /// <summary>
    /// No in-app rendering — payload is the (possibly re-encoded) original file content, to be
    /// offered as "open with the system's default application" or a plain Save As.
    /// </summary>
    Binary,
}

/// <summary>The JSON header line a <c>decode</c> phase writes to stdout before its raw payload bytes.</summary>
public sealed class BlobViewerOutputHeader
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    /// <summary>Suggested file extension (with leading dot) for <see cref="BlobViewerKind.Binary"/> output.</summary>
    [JsonPropertyName("extension")]
    public string? Extension { get; set; }
}

/// <summary>A blob viewer plugin's decoded output: what kind of data it is, and the bytes themselves.</summary>
public sealed record BlobViewerResult(BlobViewerKind Kind, byte[] Payload, string? SuggestedExtension);
