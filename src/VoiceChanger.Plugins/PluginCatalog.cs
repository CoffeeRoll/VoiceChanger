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

/// <summary>What <see cref="PluginCatalog"/> keeps in its cache file.</summary>
public sealed class PluginCache
{
    public int Version { get; set; } = PluginCatalog.CacheVersion;

    public List<PluginModule> Modules { get; set; } = [];
}

/// <summary>
/// The VST3 effects installed on this machine. Scanning looks through the plug-in folders, reads
/// each new or changed module's <c>moduleinfo.json</c> or, when it has none, loads it (in a separate
/// process, through the scan function given), and remembers the results in a cache file so the next
/// start doesn't scan again.
/// </summary>
public sealed class PluginCatalog(string cachePath)
{
    public const int CacheVersion = 1;

    private readonly object _gate = new();
    private IReadOnlyList<PluginModule> _modules = [];

    /// <summary>Where the app keeps the cache: next to its settings.</summary>
    public static string DefaultCachePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VoiceChanger",
        "plugins.json");

    /// <summary>The standard VST3 folders of this platform.</summary>
    public static IReadOnlyList<string> DefaultFolders { get; } = GetDefaultFolders();

    public string CachePath { get; } = cachePath;

    public IReadOnlyList<PluginModule> Modules => Volatile.Read(ref _modules);

    /// <summary>Every effect of the modules that scanned fine, by name.</summary>
    public IReadOnlyList<PluginInfo> Plugins =>
        Modules.Where(m => m.Error is null)
            .SelectMany(m => m.Classes)
            .DistinctBy(p => p.ClassId, StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    /// <summary>
    /// Scans in a separate process: the running app started again with
    /// <see cref="PluginScanner.Command"/>, so a plug-in that crashes while loading can't take the
    /// app with it.
    /// </summary>
    public static IReadOnlyList<PluginInfo> ScanWithThisApp(string modulePath)
    {
        string executable = Environment.ProcessPath ?? throw new PluginException("The app's path is unknown.");
        string[] arguments = [];
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            // Started as "dotnet VoiceChanger.dll": that path comes first on the command line.
            arguments = [Environment.GetCommandLineArgs()[0]];
        }

        return PluginScanner.ScanInSeparateProcess(modulePath, executable, arguments);
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

        var modules = cache?.Version == CacheVersion ? cache.Modules : [];
        Volatile.Write(ref _modules, modules.Where(m => !string.IsNullOrEmpty(m.Path)).ToList());
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
            JsonSerializer.Serialize(stream, new PluginCache { Modules = [.. Modules] }, PluginJsonContext.Default.PluginCache);
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
    public IReadOnlyList<EffectDescriptor> RegisterInto(EffectRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var added = new List<EffectDescriptor>();
        foreach (var plugin in Plugins)
        {
            if (registry.Find(plugin.TypeId) is not null)
            {
                continue;
            }

            var descriptor = Describe(plugin);
            registry.Register(descriptor);
            added.Add(descriptor);
        }

        return added;
    }

    /// <summary>How a plug-in appears in the effect and node lists.</summary>
    public static EffectDescriptor Describe(PluginInfo plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        string vendor = string.IsNullOrWhiteSpace(plugin.Vendor) ? "an unknown vendor" : plugin.Vendor;
        return new EffectDescriptor(plugin.TypeId, plugin.Name, $"VST3 plug-in by {vendor}.", () => PluginEffect.Create(plugin))
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
