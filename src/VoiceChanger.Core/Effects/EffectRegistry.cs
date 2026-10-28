namespace VoiceChanger.Core.Effects;

/// <summary>Describes an effect type that can be added to a chain.</summary>
public sealed record EffectDescriptor(string TypeId, string Name, string Description, Func<AudioEffect> Create)
{
    /// <summary>Where menus list it: <c>null</c> for the built-in effects, the vendor for a plug-in.</summary>
    public string? Group { get; init; }

    public override string ToString() => Name;
}

/// <summary>Catalogue of available effect types. New effects only need to be registered here.</summary>
public sealed class EffectRegistry
{
    private readonly List<EffectDescriptor> _descriptors = [];

    public static EffectRegistry Default { get; } = CreateDefault();

    public IReadOnlyList<EffectDescriptor> Descriptors => _descriptors;

    public static EffectRegistry CreateDefault()
    {
        var registry = new EffectRegistry();
        registry.Register(new EffectDescriptor(
            PitchShiftEffect.Type,
            "Pitch Shifter",
            "Makes the voice higher or lower without changing its speed.",
            () => new PitchShiftEffect()));
        registry.Register(new EffectDescriptor(
            AutoTuneEffect.Type,
            "Auto Tune",
            "Pulls the voice to the nearest note of a key and scale.",
            () => new AutoTuneEffect()));
        registry.Register(new EffectDescriptor(
            EqualizerEffect.Type,
            "Equalizer",
            "Ten band graphic equalizer with low and high cut filters.",
            () => new EqualizerEffect()));
        registry.Register(new EffectDescriptor(
            VocoderEffect.Type,
            "Vocoder",
            "Your voice shapes another sound: a robot, a choir, a whisper, or any instrument plugged into its Carrier socket in the node editor.",
            () => new VocoderEffect()));
        return registry;
    }

    public void Register(EffectDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (Find(descriptor.TypeId) is not null)
        {
            throw new InvalidOperationException($"An effect with type id '{descriptor.TypeId}' is already registered.");
        }

        _descriptors.Add(descriptor);
    }

    /// <summary>Takes an effect type out (a plug-in that was blocked). Returns whether it was there.</summary>
    public bool Unregister(string typeId) =>
        _descriptors.RemoveAll(d => string.Equals(d.TypeId, typeId, StringComparison.OrdinalIgnoreCase)) > 0;

    public EffectDescriptor? Find(string typeId) =>
        _descriptors.FirstOrDefault(d => string.Equals(d.TypeId, typeId, StringComparison.OrdinalIgnoreCase));

    public AudioEffect? Create(string typeId) => Find(typeId)?.Create();
}
