using VoiceChanger.Core.Audio;
using VoiceChanger.Core.Effects;

namespace VoiceChanger.Core.Graph;

public enum PortDirection
{
    Input,
    Output,
}

/// <summary>What a node does, used to group and colour nodes in the editor.</summary>
public enum NodeCategory
{
    /// <summary>Produces audio: a recording device or a generator.</summary>
    Source,

    /// <summary>Changes audio: one input, one output.</summary>
    Effect,

    /// <summary>Combines, lines up or splits signals.</summary>
    Mixing,

    /// <summary>Plays audio on a device.</summary>
    Output,
}

/// <summary>A socket on a node that a connection can be attached to.</summary>
public sealed class NodePort
{
    internal NodePort(AudioNode node, string id, string name, PortDirection direction, int index)
    {
        Node = node;
        Id = id;
        Name = name;
        Direction = direction;
        Index = index;
    }

    public AudioNode Node { get; }

    /// <summary>Stable identifier used to save connections, e.g. "in2".</summary>
    public string Id { get; }

    public string Name { get; }

    public PortDirection Direction { get; }

    /// <summary>Position among the node's inputs or outputs.</summary>
    public int Index { get; }

    public override string ToString() => $"{Node.Title} › {Name}";
}

/// <summary>A wire from an output port to an input port.</summary>
public sealed record NodeConnection(NodePort From, NodePort To)
{
    public override string ToString() => $"{From} → {To}";
}

/// <summary>
/// The buffers a node works on during one block: one per connected input, one per output, all
/// holding <see cref="Frames"/> frames of interleaved audio with <see cref="Channels"/> channels.
/// </summary>
public readonly ref struct NodeBlock
{
    private readonly float[]?[] _inputs;
    private readonly float[][] _outputs;
    private readonly float[] _silence;
    private readonly int[] _inputLatencies;

    internal NodeBlock(float[]?[] inputs, float[][] outputs, float[] silence, Span<float> device, int frames, int channels, int[]? inputLatencies = null)
    {
        _inputs = inputs;
        _outputs = outputs;
        _silence = silence;
        _inputLatencies = inputLatencies ?? new int[inputs.Length];
        Device = device;
        Frames = frames;
        Channels = channels;
    }

    public int Frames { get; }

    public int Channels { get; }

    public int Samples => Frames * Channels;

    public int InputCount => _inputs.Length;

    public int OutputCount => _outputs.Length;

    /// <summary>
    /// Audio exchanged with the device a device node stands for: what was captured (input nodes), or
    /// the device's mix that the node adds to (output nodes). Empty when no device is open.
    /// </summary>
    public Span<float> Device { get; }

    public bool IsConnected(int input) => _inputs[input] is not null;

    /// <summary>
    /// How late the signal at an input is: the delay of the slowest path from a source to it, in
    /// frames (0 when nothing is connected).
    /// </summary>
    public int InputLatencyFrames(int input) => _inputLatencies[input];

    /// <summary>The signal arriving at an input (silence when nothing is connected). Never write to it: it is shared.</summary>
    public ReadOnlySpan<float> Input(int index) => (_inputs[index] ?? _silence).AsSpan(0, Samples);

    public Span<float> Output(int index) => _outputs[index].AsSpan(0, Samples);
}

/// <summary>
/// A processing step in an <see cref="AudioGraph"/>.
/// <para>
/// Threading model: the graph and the nodes' structure are edited on one (UI) thread.
/// <see cref="Process"/> runs on the audio thread. Parameters may change at any time from any thread,
/// like effect parameters.
/// </para>
/// </summary>
public abstract class AudioNode
{
    private readonly List<EffectParameter> _parameters = [];
    private NodePort[] _inputs = [];
    private NodePort[] _outputs = [];
    private string? _label;

