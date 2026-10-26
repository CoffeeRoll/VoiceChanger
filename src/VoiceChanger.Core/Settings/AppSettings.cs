using System.Text.Json;
using System.Text.Json.Serialization;
using VoiceChanger.Core.Audio;
using VoiceChanger.Core.Effects;

namespace VoiceChanger.Core.Settings;

/// <summary>Everything the application remembers between runs.</summary>
public sealed class AppSettings
{
    public string? InputDeviceId { get; set; }

    public string? OutputDeviceId { get; set; }

    public LatencyPreset Latency { get; set; } = LatencyPreset.Low;

    public bool MonoProcessing { get; set; } = true;

    public float InputGainDb { get; set; }

    public float OutputGainDb { get; set; }

    public bool EffectsEnabled { get; set; } = true;

    /// <summary>The effect chain, in processing order. <c>null</c> means "never saved" (first run).</summary>
    public List<EffectSettings>? Effects { get; set; }
}

/// <summary>Saved state of one effect in the chain.</summary>
public sealed class EffectSettings
{
    public string Type { get; set; } = "";

    public bool Enabled { get; set; } = true;

    /// <summary>Whether the effect's card was collapsed in the window.</summary>
    public bool Collapsed { get; set; }

    public Dictionary<string, float> Parameters { get; set; } = [];

    public static EffectSettings Capture(AudioEffect effect)
    {
        ArgumentNullException.ThrowIfNull(effect);
        return new EffectSettings
        {
            Type = effect.TypeId,
            Enabled = effect.IsEnabled,
            Parameters = effect.Parameters.ToDictionary(p => p.Id, p => p.Value),
        };
    }

    /// <summary>Creates the effect described by these settings, or <c>null</c> if the type is unknown.</summary>
    public AudioEffect? Restore(EffectRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var effect = registry.Create(Type);
        if (effect is null)
        {
            return null;
        }

        foreach (var (id, value) in Parameters ?? [])
        {
            effect.FindParameter(id)?.Value = value;
        }

        effect.IsEnabled = Enabled;
        return effect;
    }
}

/// <summary>Loads and saves <see cref="AppSettings"/> as JSON.</summary>
public sealed class SettingsStore(string filePath)
{
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VoiceChanger",
        "settings.json");

    public string FilePath { get; } = filePath;

    /// <summary>Returns the saved settings, or defaults when there are none or the file is unreadable.</summary>
    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return new AppSettings();
            }

            using var stream = File.OpenRead(FilePath);
            return JsonSerializer.Deserialize(stream, SettingsJsonContext.Default.AppSettings) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string? directory = Path.GetDirectoryName(Path.GetFullPath(FilePath));
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        // Write to a temporary file first so a crash never leaves a truncated settings file behind.
        string temporary = FilePath + ".tmp";
        using (var stream = File.Create(temporary))
        {
            JsonSerializer.Serialize(stream, settings, SettingsJsonContext.Default.AppSettings);
        }

        File.Move(temporary, FilePath, overwrite: true);
    }
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    UseStringEnumConverter = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
