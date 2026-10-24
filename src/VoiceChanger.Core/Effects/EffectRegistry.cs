namespace VoiceChanger.Core.Effects;

/// <summary>Describes an effect type that can be added to a chain.</summary>
public sealed record EffectDescriptor(string TypeId, string Name, string Description, Func<AudioEffect> Create)
{
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
            EqualizerEffect.Type,
            "Equalizer",
            "Ten band graphic equalizer with low and high cut filters.",
            () => new EqualizerEffect()));
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

    public EffectDescriptor? Find(string typeId) =>
        _descriptors.FirstOrDefault(d => string.Equals(d.TypeId, typeId, StringComparison.OrdinalIgnoreCase));

    public AudioEffect? Create(string typeId) => Find(typeId)?.Create();
}
