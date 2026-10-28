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

    /// <summary>Whether the window shows the effect chain or the node editor.</summary>
    public EditorMode Mode { get; set; } = EditorMode.Chain;

    /// <summary>The node graph. <c>null</c> until the node editor was opened for the first time.</summary>
    public GraphSettings? Graph { get; set; }
}

/// <summary>Saved state of one effect in the chain.</summary>
public sealed class EffectSettings
{
    public string Type { get; set; } = "";

    public bool Enabled { get; set; } = true;

    /// <summary>Whether the effect's card was collapsed in the window.</summary>
    public bool Collapsed { get; set; }

    public Dictionary<string, float> Parameters { get; set; } = [];

    /// <summary>A plug-in's name, so it can be named when it is missing.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }

    /// <summary>A plug-in's own state (base64), see <see cref="IStatefulEffect"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? State { get; set; }

    public static EffectSettings Capture(AudioEffect effect)
    {
        ArgumentNullException.ThrowIfNull(effect);
        return new EffectSettings
        {
            Type = effect.TypeId,
            Enabled = effect.IsEnabled,
            Parameters = SavedEffect.ParametersOf(effect),
            Name = SavedEffect.NameOf(effect),
            State = SavedEffect.StateOf(effect),
        };
    }

    /// <summary>
    /// Creates the effect described by these settings: a <see cref="MissingEffect"/> for a plug-in
    /// that isn't installed or doesn't load, or <c>null</c> if the type is unknown.
    /// </summary>
    public AudioEffect? Restore(EffectRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var effect = registry.Create(Type);
        if (effect is null or MissingEffect)
        {
            effect = SavedEffect.StandIn(Type, Name, Parameters, State, hasSidechain: false);
        }
        else
        {
            SavedEffect.Apply(effect, Parameters, State);
        }

        effect?.IsEnabled = Enabled;
        return effect;
    }
}

/// <summary>What <see cref="EffectSettings"/> and <see cref="NodeSettings"/> share about saving effects.</summary>
internal static class SavedEffect
{
    public static Dictionary<string, float> ParametersOf(AudioEffect effect) =>
        effect is MissingEffect missing
            ? new Dictionary<string, float>(missing.SavedParameters)
            : effect.Parameters.ToDictionary(p => p.Id, p => p.Value);

    public static string? NameOf(AudioEffect effect) =>
        effect is MissingEffect missing ? missing.SavedName
        : MissingEffect.CanStandIn(effect.TypeId) ? effect.Name
        : null;

    public static string? StateOf(AudioEffect effect) =>
        effect is IStatefulEffect stateful && stateful.SaveState() is { } state ? Convert.ToBase64String(state) : null;

    /// <summary>A stand-in that keeps what was saved, for a plug-in; <c>null</c> for anything else.</summary>
    public static MissingEffect? StandIn(string type, string? name, Dictionary<string, float>? parameters, string? state, bool hasSidechain) =>
        MissingEffect.CanStandIn(type) ? MissingEffect.Create(type, name, parameters, Decode(state), hasSidechain) : null;

    /// <summary>
    /// Sets up a freshly created effect from what was saved: its state when it has one and accepts
    /// it, otherwise the parameter values.
    /// </summary>
    public static void Apply(AudioEffect effect, Dictionary<string, float>? parameters, string? state)
    {
        if (Decode(state) is { } bytes && effect is IStatefulEffect stateful && stateful.LoadState(bytes))
        {
            return;
        }

        // In the effect's own order, so settings that add ports (the mixer's input count) come first.
        var values = new Dictionary<string, float>(parameters ?? [], StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in effect.Parameters)
        {
            if (values.TryGetValue(parameter.Id, out float value))
            {
                parameter.Value = value;
            }
        }
    }

    private static byte[]? Decode(string? state)
    {
        if (string.IsNullOrEmpty(state))
        {
            return null;
        }

        try
        {
            return Convert.FromBase64String(state);
        }
        catch (FormatException)
        {
            return null;
        }
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
