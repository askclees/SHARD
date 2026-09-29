using System.Text.Json.Serialization;

namespace SHARD.Core.BlobViewers;

/// <summary>How a phase's input bytes are handed to the script.</summary>
public enum BlobViewerInputMode
{
    /// <summary>Bytes are written to the script's stdin, then stdin is closed.</summary>
    Stdin,

    /// <summary>Bytes are written to a temp file, whose path is appended as the script's last argument.</summary>
    File,
}

/// <summary>
/// One invocation phase (<c>check</c> or <c>decode</c>) of a blob viewer script: the extra
/// CLI arguments to pass (e.g. a subcommand name) and how input bytes reach the script.
/// </summary>
public sealed class BlobViewerPhase
{
    [JsonPropertyName("args")]
    public string[] Args { get; set; } = [];

    [JsonPropertyName("input_mode")]
    public BlobViewerInputMode InputMode { get; set; } = BlobViewerInputMode.Stdin;
}

/// <summary>
/// Declares one blob-viewer plugin: a Python script plus how to invoke its two phases.
/// Deserialized from a <c>viewer.json</c> manifest that sits alongside the script — see
/// <see cref="BlobViewerRegistry"/> for discovery and <see cref="BlobViewerPlugin"/> for
/// how each phase is actually run.
/// </summary>
public sealed class BlobViewerManifest
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>Script file name, resolved relative to the manifest's own folder.</summary>
    [JsonPropertyName("script")]
    public string Script { get; set; } = "";

    /// <summary>
    /// Cheap "can you handle this blob?" call. Always fed a small leading sample of the
    /// blob (see <see cref="BlobViewerPlugin.CheckSampleBytes"/>), regardless of the full
    /// blob's actual size, so sniffing never requires materializing a huge blob. Exit code
    /// 0 means yes; anything else (including a thrown exception) means no. No stdout
    /// protocol — unlike <see cref="Decode"/>, nothing is parsed from its output.
    /// </summary>
    [JsonPropertyName("check")]
    public BlobViewerPhase Check { get; set; } = new();

    /// <summary>
    /// Produces the viewable result from the full blob. Exit code 0 with stdout formatted
    /// as a single UTF-8 JSON header line (see <see cref="BlobViewerOutputHeader"/>)
    /// followed immediately by the raw result bytes; anything else is treated as failure.
    /// </summary>
    [JsonPropertyName("decode")]
    public BlobViewerPhase Decode { get; set; } = new();
}
