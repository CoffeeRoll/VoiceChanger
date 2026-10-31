using System.Diagnostics;
using System.Text.Json;
using VoiceChanger.Core.Effects;

namespace VoiceChanger.Plugins;

/// <summary>A scanned VST3 module, as the catalogue remembers it.</summary>
public sealed record PluginModule
{
    public required string Path { get; init; }

    /// <summary>Total size of the module's files, to notice when it changes.</summary>
    public long Size { get; init; }

    /// <summary>Newest write time of the module's files (UTC ticks), to notice when it changes.</summary>
    public long Modified { get; init; }

    public List<PluginInfo> Classes { get; init; } = [];

    /// <summary>Why the module couldn't be scanned (it is then left out until it changes), or <c>null</c>.</summary>
    public string? Error { get; init; }
}

/// <summary>A plug-in kept out of the menus (it crashed, or the user said so).</summary>
public sealed record BlockedPlugin(string ClassId, string Name, string Vendor, string Reason);

/// <summary>What <see cref="PluginCatalog"/> keeps in its file: the scan results and the user's choices.</summary>
public sealed class PluginCache
{
    public int Version { get; set; } = PluginCatalog.CacheVersion;

    public List<PluginModule> Modules { get; set; } = [];

    /// <summary>Folders the user added to the standard ones.</summary>
    public List<string> Folders { get; set; } = [];

    public List<BlockedPlugin> Blocked { get; set; } = [];

    /// <summary>Whether each plug-in runs in a process of its own.</summary>
    public bool SeparateProcesses { get; set; } = true;
}

/// <summary>
/// The VST3 effects installed on this machine. Scanning looks through the plug-in folders, reads
/// each new or changed module's <c>moduleinfo.json</c> or, when it has none, loads it (in a separate
/// process, through the scan function given), and remembers the results in a cache file so the next
/// start doesn't scan again. The file also keeps the folders the user added, the plug-ins kept out
/// of the menus, and whether plug-ins run in processes of their own.
/// </summary>
public sealed class PluginCatalog(string cachePath)
{
    public const int CacheVersion = 1;

    private readonly object _gate = new();
    private IReadOnlyList<PluginModule> _modules = [];
    private List<string> _folders = [];
    private List<BlockedPlugin> _blocked = [];

