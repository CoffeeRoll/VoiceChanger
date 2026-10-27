using VoiceChanger.Core.Effects;

namespace VoiceChanger.Core.Graph;

/// <summary>Describes a node type that can be added to a graph.</summary>
public sealed record NodeDescriptor(string TypeId, string Name, string Description, NodeCategory Category, Func<AudioNode> Create)
{
    public override string ToString() => Name;
}

/// <summary>Catalogue of node types: devices, generators, the mixer and one node per registered effect.</summary>
public sealed class NodeRegistry
{
    private readonly List<NodeDescriptor> _descriptors = [];

    public static NodeRegistry Default { get; } = CreateDefault(EffectRegistry.Default);

    public IReadOnlyList<NodeDescriptor> Descriptors => _descriptors;

    public static NodeRegistry CreateDefault(EffectRegistry effects)
    {
        ArgumentNullException.ThrowIfNull(effects);
        var registry = new NodeRegistry();
        registry.Register(new NodeDescriptor(
            InputDeviceNode.Type,
            "Audio Input",
            "Audio from a microphone, line input or another recording device.",
            NodeCategory.Source,
            () => new InputDeviceNode()));
        registry.Register(new NodeDescriptor(
            ToneNode.Type,
            "Tone Generator",
            "A steady tone or noise: handy for trying out a patch, or as a drone under your voice.",
            NodeCategory.Source,
            () => new ToneNode()));
        foreach (var effect in effects.Descriptors)
        {
            registry.Register(new NodeDescriptor(
                EffectNode.TypePrefix + effect.TypeId,
                effect.Name,
                effect.Description,
                NodeCategory.Effect,
                () => new EffectNode(effect.Create())));
        }

        registry.Register(new NodeDescriptor(
            MixerNode.Type,
            "Mixer",
            "Combines up to eight signals into one, each with its own volume.",
            NodeCategory.Mixing,
            () => new MixerNode()));
        registry.Register(new NodeDescriptor(
            OutputDeviceNode.Type,
            "Audio Output",
            "Plays the signal on speakers, headphones or a virtual cable.",
            NodeCategory.Output,
            () => new OutputDeviceNode()));
        return registry;
    }

    public void Register(NodeDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (Find(descriptor.TypeId) is not null)
        {
            throw new InvalidOperationException($"A node with type id '{descriptor.TypeId}' is already registered.");
        }

        _descriptors.Add(descriptor);
    }

    public NodeDescriptor? Find(string typeId) =>
        _descriptors.FirstOrDefault(d => string.Equals(d.TypeId, typeId, StringComparison.OrdinalIgnoreCase));

    public AudioNode? Create(string typeId) => Find(typeId)?.Create();
}
