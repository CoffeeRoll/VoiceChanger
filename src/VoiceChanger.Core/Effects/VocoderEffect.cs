using System.Globalization;
using VoiceChanger.Core.Dsp;

namespace VoiceChanger.Core.Effects;

/// <summary>The sound a <see cref="VocoderEffect"/> shapes with the voice when no carrier is plugged in.</summary>
public enum VocoderSynth
{
    /// <summary>A sawtooth that follows the pitch of the voice: a talking robot.</summary>
    YourPitch,

    /// <summary>A sawtooth on one note: a monotone robot.</summary>
    Note,

    /// <summary>A major chord: a choir of robots.</summary>
    Chord,

    /// <summary>Noise: a whisper.</summary>
    Whisper,
}

/// <summary>
/// Vocoder: the voice shapes another sound (<see cref="ChannelVocoder"/>). That sound is whatever
/// is plugged into the node's Carrier socket in the node editor (an instrument, music, a tone), or
/// otherwise a built-in synth: a sawtooth on the voice's own pitch (robot), on a fixed note, a
/// chord (choir), or noise (whisper).
/// </summary>
public sealed class VocoderEffect : AudioEffect, ISidechainEffect
{
    public const string Type = "vocoder";

    private const double GlideSeconds = 0.015;
    // A major chord with the octave, each note a hair off so they beat like real voices.
    private static readonly double[] ChordRatios = [1, Math.Pow(2, 4 / 12.0) * 1.0012, Math.Pow(2, 7 / 12.0) * 0.9991, 2 * 1.0006];

    private ChannelVocoder? _vocoder;
    private PitchDetector? _detector;
    private float[] _modulator = [];
    private float[] _carrier = [];
    private float[] _dry = [];
    private float[]? _sidechain;
    private readonly double[] _phases = new double[ChordRatios.Length];
    private double _frequency;
    private uint _noise = 0x2545F491;
    private bool _external;

    // Settings, refreshed in OnParametersChanged.
    private VocoderSynth _synth;
    private double _noteFrequency;
    private double _transposeRatio = 1;
    private float _mix = 1;
    private float _gain = 1;

    public VocoderEffect()
        : base(Type, "Vocoder")
    {
        Synth = AddParameter(EffectParameter.Choice(
            "synth", "Synth", ["Your pitch", "Note", "Chord", "Whisper"],
            description: "The sound your voice shapes when nothing is plugged into the Carrier socket (node editor): a synth on your own pitch (a talking robot), on one note, a chord (a choir), or noise (a whisper)."));
        Note = AddParameter(EffectParameter.Integer(
            "note", "Note", 28, 72, 45,
            formatter: v => MusicalScale.NoteName((int)MathF.Round(v)),
            description: "The note of the synth, or the lowest note of the chord.")
            .AvailableWhen(() => (VocoderSynth)Synth.IntValue is VocoderSynth.Note or VocoderSynth.Chord));
        Transpose = AddParameter(EffectParameter.Integer(
            "transpose", "Transpose", -24, 12, 0, "st",
            formatter: v => MusicalScale.FormatInterval((int)MathF.Round(v)),
            description: "Plays the synth this far above or below your pitch.")
            .AvailableWhen(() => (VocoderSynth)Synth.IntValue == VocoderSynth.YourPitch));
        Bands = AddParameter(EffectParameter.Integer(
            "bands", "Bands", ChannelVocoder.MinBands, ChannelVocoder.MaxBands, 20,
            description: "How finely your voice is measured: more bands make words clearer, fewer sound more robotic."));
        Response = AddParameter(EffectParameter.Continuous(
            "response", "Response", 5, 200, 30, "ms", decimals: 0, ParameterScale.Logarithmic,
            formatter: v => v.ToString("0", CultureInfo.CurrentCulture) + " ms",
            description: "How quickly the sound falls quiet after your voice does. Short is crisp, long is smooth."));
        FormantShift = AddParameter(EffectParameter.Continuous(
            "formant", "Formant shift", -12, 12, 0, "st", decimals: 1,
            formatter: v => v.ToString("+0.0;-0.0;0", CultureInfo.CurrentCulture) + " semitones",
            description: "Moves the resonances of your voice: negative sounds bigger, positive smaller."));
        Consonants = AddParameter(EffectParameter.Continuous(
            "consonants", "Consonants", 0, 100, 40, "%", decimals: 0,
            description: "Lets the hiss of s, t and f through, so words stay clear."));
        Mix = AddParameter(EffectParameter.Continuous(
            "mix", "Mix", 0, 100, 100, "%", decimals: 0,
            description: "Blend between your own voice (0 %) and the vocoder (100 %)."));
        Level = AddParameter(EffectParameter.Continuous(
            "level", "Output", -24, 12, 0, "dB", decimals: 1,
            formatter: v => v.ToString("+0.0;-0.0;0.0", CultureInfo.CurrentCulture) + " dB",
            description: "Volume of the vocoder."));

        AddPreset("Robot");
        AddPreset("Deep robot", ("transpose", -12), ("formant", -4));
        AddPreset("Monotone", ("synth", (int)VocoderSynth.Note), ("note", 45), ("bands", 16));
        AddPreset("Choir", ("synth", (int)VocoderSynth.Chord), ("note", 48), ("bands", 24), ("response", 60));
        AddPreset("Whisper", ("synth", (int)VocoderSynth.Whisper), ("consonants", 60));
        AddPreset("Classic (16 bands)", ("bands", 16), ("response", 20), ("consonants", 25));
    }

