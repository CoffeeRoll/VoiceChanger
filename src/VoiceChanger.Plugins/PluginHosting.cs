using System.Runtime.InteropServices;
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

    /// <summary>
    /// Helper hosts for plug-ins built for another architecture, found next to the app: an ARM64 app
    /// runs x64 plug-ins in VoiceChanger.PluginHost-x64.exe (under Windows' emulation).
    /// </summary>
    public static IReadOnlyDictionary<Architecture, PluginHostCommand> Helpers()
    {
        var helpers = new Dictionary<Architecture, PluginHostCommand>();
        string? folder = Path.GetDirectoryName(Environment.ProcessPath);
        if (folder is null || !OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
        {
            return helpers;
        }

        string x64 = Path.Combine(folder, "VoiceChanger.PluginHost-x64.exe");
        if (File.Exists(x64))
        {
            helpers[Architecture.X64] = new PluginHostCommand(x64, []);
        }

        return helpers;
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

    /// <summary>Hosts for plug-ins this process can't load, by the architecture they are built for.</summary>
    public IReadOnlyDictionary<Architecture, PluginHostCommand> Helpers { get; init; } = new Dictionary<Architecture, PluginHostCommand>();

    /// <summary>Folders with .vstpreset files (by vendor and plug-in) for the preset menus.</summary>
    public IReadOnlyList<string> PresetFolders { get; init; } = PluginPresetFiles.DefaultFolders;

    /// <summary>The architecture of this process (the tests pretend otherwise).</summary>
    internal Architecture ProcessArchitecture { get; init; } = RuntimeInformation.ProcessArchitecture;

    /// <summary>
    /// Scans a module in a separate process: one of this architecture, or a helper for the module's.
    /// </summary>
    /// <exception cref="PluginException">The scan failed, or nothing here can load the module.</exception>
    public IReadOnlyList<PluginInfo> Scan(string modulePath)
    {
        var (_, command, problem) = Choose(modulePath, Path.GetFileName(modulePath));
        command ??= HostCommand ?? throw new PluginException(problem ?? "There is no program to scan plug-ins with.");
        return PluginScanner.ScanInSeparateProcess(modulePath, command.Executable, command.Arguments);
    }

    /// <summary>Loads a plug-in as these settings say.</summary>
    /// <exception cref="PluginException">It could not be loaded.</exception>
    internal IPluginInstance Start(PluginInfo info)
    {
        var (separate, command, problem) = Choose(info.Path, info.Name);
        if (problem is not null)
        {
            throw new PluginException(problem);
        }

        if (!separate)
        {
            return LocalPlugin.Open(info.Path, info.ClassId);
        }

        return RemotePlugin.Start(info, command ?? throw new PluginException("There is no plug-in host to run the plug-in in."));
    }

    /// <summary>
    /// Where a module runs: in this process or a host of its architecture as set, or (built for
    /// another architecture) in that architecture's helper, or nowhere, with the reason.
    /// </summary>
    internal (bool Separate, PluginHostCommand? Command, string? Problem) Choose(string modulePath, string name)
    {
        var architectures = PluginArchitectures.Of(modulePath);
        if (architectures.Count == 0 || architectures.Contains(ProcessArchitecture))
        {
            return (SeparateProcesses, HostCommand, null);
        }

        foreach (var architecture in architectures.Order())
        {
            if (Helpers.TryGetValue(architecture, out var helper))
            {
                return (true, helper, null);
            }
        }

        string built = string.Join(" and ", architectures.Order().Select(Describe));
        return (false, null, $"{name} is built for {built}, and VoiceChanger runs as {Describe(ProcessArchitecture)}.");
    }

    private static string Describe(Architecture architecture) => architecture switch
    {
        Architecture.X64 => "x64",
        Architecture.X86 => "32-bit x86",
        Architecture.Arm64 => "ARM64",
        Architecture.Arm => "32-bit ARM",
        _ => architecture.ToString(),
    };
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