    protected AudioNode(string typeId, string name, NodeCategory category)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        TypeId = typeId;
        Name = name;
        Category = category;
    }

    /// <summary>Identifies the node in saved graphs.</summary>
    public Guid Id { get; internal set; } = Guid.NewGuid();

    /// <summary>Stable identifier of the node type used to save and restore it.</summary>
    public string TypeId { get; }

    /// <summary>Name of the node type, e.g. "Mixer".</summary>
    public string Name { get; }

    /// <summary>Optional name the user gave this node, e.g. "Guitar".</summary>
    public string? Label
    {
        get => _label;
        set
        {
            value = string.IsNullOrWhiteSpace(value) ? null : value;
            if (_label == value)
            {
                return;
            }

            _label = value;
            LabelChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>What the editor shows in the node's header.</summary>
    public string Title => Label ?? Name;

    public NodeCategory Category { get; }

    /// <summary>Position of the node's top-left corner in the editor.</summary>
    public double X { get; set; }

    public double Y { get; set; }

    public IReadOnlyList<NodePort> Inputs => _inputs;

    public IReadOnlyList<NodePort> Outputs => _outputs;

    public virtual IReadOnlyList<EffectParameter> Parameters => _parameters;

    /// <summary>Level of the signal the node produces (or plays, for outputs).</summary>
    public PeakMeter Meter { get; } = new();

    /// <summary>Delay the node adds to the signal, in frames.</summary>
    public virtual int LatencyFrames => 0;

    public int SampleRate { get; private set; }

    public int Channels { get; private set; }

    public bool IsPrepared => SampleRate > 0;

    /// <summary>Raised when the node's ports or device change, which changes how the graph is wired up.</summary>
    public event EventHandler? StructureChanged;

    public event EventHandler? LabelChanged;

    public EffectParameter? FindParameter(string id) =>
        Parameters.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    public NodePort? FindInput(string id) => _inputs.FirstOrDefault(p => p.Id == id);

    public NodePort? FindOutput(string id) => _outputs.FirstOrDefault(p => p.Id == id);

    /// <summary>Configures the node for a stream format and clears its state. Never called while it is being processed.</summary>
    public void Prepare(int sampleRate, int channels)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        SampleRate = sampleRate;
        Channels = channels;
        OnPrepare();
    }

    /// <summary>Processes one block. Audio thread only.</summary>
    public void Process(NodeBlock block)
    {
        if (IsPrepared)
        {
            ProcessCore(block);
        }
    }

    protected EffectParameter AddParameter(EffectParameter parameter)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        if (_parameters.Any(p => p.Id == parameter.Id))
        {
            throw new InvalidOperationException($"Duplicate parameter id '{parameter.Id}'.");
        }

        _parameters.Add(parameter);
        return parameter;
    }

    protected NodePort CreatePort(string id, string name, PortDirection direction, int index) =>
        new(this, id, name, direction, index);

    /// <summary>Replaces the ports. Keep passing the same port objects so existing connections stay valid.</summary>
    protected void SetPorts(NodePort[] inputs, NodePort[] outputs)
    {
        bool changed = !_inputs.SequenceEqual(inputs) || !_outputs.SequenceEqual(outputs);
        _inputs = inputs;
        _outputs = outputs;
        if (changed)
        {
            OnStructureChanged();
        }
    }

    protected void OnStructureChanged() => StructureChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>Allocate buffers for <see cref="SampleRate"/> and <see cref="Channels"/> and clear all state.</summary>
    protected virtual void OnPrepare()
    {
    }

    protected abstract void ProcessCore(NodeBlock block);
}

/// <summary>A node that stands for an audio device.</summary>
public abstract class DeviceNode(string typeId, string name, NodeCategory category) : AudioNode(typeId, name, category)
{
    private string? _deviceId;

    public abstract AudioDeviceKind DeviceKind { get; }

    /// <summary>The device this node captures from or plays to; <c>null</c> when none is chosen.</summary>
    public string? DeviceId
    {
        get => _deviceId;
        set
        {
            value = string.IsNullOrEmpty(value) ? null : value;
            if (_deviceId == value)
            {
                return;
            }

            _deviceId = value;
            OnStructureChanged();
        }
    }
}