    public EffectParameter Synth { get; }

    public EffectParameter Note { get; }

    public EffectParameter Transpose { get; }

    public EffectParameter Bands { get; }

    public EffectParameter Response { get; }

    public EffectParameter FormantShift { get; }

    public EffectParameter Consonants { get; }

    public EffectParameter Mix { get; }

    public EffectParameter Level { get; }

    public string InputName => "Voice";

    public string SidechainName => "Carrier";

    /// <summary>Whether the last block used a plugged-in carrier rather than the synth.</summary>
    public bool UsesCarrierInput => Volatile.Read(ref _external);

    public void SetSidechain(float[]? samples) => _sidechain = samples;

    protected override void OnPrepare()
    {
        _vocoder = new ChannelVocoder(Channels, SampleRate);
        _detector = new PitchDetector(SampleRate, windowSeconds: 0.03, hopSeconds: 0.005);
        _modulator = new float[EffectChain.MaxBlockFrames];
        _carrier = new float[EffectChain.MaxBlockFrames];
        _dry = new float[EffectChain.MaxBlockFrames * Channels];
    }

    protected override void OnParametersChanged()
    {
        _synth = (VocoderSynth)Synth.IntValue;
        _noteFrequency = MusicalScale.NoteToFrequency(Note.IntValue);
        _transposeRatio = Math.Pow(2, Transpose.IntValue / 12.0);
        _mix = Mix.Value / 100f;
        _gain = MathF.Pow(10, Level.Value / 20);
        _vocoder!.Bands = Bands.IntValue;
        _vocoder.ReleaseSeconds = Response.Value / 1000.0;
        _vocoder.FormantSemitones = FormantShift.Value;
        _vocoder.Consonants = Consonants.Value / 100.0;
    }

    protected override void OnReset()
    {
        _vocoder?.Reset();
        _detector?.Reset();
        Array.Clear(_phases);
        _frequency = 0;
    }

    protected override void ProcessCore(Span<float> interleaved)
    {
        int channels = Channels;
        int frames = Math.Min(interleaved.Length / channels, _modulator.Length);
        var modulator = _modulator.AsSpan(0, frames);
        var dry = _dry.AsSpan(0, frames * channels);
        interleaved[..dry.Length].CopyTo(dry);
        float scale = 1f / channels;
        for (int f = 0; f < frames; f++)
        {
            float sum = 0;
            for (int ch = 0; ch < channels; ch++)
            {
                sum += dry[f * channels + ch];
            }

            modulator[f] = sum * scale;
        }

        var sidechain = _sidechain;
        _external = sidechain is not null;
        if (sidechain is not null)
        {
            _vocoder!.Process(modulator, sidechain.AsSpan(0, frames * channels), channels, interleaved);
        }
        else
        {
            Synthesise(modulator);
            _vocoder!.Process(modulator, _carrier.AsSpan(0, frames), 1, interleaved);
        }

        for (int i = 0; i < dry.Length; i++)
        {
            interleaved[i] = dry[i] + (interleaved[i] * _gain - dry[i]) * _mix;
        }
    }

    /// <summary>The built-in carrier for one block.</summary>
    private void Synthesise(ReadOnlySpan<float> voice)
    {
        var carrier = _carrier.AsSpan(0, voice.Length);
        if (_synth == VocoderSynth.Whisper)
        {
            for (int n = 0; n < carrier.Length; n++)
            {
                _noise ^= _noise << 13;
                _noise ^= _noise >> 17;
                _noise ^= _noise << 5;
                carrier[n] = (_noise / (float)uint.MaxValue - 0.5f) * 1.4f;
            }

            return;
        }

        double target = _noteFrequency;
        if (_synth == VocoderSynth.YourPitch)
        {
            // The voice's own pitch (the last one heard while it isn't voiced).
            _detector!.Process(voice);
            if (_detector.IsVoiced)
            {
                target = _detector.Frequency * _transposeRatio;
            }
            else if (_frequency > 0)
            {
                target = _frequency;
            }
        }

        double glide = 1 - Math.Exp(-1 / (GlideSeconds * SampleRate));
        int voices = _synth == VocoderSynth.Chord ? ChordRatios.Length : 1;
        for (int n = 0; n < carrier.Length; n++)
        {
            _frequency = _frequency <= 0 ? target : _frequency + glide * (target - _frequency);
            double value = 0;
            for (int v = 0; v < voices; v++)
            {
                double step = Math.Min(_frequency * ChordRatios[v] / SampleRate, 0.45);
                value += Saw(ref _phases[v], step);
            }

            carrier[n] = (float)(value * 0.5 / Math.Sqrt(voices));
        }
    }

    /// <summary>One sample of a band-limited sawtooth (PolyBLEP).</summary>
    private static double Saw(ref double phase, double step)
    {
        double value = 2 * phase - 1;
        if (phase < step)
        {
            double t = phase / step;
            value -= t + t - t * t - 1;
        }
        else if (phase > 1 - step)
        {
            double t = (phase - 1) / step;
            value -= t * t + t + t + 1;
        }

        phase += step;
        if (phase >= 1)
        {
            phase -= 1;
        }

        return value;
    }
}
