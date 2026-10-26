using VoiceChanger.Core.Audio;
using VoiceChanger.Core.Effects;
using VoiceChanger.Core.Settings;

namespace VoiceChanger.Core.Tests;

public sealed class SettingsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "VoiceChangerTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void Round_trips_through_json()
    {
        var store = new SettingsStore(Path.Combine(_directory, "settings.json"));
        var pitch = new PitchShiftEffect();
        pitch.Semitones.Value = -5;
        pitch.IsEnabled = false;
        var eq = new EqualizerEffect();
        eq.Bands[3].Value = 4.5f;

        store.Save(new AppSettings
        {
            InputDeviceId = "in",
            OutputDeviceId = "out",
            Latency = LatencyPreset.Safe,
            MonoProcessing = false,
            InputGainDb = 3,
            OutputGainDb = -2,
            EffectsEnabled = false,
            Effects = [EffectSettings.Capture(pitch), EffectSettings.Capture(eq)],
        });

        var loaded = store.Load();
        Assert.Equal("in", loaded.InputDeviceId);
        Assert.Equal("out", loaded.OutputDeviceId);
        Assert.Equal(LatencyPreset.Safe, loaded.Latency);
        Assert.False(loaded.MonoProcessing);
        Assert.Equal(3, loaded.InputGainDb);
        Assert.Equal(-2, loaded.OutputGainDb);
        Assert.False(loaded.EffectsEnabled);
        Assert.Contains("\"latency\": \"Safe\"", File.ReadAllText(store.FilePath));

        Assert.All(loaded.Effects!, e => Assert.False(e.Collapsed));
        var effects = loaded.Effects!.Select(e => e.Restore(EffectRegistry.Default)).ToList();
        var restoredPitch = Assert.IsType<PitchShiftEffect>(effects[0]);
        Assert.Equal(-5, restoredPitch.Semitones.Value);
        Assert.False(restoredPitch.IsEnabled);
        var restoredEq = Assert.IsType<EqualizerEffect>(effects[1]);
        Assert.Equal(4.5f, restoredEq.Bands[3].Value);
    }

    [Fact]
    public void Remembers_which_effects_were_collapsed()
    {
        var store = new SettingsStore(Path.Combine(_directory, "settings.json"));
        var settings = EffectSettings.Capture(new EqualizerEffect());
        settings.Collapsed = true;
        store.Save(new AppSettings { Effects = [settings] });

        Assert.True(Assert.Single(store.Load().Effects!).Collapsed);

        // Files written before the option existed load as expanded.
        File.WriteAllText(store.FilePath, """{ "effects": [ { "type": "equalizer", "enabled": true, "parameters": {} } ] }""");
        Assert.False(Assert.Single(store.Load().Effects!).Collapsed);
    }

    [Fact]
    public void Missing_or_corrupt_files_yield_defaults()
    {
        var store = new SettingsStore(Path.Combine(_directory, "nope.json"));
        var settings = store.Load();
        Assert.Null(settings.Effects);
        Assert.Equal(LatencyPreset.Low, settings.Latency);

        Directory.CreateDirectory(_directory);
        File.WriteAllText(store.FilePath, "{ not json");
        Assert.Null(store.Load().Effects);
    }

    [Fact]
    public void Unknown_effects_and_parameters_are_ignored()
    {
        var unknown = new EffectSettings { Type = "flanger" };
        Assert.Null(unknown.Restore(EffectRegistry.Default));

        var eq = new EffectSettings
        {
            Type = EqualizerEffect.Type,
            Parameters = new() { ["band0"] = 3, ["doesNotExist"] = 1, ["band1"] = 99 },
        };
        var restored = Assert.IsType<EqualizerEffect>(eq.Restore(EffectRegistry.Default));
        Assert.Equal(3, restored.Bands[0].Value);
        Assert.Equal(12, restored.Bands[1].Value);
    }

    [Fact]
    public void Registry_lists_the_built_in_effects()
    {
        var types = EffectRegistry.Default.Descriptors.Select(d => d.TypeId).ToList();
        Assert.Contains(PitchShiftEffect.Type, types);
        Assert.Contains(EqualizerEffect.Type, types);
        Assert.Throws<InvalidOperationException>(() =>
            EffectRegistry.Default.Register(new EffectDescriptor(EqualizerEffect.Type, "Dup", "", () => new EqualizerEffect())));
    }
}
