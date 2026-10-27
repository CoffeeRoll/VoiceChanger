using System.Globalization;
using VoiceChanger.Core.Audio;
using VoiceChanger.Core.Effects;

namespace VoiceChanger.Core.Graph;

/// <summary>Combines several signals into one, each with its own volume and mute switch.</summary>
public sealed class MixerNode : AudioNode
{
    public const string Type = "mixer";
    public const int MinInputs = 2;
    public const int MaxInputs = 8;

    private readonly NodePort[] _allInputs = new NodePort[MaxInputs];
    private readonly NodePort[] _outputs;
    private readonly SmoothedGain[] _gains = new SmoothedGain[MaxInputs];
    private SmoothedGain _master;

    public MixerNode()
        : base(Type, "Mixer", NodeCategory.Mixing)
    {
        InputCount = AddParameter(EffectParameter.Integer(
            "inputs", "Inputs", MinInputs, MaxInputs, MinInputs,
            description: "How many signals the mixer combines."));
        Master = AddParameter(EffectParameter.Continuous(
            "master", "Master", -40, 12, 0, "dB", decimals: 1,
            formatter: InputDeviceNode.FormatDb,
            description: "Level of the combined signal."));

        var gains = new EffectParameter[MaxInputs];
        var mutes = new EffectParameter[MaxInputs];
        for (int i = 0; i < MaxInputs; i++)
        {
            int number = i + 1;
            int index = i;
            gains[i] = AddParameter(EffectParameter.Continuous(
                $"gain{number}", $"Input {number}", -60, 12, 0, "dB", decimals: 1,
                formatter: FormatGain,
                description: $"Volume of input {number} in the mix.")
                .AvailableWhen(() => index < InputCount.IntValue));
            mutes[i] = AddParameter(EffectParameter.Toggle(
                $"mute{number}", $"Mute input {number}", false,
                description: $"Silences input {number}.")
                .AvailableWhen(() => index < InputCount.IntValue));
            _allInputs[i] = CreatePort($"in{number}", $"Input {number}", PortDirection.Input, i);
            InputMeters[i] = new PeakMeter();
        }

        Gains = gains;
        Mutes = mutes;
        _outputs = [CreatePort("out", "Mix", PortDirection.Output, 0)];
        UpdatePorts();
        InputCount.ValueChanged += (_, _) => UpdatePorts();
    }

    /// <summary>Number of inputs (the ports beyond it disappear, together with their connections).</summary>
    public EffectParameter InputCount { get; }

    public EffectParameter Master { get; }

    /// <summary>Volume of each input in dB; the lowest value silences it.</summary>
    public IReadOnlyList<EffectParameter> Gains { get; }

    public IReadOnlyList<EffectParameter> Mutes { get; }

    /// <summary>Level arriving at each input, before its volume is applied.</summary>
    public PeakMeter[] InputMeters { get; } = new PeakMeter[MaxInputs];

    internal static string FormatGain(float value) =>
        value <= -60 ? "Off" : value.ToString("+0.0;-0.0;0.0", CultureInfo.CurrentCulture) + " dB";

    protected override void OnPrepare()
    {
        for (int i = 0; i < MaxInputs; i++)
        {
            _gains[i].Reset(TargetGain(i));
        }

        _master.Reset(SmoothedGain.DbToGain(Master.Value));
    }

    protected override void ProcessCore(NodeBlock block)
    {
        var output = block.Output(0);
        output.Clear();
        for (int i = 0; i < block.InputCount; i++)
        {
            float target = TargetGain(i);
            if (!block.IsConnected(i))
            {
                // Fades in once something gets plugged in.
                _gains[i].Reset(0);
                continue;
            }

            var input = block.Input(i);
            InputMeters[i].Update(input);
            if (target == 0 && _gains[i].Current == 0)
            {
                continue;
            }

            _gains[i].Apply(input, output, target, block.Channels, accumulate: true);
        }

        _master.Apply(output, output, SmoothedGain.DbToGain(Master.Value), block.Channels, accumulate: false);
        Meter.Update(output);
    }

    private float TargetGain(int input) =>
        Mutes[input].BoolValue || Gains[input].Value <= -60 ? 0 : SmoothedGain.DbToGain(Gains[input].Value);

    private void UpdatePorts() => SetPorts(_allInputs[..InputCount.IntValue], _outputs);
}
