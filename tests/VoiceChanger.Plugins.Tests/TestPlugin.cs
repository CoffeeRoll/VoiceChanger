using VoiceChanger.Plugins.Native;

namespace VoiceChanger.Plugins.Tests;

/// <summary>
/// The "VoiceChanger Test Effect" built from native/testplugin, and the bridge built next to it
/// (cmake -S native -B native/build, then cmake --build native/build --config Release).
/// </summary>
internal static class TestPlugin
{
    public const string ClassName = "VoiceChanger Test Effect";

    // Its parameter ids.
    public const uint Gain = 0;
    public const uint Latency = 1;
    public const uint SidechainMix = 2;
    public const uint Bypass = 3;
    public const uint Hold = 4;
    public const uint Crash = 5;
    public const uint Program = 6;
    public const uint Hang = 7;
    public const uint EditorWidth = 8;
    public const uint EditorScale = 9;

    private static readonly Lazy<(PluginInfo? Info, string? Problem)> Found = new(Find);

    /// <summary>The folder the native build puts plug-ins in, or <c>null</c>.</summary>
    public static string? Folder { get; private set; }

    public static PluginInfo Info => Require();

    /// <summary>The test plug-in, or skips the test when it hasn't been built (fails it on CI, where it must be).</summary>
    public static PluginInfo Require()
    {
        var (info, problem) = Found.Value;
        if (info is not null)
        {
            return info;
        }

        if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Fail(problem);
        }

        Assert.Skip(problem!);
        return null!;
    }

    public static PluginEffect Open(PluginHosting? hosting = null) => PluginEffect.Open(Require(), hosting);

    /// <summary>Plug-ins in host processes of their own: VoiceChanger.PluginHost, built next to the tests.</summary>
    public static PluginHosting SeparateProcesses { get; } = new()
    {
        SeparateProcesses = true,
        HostCommand = new PluginHostCommand(
            Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "VoiceChanger.PluginHost.exe" : "VoiceChanger.PluginHost"),
            []),
    };

    /// <summary>A gain in dB as the plug-in's normalised value.</summary>
    public static double GainValue(double decibels) => (decibels + 24) / 36;

    private static (PluginInfo?, string?) Find()
    {
        if (!VcPlugin.IsAvailable(out string? problem))
        {
            return (null, problem + " Build native/ with CMake first.");
        }

        string? bundle = Environment.GetEnvironmentVariable("VOICECHANGER_TEST_PLUGIN");
        if (string.IsNullOrEmpty(bundle))
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "native", "CMakeLists.txt")))
            {
                directory = directory.Parent;
            }

            string folder = Path.Combine(directory?.FullName ?? "", "native", "build", "out", "VST3");
            bundle = Directory.Exists(folder)
                ? Directory.EnumerateDirectories(folder, "VoiceChangerTest.vst3", SearchOption.AllDirectories).FirstOrDefault()
                : null;
        }

        if (bundle is null || !Directory.Exists(bundle))
        {
            return (null, "The test plug-in (VoiceChangerTest.vst3) isn't built. Build native/ with CMake first.");
        }

        Folder = Path.GetDirectoryName(bundle);
        var info = PluginScanner.ReadModuleInfo(bundle)?.SingleOrDefault(p => p.Name == ClassName)
            ?? PluginScanner.ScanModule(bundle).Single(p => p.Name == ClassName);
        return (info, null);
    }
}
