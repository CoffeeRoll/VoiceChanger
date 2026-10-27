using System.Globalization;
using VoiceChanger.Core.Effects;

namespace VoiceChanger.Core.Graph;

public enum ToneWaveform
{
    Sine,
    Triangle,
    Sawtooth,
    Square,
    Noise,
}

/// <summary>A steady tone or noise: handy for trying out a patch, or as a drone under a voice.</summary>
public sealed class ToneNode : AudioNode
{
    public const string Type = "tone";

    private SmoothedGain _level;
    private float[] _mono = [];
    private double _phase;
    private uint _noiseState = 0x9E3779B9;

    public ToneNode()
        : base(Type, "Tone Generator", NodeCategory.Source)
    {
        Waveform = AddParameter(EffectParameter.Choice(
            "waveform", "Waveform", ["Sine", "Triangle", "Sawtooth", "Square", "White noise"],
            description: "Shape of the sound: a sine is pure, sawtooth and square are buzzy, noise has no pitch."));
        Frequency = AddParameter(EffectParameter.Continuous(
            "frequency", "Frequency", 20, 4000, 220, "Hz", decimals: 1, ParameterScale.Logarithmic,
            formatter: v => v.ToString(v < 100 ? "0.0" : "0", CultureInfo.CurrentCulture) + " Hz",
            description: "Pitch of the tone. 440 Hz is the A above middle C.")
            .AvailableWhen(() => (ToneWaveform)Waveform.IntValue != ToneWaveform.Noise));
        Level = AddParameter(EffectParameter.Continuous(
            "level", "Level", -60, 0, -18, "dB", decimals: 1,
            formatter: InputDeviceNode.FormatDb,
            description: "How loud the tone is."));
        SetPorts([], [CreatePort("out", "Audio", PortDirection.Output, 0)]);
    }

    public EffectParameter Waveform { get; }

    public EffectParameter Frequency { get; }

    public EffectParameter Level { get; }

    protected override void OnPrepare()
    {
        _mono = new float[GraphPlan.BlockFrames];
        _phase = 0;
        _level.Reset(SmoothedGain.DbToGain(Level.Value));
    }

    protected override void ProcessCore(NodeBlock block)
    {
        int frames = block.Frames;
        var mono = _mono.AsSpan(0, frames);
        var waveform = (ToneWaveform)Waveform.IntValue;
        double increment = Frequency.Value / SampleRate;

        for (int i = 0; i < frames; i++)
        {
            double t = _phase;
            mono[i] = waveform switch
            {
                ToneWaveform.Sine => (float)Math.Sin(2 * Math.PI * t),
                ToneWaveform.Triangle => (float)(1 - 4 * Math.Abs(t - 0.5)),
                ToneWaveform.Sawtooth => (float)(2 * t - 1 - PolyBlep(t, increment)),
                ToneWaveform.Square => (float)((t < 0.5 ? 1 : -1) + PolyBlep(t, increment) - PolyBlep((t + 0.5) % 1, increment)),
                _ => NextNoise(),
            };

            _phase += increment;
            if (_phase >= 1)
            {
                _phase -= 1;
            }
        }

        // The same signal on every channel.
        var output = block.Output(0);
        int channels = block.Channels;
        for (int i = 0, s = 0; i < frames; i++)
        {
            for (int ch = 0; ch < channels; ch++)
            {
                output[s++] = mono[i];
            }
        }

        _level.Apply(output, output, SmoothedGain.DbToGain(Level.Value), channels, accumulate: false);
        Meter.Update(output);
    }

    /// <summary>Polynomial band-limited step: rounds off the jumps of sawtooth and square waves so they do not alias.</summary>
    private static double PolyBlep(double t, double dt)
    {
        if (t < dt)
        {
            t /= dt;
            return t + t - t * t - 1;
        }

        if (t > 1 - dt)
        {
            t = (t - 1) / dt;
            return t * t + t + t + 1;
        }

        return 0;
    }

    private float NextNoise()
    {
        // xorshift32: fast, allocation-free and good enough for audio noise.
        uint x = _noiseState;
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        _noiseState = x;
        return (int)x / (float)int.MaxValue * 0.5f;
    }
}
