using System.Globalization;
using VoiceChanger.Core.Audio;
using VoiceChanger.Core.Effects;

namespace VoiceChanger.Core.Graph;

/// <summary>Audio from a microphone or another recording device.</summary>
public sealed class InputDeviceNode : DeviceNode
{
    public const string Type = "input";

    private SmoothedGain _gain;

    public InputDeviceNode()
        : base(Type, "Audio Input", NodeCategory.Source)
    {
        Gain = AddParameter(EffectParameter.Continuous(
            "gain", "Gain", -24, 24, 0, "dB", decimals: 1,
            formatter: FormatDb,
            description: "Makes the device louder or quieter before anything else."));
        SetPorts([], [CreatePort("out", "Audio", PortDirection.Output, 0)]);
    }

    public override AudioDeviceKind DeviceKind => AudioDeviceKind.Input;

    public EffectParameter Gain { get; }

    internal static string FormatDb(float value) => value.ToString("+0.0;-0.0;0.0", CultureInfo.CurrentCulture) + " dB";

    protected override void OnPrepare() => _gain.Reset(SmoothedGain.DbToGain(Gain.Value));

    protected override void ProcessCore(NodeBlock block)
    {
        var output = block.Output(0);
        if (block.Device.IsEmpty)
        {
            output.Clear();
        }
        else
        {
            _gain.Apply(block.Device[..block.Samples], output, SmoothedGain.DbToGain(Gain.Value), block.Channels, accumulate: false);
        }

        Meter.Update(output);
    }
}

/// <summary>Plays its input on speakers, headphones or a virtual cable.</summary>
public sealed class OutputDeviceNode : DeviceNode
{
    public const string Type = "output";

    private SmoothedGain _volume;
    private float[] _scratch = [];

    public OutputDeviceNode()
        : base(Type, "Audio Output", NodeCategory.Output)
    {
        Volume = AddParameter(EffectParameter.Continuous(
            "volume", "Volume", -40, 12, 0, "dB", decimals: 1,
            formatter: InputDeviceNode.FormatDb,
            description: "Level sent to the device."));
        SetPorts([CreatePort("in", "Audio", PortDirection.Input, 0)], []);
    }

    public override AudioDeviceKind DeviceKind => AudioDeviceKind.Output;

    public EffectParameter Volume { get; }

    protected override void OnPrepare()
    {
        _volume.Reset(SmoothedGain.DbToGain(Volume.Value));
        _scratch = new float[GraphPlan.BlockFrames * Channels];
    }

    protected override void ProcessCore(NodeBlock block)
    {
        if (!block.IsConnected(0))
        {
            return;
        }

        // Several output nodes can share a device, so add to its mix instead of overwriting it.
        var scaled = _scratch.AsSpan(0, block.Samples);
        _volume.Apply(block.Input(0), scaled, SmoothedGain.DbToGain(Volume.Value), block.Channels, accumulate: false);
        Meter.Update(scaled);
        if (!block.Device.IsEmpty)
        {
            var device = block.Device[..block.Samples];
            for (int i = 0; i < scaled.Length; i++)
            {
                device[i] += scaled[i];
            }
        }
    }
}
