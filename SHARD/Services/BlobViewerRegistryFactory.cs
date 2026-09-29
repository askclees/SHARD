using SHARD.Core.BlobViewers;

namespace SHARD.BlobViewers;

/// <summary>
/// Locates and loads blob-viewer plugins from both of SHARD's plugin roots: the ones bundled
/// with the app, and any the user has added themselves. Bundled plugins are listed first, so
/// a user-added plugin can't accidentally shadow a built-in one for the same blob.
/// </summary>
public static class BlobViewerRegistryFactory
{
    /// <summary>Plugins shipped with SHARD itself — see the BlobViewers/ folder copied alongside the app.</summary>
    public static string BundledPluginsFolder =>
        Path.Combine(AppContext.BaseDirectory, "BlobViewers");

    /// <summary>Plugins the user has dropped in themselves, alongside <c>AppSettings</c>' settings.json.</summary>
    public static string UserPluginsFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SHARD", "blob-viewers");

    public static BlobViewerRegistry LoadDefault() =>
        BlobViewerRegistry.Load(BundledPluginsFolder, UserPluginsFolder);

    private static readonly Lazy<BlobViewerRegistry> LazyShared = new(LoadDefault);

    /// <summary>A process-lifetime cached registry for the common case (e.g. opening a blob
    /// viewer) where re-scanning the plugin folders on every use would just be wasted I/O.
    /// Call <see cref="LoadDefault"/> directly instead if a just-added plugin needs picking up.</summary>
    public static BlobViewerRegistry Shared => LazyShared.Value;
}
