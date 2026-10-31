using System.Text.Json.Serialization;

namespace VoiceChanger.Plugins;

/// <summary>One effect of a VST3 module, as a scan finds it.</summary>
public sealed record PluginInfo
{
    /// <summary>The module: a .vst3 bundle (folder) or file.</summary>
    public required string Path { get; init; }

    /// <summary>The effect's class id, 32 hexadecimal digits as the module lists it.</summary>
    public required string ClassId { get; init; }

    public required string Name { get; init; }

    public string Vendor { get; init; } = "";

    public string Version { get; init; } = "";

    public string SdkVersion { get; init; } = "";

    /// <summary>The plug-in's categories, e.g. <c>Fx|Delay</c>.</summary>
    public string Subcategories { get; init; } = "";

    /// <summary>The effect type id it is registered and saved under.</summary>
    [JsonIgnore]
    public string TypeId => PluginEffect.TypePrefix + ClassId;

    /// <summary>
    /// Whether this is an effect VoiceChanger can use. Modules also list instruments, which have no
    /// audio input.
    /// </summary>
    [JsonIgnore]
    public bool IsEffect
    {
        get
        {
            var categories = Subcategories.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return categories.Contains("Fx", StringComparer.OrdinalIgnoreCase)
                || !categories.Contains("Instrument", StringComparer.OrdinalIgnoreCase);
        }
    }

    public override string ToString() => string.IsNullOrEmpty(Vendor) ? Name : $"{Name} ({Vendor})";
}
