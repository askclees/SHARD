using System.Text.Json;
using System.Text.Json.Serialization;

namespace SHARD.Core.BlobViewers;

/// <summary>
/// Discovers blob-viewer plugins from one or more folders, each containing subfolders with a
/// <c>viewer.json</c> manifest (see <see cref="BlobViewerManifest"/>) plus the script it names.
/// </summary>
public sealed class BlobViewerRegistry
{
    private const string ManifestFileName = "viewer.json";

    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public IReadOnlyList<BlobViewerPlugin> Plugins { get; }

    private BlobViewerRegistry(IReadOnlyList<BlobViewerPlugin> plugins) => Plugins = plugins;

    /// <summary>
    /// Loads every valid plugin found directly under any of <paramref name="pluginRootFolders"/>
    /// (each plugin one subfolder deep, e.g. <c>root/some-viewer/viewer.json</c>). Missing
    /// folders and individually malformed manifests are skipped silently rather than failing
    /// the whole load — one broken plugin shouldn't take down every other one.
    /// </summary>
    public static BlobViewerRegistry Load(params IEnumerable<string> pluginRootFolders)
    {
        var plugins = new List<BlobViewerPlugin>();

        foreach (string root in pluginRootFolders)
        {
            if (!Directory.Exists(root)) continue;

            foreach (string dir in Directory.GetDirectories(root))
            {
                var plugin = TryLoadPlugin(dir);
                if (plugin is not null) plugins.Add(plugin);
            }
        }

        return new BlobViewerRegistry(plugins);
    }

    private static BlobViewerPlugin? TryLoadPlugin(string dir)
    {
        string manifestPath = Path.Combine(dir, ManifestFileName);
        if (!File.Exists(manifestPath)) return null;

        try
        {
            var manifest = JsonSerializer.Deserialize<BlobViewerManifest>(File.ReadAllText(manifestPath), ManifestJsonOptions);
            if (manifest is null || string.IsNullOrWhiteSpace(manifest.Script)) return null;
            if (!File.Exists(Path.Combine(dir, manifest.Script))) return null;

            return new BlobViewerPlugin(manifest, dir);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Finds the first plugin able to handle <paramref name="data"/>, sniffing only a small
    /// leading sample via each candidate's <c>check</c> phase — cheap even for a large blob —
    /// before the caller commits to the (potentially expensive) full <see cref="BlobViewerPlugin.Decode"/>.
    /// Plugins are tried in registration order; the first match wins.
    /// </summary>
    public BlobViewerPlugin? FindHandler(byte[] data)
    {
        byte[] sample = data.Length <= BlobViewerPlugin.CheckSampleBytes
            ? data
            : data[..BlobViewerPlugin.CheckSampleBytes];

        foreach (var plugin in Plugins)
        {
            if (plugin.CanHandle(sample)) return plugin;
        }

        return null;
    }
}
