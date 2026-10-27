namespace VoiceChanger.Core.Dsp;

/// <summary>
/// Channel vocoder (H. Dudley, 1939): puts the spectral shape of one signal (the modulator, a voice)
/// on another (the carrier, a synth or any rich sound).
/// <para>
/// Both signals go through the same bank of steep band-pass filters from 100 Hz to 8 kHz, spaced
/// like the mel scale (with its corner at 250 Hz): evenly in pitch higher up, where the ear judges
/// by ratio, and closer to evenly in hertz at the bottom, where bands that narrow in pitch would lag
/// behind the voice and blur the first resonance. An envelope follower measures how loud the voice is in every band, and the carrier's
/// band is played at that loudness. Each carrier band is first evened out by its own envelope, so
/// the result follows the voice whatever the carrier's balance of low and high: a dull pad works as
/// well as a bright saw, as long as it has something in every band. The bands can be shifted
/// against each other (the formant shift), which makes the voice sound bigger or smaller, and a
/// little of the voice above 4.5 kHz can be let through so "s", "t" and "f" stay clear.
/// </para>
/// </summary>
public sealed class ChannelVocoder
{
    public const int MinBands = 8;
    public const int MaxBands = 32;

    private const double LowestHz = 100;
    private const double CornerHz = 250;
    private const double HighestHz = 8000;
    // Slow enough not to follow the single pulses of a voice, fast enough for consonants.
    private const double AttackSeconds = 0.005;
    private const double CarrierAttackSeconds = 0.001;
    private const double CarrierReleaseSeconds = 0.02;
    private const double ConsonantHz = 4500;

    // Steep bands that meet 3 dB down pass about half the power of the voice: made up here.
    private const double Makeup = 2;
    // Four band-pass stages per band (8th order): steep enough that loud bands don't spill into
    // quiet ones, so the voice's peaks and valleys come out as deep as they went in.
    private const int Stages = 4;

    private readonly int _channels;
    private readonly int _sampleRate;
    private readonly double[] _b0 = new double[MaxBands];
    private readonly double[] _a1 = new double[MaxBands];
    private readonly double[] _a2 = new double[MaxBands];
    private readonly double[] _centres = new double[MaxBands];
    private readonly double[] _formantSource = new double[MaxBands];
    private readonly double[] _modulatorState = new double[MaxBands * 2 * Stages];
    private readonly double[] _modulatorEnvelope = new double[MaxBands];
    private readonly double[] _gains = new double[MaxBands];
    private readonly double[][] _carrierState;
    private readonly double[][] _carrierEnvelope;
    private readonly double[] _carrierLoudest;
    private readonly BiquadCoefficients _consonant;
    private double _consonant1;
    private double _consonant2;
    private int _bands;
    private double _lowestMel;
    private double _melPerBand;
    private double _formantSemitones;
    private double _attack;
    private double _release;
    private double _carrierAttack;
    private double _carrierRelease;

    public ChannelVocoder(int channels, int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 8000);
        _channels = channels;
        _sampleRate = sampleRate;
        _carrierState = new double[channels][];
        _carrierEnvelope = new double[channels][];
        _carrierLoudest = new double[channels];
        for (int ch = 0; ch < channels; ch++)
        {
            _carrierState[ch] = new double[MaxBands * 2 * Stages];
            _carrierEnvelope[ch] = new double[MaxBands];
        }

