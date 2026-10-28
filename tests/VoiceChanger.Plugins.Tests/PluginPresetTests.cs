using VoiceChanger.Core.Effects;

namespace VoiceChanger.Plugins.Tests;

public sealed class PluginPresetTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "vc-presets-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void Programs_are_presets()
    {
        using var effect = TestPlugin.Open(new PluginHosting { PresetFolders = [] });
        Assert.Equal(["Default", "Loud", "Crushed"], effect.Presets.Select(p => p.Name));
        Assert.Null(effect.FindMatchingPreset());

        effect.ApplyPreset(effect.Presets[1]);
        Assert.Equal(TestPlugin.GainValue(6), effect.GetParameter("0").Value, 5);
        Assert.Same(effect.Presets[1], effect.FindMatchingPreset());

        // The processor got the program too.
        effect.Prepare(48000, 1);
        var block = Enumerable.Repeat(0.1f, 64).ToArray();
        effect.Process(block);
        Assert.All(block, sample => Assert.Equal(0.1f * MathF.Pow(10, 6 / 20f), sample, 4));

        effect.ApplyPreset(effect.Presets[2]);
        Assert.Equal(3, effect.GetParameter("4").Value);

        // Changing a setting leaves the preset.
        effect.GetParameter("4").Value = 1;
        Assert.Null(effect.FindMatchingPreset());
    }

    [Fact]
    public void Preset_files_are_saved_and_listed_with_their_folders()
    {
        var info = TestPlugin.Require();
        string folder = Path.Combine(_directory, "VoiceChanger", "VoiceChanger Test Effect", "Mine");
        Directory.CreateDirectory(folder);
        var hosting = new PluginHosting { PresetFolders = [_directory] };

        using (var effect = TestPlugin.Open(hosting))
        {
            // Edits the processor hasn't had yet are in the file too.
            effect.GetParameter("0").Value = (float)TestPlugin.GainValue(-9);
            effect.GetParameter("4").Value = 2;
            Assert.Null(effect.SavePreset(Path.Combine(folder, "Warm.vstpreset")));
        }

        File.WriteAllText(Path.Combine(folder, "Broken.vstpreset"), "not a preset");

        using var reopened = TestPlugin.Open(hosting);
        Assert.Equal(["Default", "Loud", "Crushed", "Mine / Broken", "Mine / Warm"], reopened.Presets.Select(p => p.Name));

        var warm = reopened.Presets.Single(p => p.Name == "Mine / Warm");
        reopened.ApplyPreset(warm);
        Assert.Equal(TestPlugin.GainValue(-9), reopened.GetParameter("0").Value, 5);
        Assert.Equal(2, reopened.GetParameter("4").Value);
        Assert.Same(warm, reopened.FindMatchingPreset());

        // A file that isn't a preset changes nothing.
        reopened.ApplyPreset(reopened.Presets.Single(p => p.Name == "Mine / Broken"));
        Assert.Equal(2, reopened.GetParameter("4").Value);
        Assert.Same(warm, reopened.FindMatchingPreset());
        Assert.Equal(info.TypeId, reopened.TypeId);
    }
}
