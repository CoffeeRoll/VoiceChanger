using VoiceChanger.Plugins.Hosting;

namespace VoiceChanger.Plugins;

/// <summary>A program to start as a plug-in host: an executable and the arguments that come first.</summary>
public sealed record PluginHostCommand(string Executable, IReadOnlyList<string> Arguments)
{
    /// <summary>The running app itself (with <see cref="PluginHostServer.Command"/>), or null when its path is unknown.</summary>
    public static PluginHostCommand? ThisApp()
    {
        string? executable = Environment.ProcessPath;
        if (executable is null)
        {
            return null;
        }

        // Started as "dotnet VoiceChanger.dll": that path comes first on the command line.
        return Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? new PluginHostCommand(executable, [Environment.GetCommandLineArgs()[0]])
            : new PluginHostCommand(executable, []);
    }
}

/// <summary>How plug-ins run: in this process, or each in a host process of its own.</summary>
public sealed class PluginHosting
{
    /// <summary>Plug-ins load into this process.</summary>
    public static PluginHosting InProcess { get; } = new();

    /// <summary>Whether each plug-in runs in a separate host process, so a crash only stops that plug-in.</summary>
    public bool SeparateProcesses { get; init; }

    /// <summary>The host program, for <see cref="SeparateProcesses"/>.</summary>
    public PluginHostCommand? HostCommand { get; init; }

    /// <summary>Folders with .vstpreset files (by vendor and plug-in) for the preset menus.</summary>
    public IReadOnlyList<string> PresetFolders { get; init; } = PluginPresetFiles.DefaultFolders;

    /// <summary>Loads a plug-in as these settings say.</summary>
    /// <exception cref="PluginException">It could not be loaded.</exception>
    internal IPluginInstance Start(PluginInfo info)
    {
        if (!SeparateProcesses)
        {
            return LocalPlugin.Open(info.Path, info.ClassId);
        }

        var command = HostCommand ?? throw new PluginException("There is no plug-in host to run the plug-in in.");
        return RemotePlugin.Start(info, command);
    }
}

/// <summary>Finds a plug-in's .vstpreset files where VST3 keeps them.</summary>
public static class PluginPresetFiles
{
    /// <summary>The preset folders of this platform (each holds vendor/plug-in/*.vstpreset).</summary>
    public static IReadOnlyList<string> DefaultFolders { get; } = GetDefaultFolders();

    /// <summary>The plug-in's presets in <paramref name="folders"/>: names (with sub-folders) and paths, by name.</summary>
    public static IReadOnlyList<(string Name, string Path)> Find(PluginInfo info, IEnumerable<string> folders)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(folders);
        var presets = new List<(string Name, string Path)>();
        foreach (string folder in folders)
        {
            string directory = Path.Combine(folder, Clean(info.Vendor), Clean(info.Name));
            if (!Directory.Exists(directory))
            {
                continue;
            }

            try
            {
                foreach (string file in Directory.EnumerateFiles(directory, "*.vstpreset", SearchOption.AllDirectories).Take(1000))
                {
                    string relative = Path.GetRelativePath(directory, file);
                    string name = Path.ChangeExtension(relative, null).Replace(Path.DirectorySeparatorChar, '/').Replace("/", " / ", StringComparison.Ordinal);
                    presets.Add((name, file));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A folder we can't read has no presets for us.
            }
        }

        return presets
            .DistinctBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    // Vendor and plug-in names become folder names: without the characters folders can't have.
    private static string Clean(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
    }

    private static List<string> GetDefaultFolders()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
        {
            return
            [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "VST3 Presets"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VST3 Presets"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "VST3 Presets"),
            ];
        }

        if (OperatingSystem.IsMacOS())
        {
            return [Path.Combine(home, "Library", "Audio", "Presets"), "/Library/Audio/Presets"];
        }

        return [Path.Combine(home, ".vst3", "presets"), "/usr/share/vst3/presets", "/usr/local/share/vst3/presets"];
    }
}