        _consonant = BiquadCoefficients.Design(BiquadType.HighPass, sampleRate, Math.Min(ConsonantHz, 0.4 * sampleRate), 0.707);
        _attack = 1 - Math.Exp(-1 / (AttackSeconds * sampleRate));
        _carrierAttack = 1 - Math.Exp(-1 / (CarrierAttackSeconds * sampleRate));
        _carrierRelease = 1 - Math.Exp(-1 / (CarrierReleaseSeconds * sampleRate));
        Bands = 20;
        ReleaseSeconds = 0.03;
    }

    /// <summary>Number of bands (8 to 32): more understand better, fewer sound more robotic.</summary>
    public int Bands
    {
        get => _bands;
        set
        {
            value = Math.Clamp(value, MinBands, MaxBands);
            if (value != _bands)
            {
                _bands = value;
                Design();
            }
        }
    }

    /// <summary>How quickly a band falls quiet after the voice does, in seconds.</summary>
    public double ReleaseSeconds
    {
        get => -1 / (Math.Log(1 - _release) * _sampleRate);
        set => _release = 1 - Math.Exp(-1 / (Math.Clamp(value, 0.001, 2) * _sampleRate));
    }

    /// <summary>Moves the voice's resonances on the carrier, in semitones (positive = smaller, brighter).</summary>
    public double FormantSemitones
    {
        get => _formantSemitones;
        set
        {
            _formantSemitones = value;
            UpdateFormantShift();
        }
    }

    /// <summary>How much of the voice above 4.5 kHz passes straight through (0 to 1).</summary>
    public double Consonants { get; set; }

    /// <summary>Centre frequency of a band, in Hz.</summary>
    public double BandFrequency(int band) => _centres[band];

    public void Reset()
    {
        Array.Clear(_modulatorState);
        Array.Clear(_modulatorEnvelope);
        for (int ch = 0; ch < _channels; ch++)
        {
            Array.Clear(_carrierState[ch]);
            Array.Clear(_carrierEnvelope[ch]);
        }

        Array.Clear(_carrierLoudest);
        _consonant1 = _consonant2 = 0;
    }

    /// <summary>
    /// Vocodes one block. <paramref name="modulator"/> is mono; <paramref name="carrier"/> is
    /// interleaved with <paramref name="carrierChannels"/> channels (1, then every output channel
    /// gets the same, or as many as the output); <paramref name="output"/> is interleaved.
    /// </summary>
    public void Process(ReadOnlySpan<float> modulator, ReadOnlySpan<float> carrier, int carrierChannels, Span<float> output)
    {
        int frames = modulator.Length;
        int bands = _bands;
        for (int n = 0; n < frames; n++)
        {
            // How loud the voice is in every band.
            double m = modulator[n];
            for (int k = 0; k < bands; k++)
            {
                double level = Math.Abs(BandPass(_modulatorState, k, m));
                double envelope = _modulatorEnvelope[k];
                _modulatorEnvelope[k] = envelope + (level > envelope ? _attack : _release) * (level - envelope);
            }

            // ... read off shifted bands for the formant shift.
            for (int k = 0; k < bands; k++)
            {
                double source = _formantSource[k];
                int below = (int)Math.Floor(source);
                double fraction = source - below;
                double low = below >= 0 && below < bands ? _modulatorEnvelope[below] : 0;
                double high = below + 1 >= 0 && below + 1 < bands ? _modulatorEnvelope[below + 1] : 0;
                _gains[k] = low + fraction * (high - low);
            }

            double consonant = 0;
            if (Consonants > 0)
            {
                double y = _consonant.B0 * m + _consonant1;
                _consonant1 = _consonant.B1 * m - _consonant.A1 * y + _consonant2;
                _consonant2 = _consonant.B2 * m - _consonant.A2 * y;
                consonant = Consonants * y;
            }

            for (int ch = 0; ch < carrierChannels; ch++)
            {
                double c = carrier[n * carrierChannels + ch];
                var state = _carrierState[ch];
                var envelopes = _carrierEnvelope[ch];
                double floor = 1e-5 + 0.003 * _carrierLoudest[ch];
                double loudest = 0, sum = 0;
                for (int k = 0; k < bands; k++)
                {
                    double band = BandPass(state, k, c);
                    double level = Math.Abs(band);
                    double envelope = envelopes[k] += (level > envelopes[k] ? _carrierAttack : _carrierRelease) * (level - envelopes[k]);
                    loudest = Math.Max(loudest, envelope);
                    sum += band / Math.Max(envelope, floor) * _gains[k];
                }

                _carrierLoudest[ch] = loudest;
                float value = (float)(sum * Makeup + consonant);
                if (carrierChannels == 1)
                {
                    for (int o = 0; o < _channels; o++)
                    {
                        output[n * _channels + o] = value;
                    }
                }
                else if (ch < _channels)
                {
                    output[n * _channels + ch] = value;
                }
            }
        }
    }

    /// <summary>Identical band-pass stages in a row for one band (state: 2 values per stage).</summary>
    private double BandPass(double[] state, int band, double x)
    {
        double b0 = _b0[band], a1 = _a1[band], a2 = _a2[band];
        int s = band * 2 * Stages;
        for (int stage = 0; stage < Stages; stage++, s += 2)
        {
            double y = b0 * x + state[s];
            state[s] = -a1 * y + state[s + 1];
            state[s + 1] = -b0 * x - a2 * y;
            x = y;
        }

        return x;
    }

    /// <summary>Position on the band scale: logarithmic well above <see cref="CornerHz"/>, linear well below.</summary>
    private static double Mel(double hz) => Math.Log(1 + hz / CornerHz);

    private static double Hz(double mel) => CornerHz * (Math.Exp(mel) - 1);

    private void Design()
    {
        double highest = Math.Min(HighestHz, 0.42 * _sampleRate);
        _lowestMel = Mel(LowestHz);
        _melPerBand = (Mel(highest) - _lowestMel) / (_bands - 1);

        // Neighbouring bands meet about 3 dB down: each stage is wider than its band by the factor
        // that n identical stages narrow a band-pass by.
        double narrowing = Math.Sqrt(Math.Pow(2, 1.0 / Stages) - 1);
        for (int k = 0; k < _bands; k++)
        {
            double mel = _lowestMel + k * _melPerBand;
            _centres[k] = Hz(mel);
            double width = Hz(mel + _melPerBand / 2) - Hz(mel - _melPerBand / 2);
            var c = BiquadCoefficients.Design(BiquadType.BandPass, _sampleRate, _centres[k], _centres[k] * narrowing / width);
            _b0[k] = c.B0;
            _a1[k] = c.A1;
            _a2[k] = c.A2;
        }

        UpdateFormantShift();
    }

    /// <summary>For every carrier band, the (fractional) band of the voice it takes its loudness from.</summary>
    private void UpdateFormantShift()
    {
        if (_bands == 0)
        {
            return;
        }

        double ratio = Math.Pow(2, _formantSemitones / 12);
        for (int k = 0; k < _bands; k++)
        {
            _formantSource[k] = (Mel(_centres[k] / ratio) - _lowestMel) / _melPerBand;
        }
    }
}