    /// <summary>Where the app keeps the cache: next to its settings.</summary>
    public static string DefaultCachePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VoiceChanger",
        "plugins.json");

    /// <summary>The standard VST3 folders of this platform.</summary>
    public static IReadOnlyList<string> DefaultFolders { get; } = GetDefaultFolders();

    public string CachePath { get; } = cachePath;

    public IReadOnlyList<PluginModule> Modules => Volatile.Read(ref _modules);

    /// <summary>Every effect of the modules that scanned fine and isn't blocked, by name.</summary>
    public IReadOnlyList<PluginInfo> Plugins =>
        AllPlugins.Where(p => !IsBlocked(p.ClassId)).ToList();

    /// <summary>Every effect of the modules that scanned fine, blocked or not, by name.</summary>
    public IReadOnlyList<PluginInfo> AllPlugins =>
        Modules.Where(m => m.Error is null)
            .SelectMany(m => m.Classes)
            .DistinctBy(p => p.ClassId, StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    /// <summary>Modules that couldn't be scanned, with the reason.</summary>
    public IReadOnlyList<PluginModule> FailedModules => Modules.Where(m => m.Error is not null).ToList();

    /// <summary>Folders the user added to <see cref="DefaultFolders"/>.</summary>
    public IReadOnlyList<string> ExtraFolders
    {
        get
        {
            lock (_gate)
            {
                return [.. _folders];
            }
        }
    }

    /// <summary>The standard folders and the user's.</summary>
    public IReadOnlyList<string> AllFolders => DefaultFolders.Concat(ExtraFolders).Distinct(PathComparer).ToList();

    public IReadOnlyList<BlockedPlugin> Blocked
    {
        get
        {
            lock (_gate)
            {
                return [.. _blocked];
            }
        }
    }

    /// <summary>Whether each plug-in runs in a process of its own (the safer way).</summary>
    public bool SeparateProcesses { get; set; } = true;

    /// <summary>Adds a folder to look for plug-ins in. Returns false when it is already one of them.</summary>
    public bool AddFolder(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        string full = Path.GetFullPath(folder);
        lock (_gate)
        {
            if (AllFolders.Contains(full, PathComparer))
            {
                return false;
            }

            _folders.Add(full);
            return true;
        }
    }

    public bool RemoveFolder(string folder)
    {
        lock (_gate)
        {
            return _folders.RemoveAll(f => PathComparer.Equals(f, folder)) > 0;
        }
    }

    public bool IsBlocked(string classId)
    {
        lock (_gate)
        {
            return _blocked.Any(b => string.Equals(b.ClassId, classId, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>Keeps a plug-in out of the menus (a saved one loads as a stand-in) until unblocked.</summary>
    public void Block(PluginInfo plugin, string reason)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        lock (_gate)
        {
            if (!IsBlocked(plugin.ClassId))
            {
                _blocked.Add(new BlockedPlugin(plugin.ClassId, plugin.Name, plugin.Vendor, reason));
            }
        }
    }

    public bool Unblock(string classId)
    {
        lock (_gate)
        {
            return _blocked.RemoveAll(b => string.Equals(b.ClassId, classId, StringComparison.OrdinalIgnoreCase)) > 0;
        }
    }

    /// <summary>Forgets what a scan found out about a module, so the next scan looks at it again.</summary>
    public bool Forget(string modulePath)
    {
        lock (_gate)
        {
            var modules = Modules.Where(m => !PathComparer.Equals(m.Path, modulePath)).ToList();
            bool forgotten = modules.Count != Modules.Count;
            Volatile.Write(ref _modules, modules);
            return forgotten;
        }
    }

    /// <summary>Reads the cache file. A missing or damaged file leaves the catalogue empty.</summary>
    public void Load()
    {
        PluginCache? cache = null;
        try
        {
            if (File.Exists(CachePath))
            {
                using var stream = File.OpenRead(CachePath);
                cache = JsonSerializer.Deserialize(stream, PluginJsonContext.Default.PluginCache);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            Trace.WriteLine($"The plug-in list could not be read: {ex.Message}");
        }

        if (cache?.Version != CacheVersion)
        {
            cache = new PluginCache();
        }

        lock (_gate)
        {
            Volatile.Write(ref _modules, (cache.Modules ?? []).Where(m => !string.IsNullOrEmpty(m.Path)).ToList());
            _folders = (cache.Folders ?? []).Where(f => !string.IsNullOrWhiteSpace(f)).ToList();
            _blocked = (cache.Blocked ?? []).Where(b => !string.IsNullOrEmpty(b.ClassId)).ToList();
            SeparateProcesses = cache.SeparateProcesses;
        }
    }

    public void Save()
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(CachePath));
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        string temporary = CachePath + ".tmp";
        using (var stream = File.Create(temporary))
        {
            var cache = new PluginCache
            {
                Modules = [.. Modules],
                Folders = [.. ExtraFolders],
                Blocked = [.. Blocked],
                SeparateProcesses = SeparateProcesses,
            };
            JsonSerializer.Serialize(stream, cache, PluginJsonContext.Default.PluginCache);
        }

        File.Move(temporary, CachePath, overwrite: true);
    }

    /// <summary>
    /// Brings the catalogue up to date with the modules in <paramref name="folders"/>: new and changed
    /// ones are scanned (through their <c>moduleinfo.json</c>, or else <paramref name="scan"/>), ones
    /// that are gone are dropped. Any thread. Returns whether anything changed.
    /// </summary>
    public bool Scan(IEnumerable<string> folders, Func<string, IReadOnlyList<PluginInfo>> scan, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(scan);

        var known = Modules.ToDictionary(m => m.Path, PathComparer);
        var modules = new List<PluginModule>();
        bool changed = false;
        foreach (string path in folders.SelectMany(FindModules).Distinct(PathComparer))
        {
            cancellation.ThrowIfCancellationRequested();
            var (size, modified) = Fingerprint(path);
            if (known.Remove(path, out var cached) && cached.Size == size && cached.Modified == modified)
            {
                modules.Add(cached);
                continue;
            }

            modules.Add(ScanModule(path, size, modified, scan));
            changed = true;
        }

        changed |= known.Count > 0;
        if (changed)
        {
            lock (_gate)
            {
                Volatile.Write(ref _modules, modules);
            }
        }

        return changed;
    }

    /// <summary>
    /// Adds the effects not registered yet to <paramref name="registry"/> (so they also become
    /// nodes). Returns the ones added.
    /// </summary>
    public IReadOnlyList<EffectDescriptor> RegisterInto(EffectRegistry registry, PluginHosting? hosting = null, Action<PluginEffect>? created = null) =>
        RegisterInto(registry, () => hosting ?? PluginHosting.InProcess, created);

    /// <summary>
    /// Adds the effects not registered yet to <paramref name="registry"/>, each running as
    /// <paramref name="hosting"/> says at the time it is made. Returns the ones added.
    /// </summary>
    public IReadOnlyList<EffectDescriptor> RegisterInto(EffectRegistry registry, Func<PluginHosting> hosting, Action<PluginEffect>? created = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var added = new List<EffectDescriptor>();
        foreach (var plugin in Plugins)
        {
            if (registry.Find(plugin.TypeId) is not null)
            {
                continue;
            }

            var descriptor = Describe(plugin, hosting, created);
            registry.Register(descriptor);
            added.Add(descriptor);
        }

        return added;
    }

    /// <summary>
    /// How a plug-in appears in the effect and node lists: it runs as <paramref name="hosting"/> says,
    /// and <paramref name="created"/> hears of each effect made.
    /// </summary>
    public static EffectDescriptor Describe(PluginInfo plugin, PluginHosting? hosting = null, Action<PluginEffect>? created = null) =>
        Describe(plugin, () => hosting ?? PluginHosting.InProcess, created);

    /// <summary>How a plug-in appears in the effect and node lists, running as <paramref name="hosting"/> says when it is made.</summary>
    public static EffectDescriptor Describe(PluginInfo plugin, Func<PluginHosting> hosting, Action<PluginEffect>? created = null)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        ArgumentNullException.ThrowIfNull(hosting);
        string vendor = string.IsNullOrWhiteSpace(plugin.Vendor) ? "an unknown vendor" : plugin.Vendor;
        AudioEffect Create()
        {
            var effect = PluginEffect.Create(plugin, hosting());
            if (effect is PluginEffect loaded)
            {
                created?.Invoke(loaded);
            }

            return effect;
        }

        return new EffectDescriptor(plugin.TypeId, plugin.Name, $"VST3 plug-in by {vendor}.", Create)
        {
            Group = string.IsNullOrWhiteSpace(plugin.Vendor) ? "Plug-ins" : plugin.Vendor,
        };
    }

    /// <summary>The .vst3 bundles and files under <paramref name="folder"/>.</summary>
    public static IEnumerable<string> FindModules(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            yield break;
        }

        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((folder, 0));
        while (pending.Count > 0)
        {
            var (directory, depth) = pending.Pop();
            string[] entries;
            try
            {
                entries = Directory.GetFileSystemEntries(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            Array.Sort(entries, StringComparer.OrdinalIgnoreCase);
            foreach (string entry in entries)
            {
                bool isModule = entry.EndsWith(".vst3", StringComparison.OrdinalIgnoreCase);
                if (Directory.Exists(entry))
                {
                    if (isModule)
                    {
                        yield return entry;
                    }
                    else if (depth < 8)
                    {
                        pending.Push((entry, depth + 1));
                    }
                }
                else if (isModule)
                {
                    yield return entry;
                }
            }
        }
    }

    /// <summary>Size and newest write time of a module file, or of the files in a bundle's <c>Contents</c> folders.</summary>
    internal static (long Size, long Modified) Fingerprint(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var file = new FileInfo(path);
                return (file.Length, file.LastWriteTimeUtc.Ticks);
            }

            long size = 0;
            long modified = 0;
            string contents = Path.Combine(path, "Contents");
            if (Directory.Exists(contents))
            {
                foreach (string directory in Directory.EnumerateDirectories(contents))
                {
                    foreach (string name in Directory.EnumerateFiles(directory))
                    {
                        var file = new FileInfo(name);
                        size += file.Length;
                        modified = Math.Max(modified, file.LastWriteTimeUtc.Ticks);
                    }
                }
            }

            return (size, modified);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (0, 0);
        }
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static PluginModule ScanModule(string path, long size, long modified, Func<string, IReadOnlyList<PluginInfo>> scan)
    {
        try
        {
            var classes = PluginScanner.ReadModuleInfo(path) ?? scan(path);
            return new PluginModule { Path = path, Size = size, Modified = modified, Classes = [.. classes] };
        }
        catch (Exception ex) when (ex is PluginException or IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Trace.WriteLine($"Scanning {path} failed: {ex.Message}");
            return new PluginModule { Path = path, Size = size, Modified = modified, Error = ex.Message };
        }
    }

    private static List<string> GetDefaultFolders()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
        {
            return
            [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles), "VST3"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Common", "VST3"),
            ];
        }

        if (OperatingSystem.IsMacOS())
        {
            return [Path.Combine(home, "Library", "Audio", "Plug-Ins", "VST3"), "/Library/Audio/Plug-Ins/VST3"];
        }

        return [Path.Combine(home, ".vst3"), "/usr/lib/vst3", "/usr/local/lib/vst3"];
    }
}
