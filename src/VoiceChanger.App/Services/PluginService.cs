using System.Diagnostics;
using VoiceChanger.Core.Effects;
using VoiceChanger.Plugins;

namespace VoiceChanger.App.Services;

/// <summary>
/// The VST3 plug-ins: the effects they add to the registry (from the list remembered at the last
/// start, then kept up to date by scanning), how they run, the folders they come from, and the ones
/// kept out of the menus. A plug-in that crashes is kept out until the user lets it back in.
/// </summary>
public sealed class PluginService
{
    private readonly Func<string, IReadOnlyList<PluginInfo>> _scan;
    private readonly PluginHostCommand? _hostCommand;
    private PluginHosting _hosting;

    /// <summary>
    /// Plug-ins from <paramref name="catalog"/>, hosted (when in processes of their own) by
    /// <paramref name="hostCommand"/>; <paramref name="scan"/> scans a module, by default in a
    /// separate process of the right architecture.
    /// </summary>
    public PluginService(PluginCatalog catalog, EffectRegistry registry, PluginHostCommand? hostCommand, Func<string, IReadOnlyList<PluginInfo>>? scan = null)
    {
        Catalog = catalog;
        Registry = registry;
        _hostCommand = hostCommand;
        _hosting = MakeHosting();
        _scan = scan ?? (path => _hosting.Scan(path));
        IsAvailable = PluginEffect.IsBridgeAvailable(out string? problem);
        Problem = problem;
        SyncRegistry();
    }

    /// <summary>The plug-in list changed (scanned, blocked, unblocked). UI thread.</summary>
    public event EventHandler? Changed;

    public PluginCatalog Catalog { get; }

    /// <summary>The built-in effects and the plug-ins.</summary>
    public EffectRegistry Registry { get; }

    /// <summary>Whether plug-ins can be loaded at all (the native bridge is there).</summary>
    public bool IsAvailable { get; }

    /// <summary>Why plug-ins can't be loaded, when they can't.</summary>
    public string? Problem { get; }

    public bool IsScanning { get; private set; }

    /// <summary>How plug-ins added from now on run.</summary>
    public PluginHosting Hosting => _hosting;

    /// <summary>Whether each plug-in runs in a process of its own. Applies to plug-ins added or loaded from now on.</summary>
    public bool SeparateProcesses
    {
        get => Catalog.SeparateProcesses;
        set
        {
            if (Catalog.SeparateProcesses == value)
            {
                return;
            }

            Catalog.SeparateProcesses = value;
            _hosting = MakeHosting();
            Save();
        }
    }

    /// <summary>The built-in effects plus the plug-ins found last time (read from the file, nothing is scanned).</summary>
    public static PluginService Load(string cachePath)
    {
        var catalog = new PluginCatalog(cachePath);
        catalog.Load();
        return new PluginService(catalog, EffectRegistry.CreateDefault(), PluginHostCommand.ThisApp());
    }

    /// <summary>
    /// Looks for new and changed plug-ins in the background (each module without
    /// <c>moduleinfo.json</c> is loaded by a separate run of the app), then updates the registry on the
    /// calling thread.
    /// </summary>
    public async Task RescanAsync()
    {
        if (!IsAvailable || IsScanning)
        {
            return;
        }

        IsScanning = true;
        Changed?.Invoke(this, EventArgs.Empty);
        try
        {
            var folders = Catalog.AllFolders;
            if (await Task.Run(() => Catalog.Scan(folders, _scan)))
            {
                Save();
            }
        }
        catch (Exception ex)
        {
            // A background chore: the plug-ins already known keep working.
            Trace.WriteLine($"Looking for plug-ins failed: {ex.Message}");
        }
        finally
        {
            IsScanning = false;
        }

        SyncRegistry();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Scans a module that failed again.</summary>
    public Task RetryAsync(string modulePath)
    {
        Catalog.Forget(modulePath);
        return RescanAsync();
    }

    /// <summary>Adds a folder and scans it. Returns false when it was one already.</summary>
    public async Task<bool> AddFolderAsync(string folder)
    {
        if (!Catalog.AddFolder(folder))
        {
            return false;
        }

        Save();
        await RescanAsync();
        return true;
    }

    public async Task RemoveFolderAsync(string folder)
    {
        if (Catalog.RemoveFolder(folder))
        {
            Save();
            await RescanAsync();
        }
    }

    /// <summary>Keeps a plug-in out of the menus; saved ones load as stand-ins from the next start.</summary>
    public void Block(PluginInfo plugin, string reason)
    {
        Catalog.Block(plugin, reason);
        Save();
        SyncRegistry();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Unblock(string classId)
    {
        if (Catalog.Unblock(classId))
        {
            Save();
            SyncRegistry();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private PluginHosting MakeHosting() => new()
    {
        SeparateProcesses = Catalog.SeparateProcesses && _hostCommand is not null,
        HostCommand = _hostCommand,
        Helpers = PluginHostCommand.Helpers(),
    };

    /// <summary>Registers the catalogue's plug-ins and takes out the ones that are gone or blocked.</summary>
    private void SyncRegistry()
    {
        var current = Catalog.Plugins.Select(p => p.TypeId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var gone in Registry.Descriptors.Where(d => d.TypeId.StartsWith(PluginEffect.TypePrefix, StringComparison.OrdinalIgnoreCase) && !current.Contains(d.TypeId)).ToList())
        {
            Registry.Unregister(gone.TypeId);
        }

        Catalog.RegisterInto(Registry, () => _hosting, OnCreated);
    }

    private void OnCreated(PluginEffect effect) =>
        effect.Crashed += (_, _) => Block(effect.Info, effect.Problem ?? "It crashed.");

    private void Save()
    {
        try
        {
            Catalog.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"The plug-in list could not be saved: {ex.Message}");
        }
    }
}
