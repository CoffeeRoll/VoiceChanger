using System.Globalization;
using VoiceChanger.Core.Dsp;

namespace VoiceChanger.Core.Effects;

/// <summary>
/// Ten band graphic equalizer (octave spaced peaking filters) with low cut / high cut filters and
/// an output gain stage.
/// </summary>
public sealed class EqualizerEffect : AudioEffect
{
    public const string Type = "equalizer";

    private const int LowCutStage = 10;
    private const int HighCutStage = 11;
    private const int StageCount = 12;
    private const float CutQ = 0.7071f;

    private static readonly float[] Frequencies = [31.5f, 63, 125, 250, 500, 1000, 2000, 4000, 8000, 16000];

    private readonly EffectParameter[] _bands;
    private readonly BiquadCoefficients[] _stages = new BiquadCoefficients[StageCount];
    private BiquadFilter[] _filters = [];
    private float _outputGain = 1;

    public EqualizerEffect()
        : base(Type, "Equalizer")
    {
        _bands = new EffectParameter[Frequencies.Length];
        for (int i = 0; i < Frequencies.Length; i++)
        {
            _bands[i] = AddParameter(EffectParameter.Continuous(
                $"band{i}",
                FormatFrequency(MathF.Floor(Frequencies[i])),
                -12,
                12,
                0,
                "dB",
                decimals: 1,
                formatter: FormatGain));
        }

        Q = AddParameter(EffectParameter.Continuous(
            "q", "Bandwidth (Q)", 0.3f, 4f, 1.4f, decimals: 2, scale: ParameterScale.Logarithmic,
            description: "Width of every band. Lower values affect a wider range of frequencies."));
        LowCut = AddParameter(EffectParameter.Continuous(
            "lowCut", "Low cut", 20, 1000, 20, "Hz", decimals: 0, scale: ParameterScale.Logarithmic,
            formatter: v => v <= 20 ? "Off" : FormatFrequency(v),
            description: "Removes rumble and everything below this frequency."));
        HighCut = AddParameter(EffectParameter.Continuous(
            "highCut", "High cut", 1000, 20000, 20000, "Hz", decimals: 0, scale: ParameterScale.Logarithmic,
            formatter: v => v >= 20000 ? "Off" : FormatFrequency(v),
            description: "Removes hiss and everything above this frequency."));
        OutputGain = AddParameter(EffectParameter.Continuous(
            "output", "Output gain", -24, 12, 0, "dB", decimals: 1, formatter: FormatGain));

        AddPreset("Flat");
        AddPreset("Bass boost", ("band0", 6), ("band1", 6), ("band2", 4), ("band3", 2), ("output", -3));
        AddPreset("Treble boost", ("band6", 2), ("band7", 4), ("band8", 6), ("band9", 6), ("output", -3));
        AddPreset("Voice clarity", ("lowCut", 80), ("band2", -2), ("band3", -1), ("band6", 3), ("band7", 4), ("band8", 2));
        AddPreset("Warm", ("band1", 3), ("band2", 4), ("band3", 2), ("band7", -2), ("band8", -3), ("band9", -4));
        AddPreset("Telephone", ("lowCut", 300), ("highCut", 3400), ("band3", -2), ("band5", 4), ("band6", 5), ("q", 1));
        AddPreset("AM radio", ("lowCut", 200), ("highCut", 5000), ("band5", 3), ("band6", 3), ("band7", 2));
        AddPreset("Megaphone", ("lowCut", 500), ("highCut", 4000), ("band5", 6), ("band6", 8), ("band7", 3), ("output", -6));
        AddPreset("Muffled", ("highCut", 800), ("band1", 4), ("band2", 3), ("output", 3));
    }

    /// <summary>Centre frequencies of the graphic bands, in Hz.</summary>
    public static IReadOnlyList<float> BandFrequencies => Frequencies;

    public IReadOnlyList<EffectParameter> Bands => _bands;

    public EffectParameter Q { get; }

