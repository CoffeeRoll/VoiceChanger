using System.Runtime.InteropServices;
using static VoiceChanger.Plugins.Tests.TestAudio;

namespace VoiceChanger.Plugins.Tests;

public sealed class PluginArchitectureTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "vc-arch-" + Guid.NewGuid().ToString("N"));

    public PluginArchitectureTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void A_bundle_is_for_the_architectures_of_its_folders()
    {
        Assert.Equal([Architecture.X64], PluginArchitectures.Of(Bundle("x86_64-win", "Resources")));
        Assert.Equal([Architecture.Arm64], PluginArchitectures.Of(Bundle("arm64-win")));
        Assert.Equal([Architecture.X64, Architecture.Arm64], PluginArchitectures.Of(Bundle("arm64x-win")).Order());
        Assert.Equal([Architecture.X64, Architecture.Arm64], PluginArchitectures.Of(Bundle("x86_64-linux", "aarch64-linux")).Order());
        Assert.Empty(PluginArchitectures.Of(Bundle("Resources")));
        Assert.Empty(PluginArchitectures.Of(Path.Combine(_directory, "Nowhere.vst3")));
    }

    [Fact]
    public void A_single_file_module_is_for_the_machine_in_its_header()
    {
        Assert.Equal([Architecture.X64], PluginArchitectures.Of(PortableExecutable("x64.vst3", 0x8664)));
        Assert.Equal([Architecture.Arm64], PluginArchitectures.Of(PortableExecutable("arm64.vst3", 0xAA64)));

        string text = Path.Combine(_directory, "text.vst3");
        File.WriteAllText(text, "not a binary");
        Assert.Empty(PluginArchitectures.Of(text));
    }

    [Fact]
    public void A_plugin_for_another_architecture_goes_to_its_helper_or_is_explained()
    {
        string module = Bundle("x86_64-win");
        var helper = new PluginHostCommand("helper-x64", []);
        var app = new PluginHostCommand("app", []);

        var native = new PluginHosting { ProcessArchitecture = Architecture.X64, HostCommand = app };
        Assert.Equal((false, app, null), native.Choose(module, "Fake"));

        var emulated = new PluginHosting { ProcessArchitecture = Architecture.Arm64, HostCommand = app, Helpers = new Dictionary<Architecture, PluginHostCommand> { [Architecture.X64] = helper } };
        Assert.Equal((true, helper, null), emulated.Choose(module, "Fake"));

        var stranded = new PluginHosting { ProcessArchitecture = Architecture.Arm64, HostCommand = app };
        var (_, command, problem) = stranded.Choose(module, "Fake");
        Assert.Null(command);
        Assert.Equal("Fake is built for x64, and VoiceChanger runs as ARM64.", problem);
    }

    [Fact]
    public void The_helper_scans_and_runs_a_plugin_this_process_cannot_load()
    {
        var info = TestPlugin.Require();
        var current = RuntimeInformation.ProcessArchitecture;

        // Pretend the app is built for another architecture: the test plug-in (built for this one)
        // must run in the "helper", here the plug-in host built next to the tests.
        var hosting = new PluginHosting
        {
            ProcessArchitecture = current == Architecture.Arm64 ? Architecture.X64 : Architecture.Arm64,
            Helpers = new Dictionary<Architecture, PluginHostCommand> { [current] = TestPlugin.SeparateProcesses.HostCommand! },
        };

        var found = Assert.Single(hosting.Scan(info.Path));
        Assert.Equal(info.ClassId, found.ClassId);

        using var effect = TestPlugin.Open(hosting);
        Assert.True(effect.RunsInSeparateProcess);
        effect.Prepare(48000, 2);
        var input = Sine(256, 2);
        AssertClose(input, Run(effect, input), 1e-6f);
    }

    private string Bundle(params string[] folders)
    {
        string bundle = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".vst3");
        foreach (string folder in folders)
        {
            Directory.CreateDirectory(Path.Combine(bundle, "Contents", folder));
        }

        if (folders.Length == 0)
        {
            Directory.CreateDirectory(bundle);
        }

        return bundle;
    }

    /// <summary>The start of a Windows DLL for <paramref name="machine"/>: enough header to tell.</summary>
    private string PortableExecutable(string name, ushort machine)
    {
        var bytes = new byte[0x100];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        BitConverter.TryWriteBytes(bytes.AsSpan(0x3C), 0x80);
        "PE\0\0"u8.CopyTo(bytes.AsSpan(0x80));
        BitConverter.TryWriteBytes(bytes.AsSpan(0x84), machine);
        string path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
