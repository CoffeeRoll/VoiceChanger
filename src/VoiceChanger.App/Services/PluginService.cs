using System.Diagnostics;
using VoiceChanger.Core.Effects;
using VoiceChanger.Plugins;

namespace VoiceChanger.App.Services;

/// <summary>
/// The VST3 plug-ins: the effects they add to the registry, from the list remembered at the last
/// start, and keeping that list up to date.
/// </summary>
internal sealed class PluginService
{
    private PluginService(PluginCatalog catalog, EffectRegistry registry)
    {
        Catalog = catalog;
        Registry = registry;
    }

    public PluginCatalog Catalog { get; }

    /// <summary>The built-in effects and the plug-ins.</summary>
    public EffectRegistry Registry { get; }

    /// <summary>The built-in effects plus the plug-ins found last time (read from the cache, nothing is scanned).</summary>
    public static PluginService Load(string cachePath)
    {
        var registry = EffectRegistry.CreateDefault();
        var catalog = new PluginCatalog(cachePath);
        catalog.Load();
        catalog.RegisterInto(registry);
        return new PluginService(catalog, registry);
    }

    /// <summary>
    /// Looks for new and changed plug-ins in the background, each module loaded in a separate run of
    /// the app. Then, back on the calling thread, registers the new effects and returns them.
    /// </summary>
    public async Task<IReadOnlyList<EffectDescriptor>> RescanAsync(IEnumerable<string> folders)
    {
        if (!PluginEffect.IsBridgeAvailable(out string? problem))
        {
            // Without the bridge every scan would fail and be remembered as failed.
            Trace.WriteLine(problem);
            return [];
        }

        try
        {
            if (await Task.Run(() => Catalog.Scan(folders, PluginCatalog.ScanWithThisApp)))
            {
                Catalog.Save();
            }
        }
        catch (Exception ex)
        {
            // A background chore: the plug-ins already known keep working.
            Trace.WriteLine($"Looking for plug-ins failed: {ex.Message}");
        }

        return Catalog.RegisterInto(Registry);
    }
}