    public EffectParameter LowCut { get; }

    public EffectParameter HighCut { get; }

    public EffectParameter OutputGain { get; }

    /// <summary>
    /// Designs the filter cascade for the current parameter values. Stages that would have no
    /// effect are returned as <see cref="BiquadCoefficients.Identity"/>.
    /// </summary>
    public BiquadCoefficients[] DesignStages(double sampleRate)
    {
        var stages = new BiquadCoefficients[StageCount];
        double nyquistGuard = sampleRate * 0.45;
        double q = Q.Value;

        for (int i = 0; i < _bands.Length; i++)
        {
            float gain = _bands[i].Value;
            stages[i] = gain == 0 || Frequencies[i] >= nyquistGuard
                ? BiquadCoefficients.Identity
                : BiquadCoefficients.Design(BiquadType.Peaking, sampleRate, Frequencies[i], q, gain);
        }

        float lowCut = LowCut.Value;
        stages[LowCutStage] = lowCut <= LowCut.Minimum
            ? BiquadCoefficients.Identity
            : BiquadCoefficients.Design(BiquadType.HighPass, sampleRate, lowCut, CutQ);

        float highCut = HighCut.Value;
        stages[HighCutStage] = highCut >= HighCut.Maximum || highCut >= nyquistGuard
            ? BiquadCoefficients.Identity
            : BiquadCoefficients.Design(BiquadType.LowPass, sampleRate, highCut, CutQ);

        return stages;
    }

    /// <summary>Computes the magnitude response (in dB) of the current settings at the given frequencies.</summary>
    public void GetResponse(ReadOnlySpan<double> frequencies, Span<double> decibels, double sampleRate)
    {
        var stages = DesignStages(sampleRate);
        double output = OutputGain.Value;
        for (int i = 0; i < frequencies.Length; i++)
        {
            double magnitude = 1;
            foreach (var stage in stages)
            {
                if (!stage.IsIdentity)
                {
                    magnitude *= stage.MagnitudeAt(frequencies[i], sampleRate);
                }
            }

            decibels[i] = 20 * Math.Log10(Math.Max(magnitude, 1e-6)) + output;
        }
    }

    protected override void OnPrepare()
    {
        _filters = new BiquadFilter[StageCount];
        for (int i = 0; i < StageCount; i++)
        {
            _filters[i] = new BiquadFilter(Channels);
            _stages[i] = BiquadCoefficients.Identity;
        }
    }

    protected override void OnParametersChanged()
    {
        var designed = DesignStages(SampleRate);
        for (int i = 0; i < StageCount; i++)
        {
            // A bypassed stage is skipped, so clear its memory to avoid a stale transient later.
            if (designed[i].IsIdentity && !_stages[i].IsIdentity)
            {
                _filters[i].Reset();
            }

            _stages[i] = designed[i];
            _filters[i].Coefficients = designed[i];
        }

        _outputGain = MathF.Pow(10, OutputGain.Value / 20);
    }

    protected override void OnReset()
    {
        foreach (var filter in _filters)
        {
            filter.Reset();
        }
    }

    protected override void ProcessCore(Span<float> interleaved)
    {
        for (int i = 0; i < StageCount; i++)
        {
            if (!_stages[i].IsIdentity)
            {
                _filters[i].Process(interleaved);
            }
        }

        float gain = _outputGain;
        if (gain != 1f)
        {
            for (int i = 0; i < interleaved.Length; i++)
            {
                interleaved[i] *= gain;
            }
        }
    }

    internal static string FormatFrequency(float hz) =>
        hz >= 1000
            ? (hz / 1000f).ToString(hz % 1000 == 0 ? "0" : "0.0", CultureInfo.CurrentCulture) + " kHz"
            : hz.ToString("0", CultureInfo.CurrentCulture) + " Hz";

    internal static string FormatGain(float db) =>
        db.ToString("+0.0;-0.0;0.0", CultureInfo.CurrentCulture) + " dB";
}
