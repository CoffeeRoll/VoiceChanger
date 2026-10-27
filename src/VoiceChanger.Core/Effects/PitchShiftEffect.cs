using System.Globalization;
using VoiceChanger.Core.Dsp;

namespace VoiceChanger.Core.Effects;

/// <summary>
/// Changes the pitch of the signal without changing its speed. Two algorithms are available:
/// a granular (time-domain) shifter with very low latency, and a phase vocoder that sounds smoother
/// for large shifts but adds more delay. Both move the formants together with the pitch, like
/// playing a recording faster or slower.
/// <para>
/// Optionally the formants (the resonances that make a voice recognisable) stay where they are, so a
/// voice an octave up still sounds like the same person instead of a chipmunk, and an octave down
/// like a deeper person rather than a slowed-down recording; they can also be moved on their own to
/// make a voice sound bigger or smaller. This uses a pitch-synchronous voice algorithm
/// (<see cref="PsolaPitchShifter"/>), which keeps the voice's own high harmonics and leaves breath
/// noise where it is.
/// </para>
/// </summary>
public sealed class PitchShiftEffect : AudioEffect
{
    public const string Type = "pitch-shift";

    public const int GranularMode = 0;
    public const int PhaseVocoderMode = 1;

    /// <summary>The voice algorithm used while formants are preserved (not a choice of <see cref="Mode"/>).</summary>
    public const int FormantMode = 2;

    private const float MaxWindowMilliseconds = 100;
    private const float BypassRampSeconds = 0.02f;

    private GranularPitchShifter? _granular;
    private PsolaPitchShifter? _psola;
    private PhaseVocoder[] _vocoders = [];
    private float[] _dry = [];
    private int _mode;
    private float _mix = 1;
    private bool _shifting;
    private float _shiftAmount;
    private float _rampStep;

    public PitchShiftEffect()
        : base(Type, "Pitch Shifter")
    {
        Semitones = AddParameter(EffectParameter.Integer(
            "semitones", "Pitch", -24, 24, 0, "st",
            formatter: v => MusicalScale.FormatInterval((int)MathF.Round(v)),
            description: "Shift in semitones, up to two octaves either way. 12 semitones make one octave."));
        Cents = AddParameter(EffectParameter.Integer(
            "cents", "Fine tune", -100, 100, 0, "cents",
            formatter: v => v.ToString("+0;-0;0", CultureInfo.CurrentCulture) + " cents",
            description: "Additional shift in hundredths of a semitone."));
        PreserveFormants = AddParameter(EffectParameter.Toggle(
            "formants", "Preserve formants", false,
            description: "Keeps the character of your voice when shifting, so it sounds like you singing higher or lower instead of a chipmunk or a slowed-down recording. Uses an algorithm made for voices."));
        FormantShift = AddParameter(EffectParameter.Continuous(
            "formantShift", "Formant shift", -12, 12, 0, "st", decimals: 1,
            formatter: v => v.ToString("+0.0;-0.0;0", CultureInfo.CurrentCulture) + " semitones",
            description: "Moves the voice's resonances on their own: negative sounds bigger and darker, positive smaller and brighter. Works while Preserve formants is on.")
            .AvailableWhen(() => PreserveFormants.BoolValue));
        Mode = AddParameter(EffectParameter.Choice(
            "mode", "Algorithm", ["Low latency (granular)", "High quality (phase vocoder)"],
            description: "Granular reacts fastest. The phase vocoder is smoother for big shifts but adds delay. Preserving formants uses its own voice algorithm.")
            .AvailableWhen(() => !PreserveFormants.BoolValue));
        WindowMilliseconds = AddParameter(EffectParameter.Continuous(
            "window", "Window size", 10, MaxWindowMilliseconds, 30, "ms", decimals: 0,
            description: "Analysis window. Longer windows sound smoother, shorter windows react faster. The voice algorithm used to preserve formants follows the pitch of the voice instead.")
            .AvailableWhen(() => !PreserveFormants.BoolValue));
        Mix = AddParameter(EffectParameter.Continuous(
            "mix", "Mix", 0, 100, 100, "%", decimals: 0,
            description: "Blend between the original (0 %) and the shifted voice (100 %)."));

        AddPreset("Natural");
        AddPreset("Higher voice", ("semitones", 4));
        AddPreset("Lower voice", ("semitones", -4), ("formants", 1), ("formantShift", -1));
        AddPreset("Chipmunk", ("semitones", 12), ("window", 25));
        AddPreset("Deep", ("semitones", -7), ("formants", 1), ("formantShift", -3));
        AddPreset("Monster", ("semitones", -12), ("window", 60), ("mode", PhaseVocoderMode));
        AddPreset("Harmony (fifth)", ("semitones", 7), ("mix", 50));
        AddPreset("Detune", ("cents", 15), ("mix", 50), ("window", 30));
        AddPreset("Natural octave up", ("semitones", 12), ("formants", 1));
        AddPreset("Natural octave down", ("semitones", -12), ("formants", 1));
        AddPreset("Bigger voice", ("semitones", -3), ("formants", 1), ("formantShift", -3));
        AddPreset("Smaller voice", ("semitones", 3), ("formants", 1), ("formantShift", 3));
    }

    public EffectParameter Semitones { get; }

    public EffectParameter Cents { get; }

    public EffectParameter PreserveFormants { get; }

    public EffectParameter FormantShift { get; }

    public EffectParameter Mode { get; }

    public EffectParameter WindowMilliseconds { get; }

    public EffectParameter Mix { get; }

    /// <summary>Pitch ratio for the current settings (2 = one octave up).</summary>
    public double Ratio => Math.Pow(2, (Semitones.Value + Cents.Value / 100.0) / 12.0);

    /// <summary>Formant ratio for the current settings (1 = formants stay where they are).</summary>
    public double FormantRatio => Math.Pow(2, FormantShift.Value / 12.0);

    /// <summary>The algorithm actually used: <see cref="FormantMode"/> while formants are preserved, otherwise the chosen <see cref="Mode"/>.</summary>
    public int EffectiveMode => PreserveFormants.BoolValue ? FormantMode : Mode.IntValue;

    /// <summary>Whether the current settings change the sound at all.</summary>
    public bool IsShifting => Ratio != 1 || (PreserveFormants.BoolValue && FormantRatio != 1);

    public override int LatencyFrames
    {
        get
        {
            if (!IsPrepared || !IsShifting)
            {
                return 0;
            }

            return EffectiveMode switch
            {
                FormantMode => _psola?.LatencyFrames ?? 0,
                PhaseVocoderMode => PhaseVocoder.LatencyFor(VocoderFrameSize()),
                _ => GranularPitchShifter.LatencyFor(WindowFrames(), SampleRate),
            };
        }
    }

    protected override void OnPrepare()
    {
        _granular = new GranularPitchShifter(Channels, (int)(MaxWindowMilliseconds / 1000 * SampleRate), SampleRate);
        _psola = new PsolaPitchShifter(Channels, SampleRate);
        _vocoders = [];
        _dry = new float[EffectChain.MaxBlockFrames * Channels];
        _rampStep = 1f / (BypassRampSeconds * SampleRate);
    }

    protected override void OnParametersChanged()
    {
        double ratio = Ratio;
        int mode = EffectiveMode;
        bool formants = PreserveFormants.BoolValue;
        double formantRatio = FormantRatio;

        _granular!.Ratio = ratio;
        _granular.WindowFrames = WindowFrames();

        int frameSize = VocoderFrameSize();
        if (_vocoders.Length != Channels || _vocoders[0].FrameSize != frameSize)
        {
            _vocoders = new PhaseVocoder[Channels];
            for (int ch = 0; ch < Channels; ch++)
            {
                _vocoders[ch] = new PhaseVocoder(frameSize, SampleRate);
            }
        }

        foreach (var vocoder in _vocoders)
        {
            vocoder.Ratio = ratio;
        }

        _psola!.Ratio = ratio;
        if (formants)
        {
            _psola.FormantRatio = formantRatio;
        }

        if (mode != _mode)
        {
            // Start the newly selected algorithm from silence instead of stale history.
            _granular.Reset();
            _psola.Reset();
            foreach (var vocoder in _vocoders)
            {
                vocoder.Reset();
            }

            _mode = mode;
        }

        _shifting = ratio != 1 || (formants && formantRatio != 1);
        _mix = Mix.Value / 100f;
    }

    protected override void OnReset()
    {
        _granular?.Reset();
        _psola?.Reset();
        foreach (var vocoder in _vocoders)
        {
            vocoder.Reset();
        }

        _shiftAmount = _shifting ? 1 : 0;
    }

    protected override void ProcessCore(Span<float> interleaved)
    {
        if (_dry.Length < interleaved.Length)
        {
            _dry = new float[interleaved.Length];
        }

        var dry = _dry.AsSpan(0, interleaved.Length);
        interleaved.CopyTo(dry);

        switch (_mode)
        {
            case FormantMode:
                _psola!.Process(interleaved);
                break;
            case PhaseVocoderMode:
                for (int ch = 0; ch < _vocoders.Length; ch++)
                {
                    _vocoders[ch].Process(interleaved, ch, Channels);
                }

                break;
            default:
                _granular!.Process(interleaved);
                break;
        }

        // Blend: at 0 semitones the shifter fades out completely (no comb filtering, no delay),
        // and the mix control blends the shifted voice with the original.
        int channels = Channels;
        float target = _shifting ? 1 : 0;
        for (int i = 0; i < interleaved.Length; i += channels)
        {
            if (_shiftAmount != target)
            {
                _shiftAmount = target > _shiftAmount
                    ? Math.Min(target, _shiftAmount + _rampStep)
                    : Math.Max(target, _shiftAmount - _rampStep);
            }

            float wet = _shiftAmount * _mix;
            for (int ch = 0; ch < channels; ch++)
            {
                float d = dry[i + ch];
                interleaved[i + ch] = d + (interleaved[i + ch] - d) * wet;
            }
        }
    }

    private int WindowFrames() => (int)(WindowMilliseconds.Value / 1000 * SampleRate);

    private int VocoderFrameSize()
    {
        int frames = Math.Clamp(WindowFrames(), 256, 8192);
        return (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)frames);
    }
}
