namespace VoiceChanger.Core.Dsp;

/// <summary>
/// Low latency time-domain pitch shifter.
/// <para>
/// The input is written to a delay line read by two taps ("grains") that move through it at the
/// speed of the pitch ratio. Their gains are complementary sin² curves, half a grain apart, so each
/// tap is silent at the moment it has to jump back. Unlike the classic "rotating tape head" design,
/// the jump target is not fixed: it is chosen by cross-correlation against the tap that is currently
/// audible, so both grains are in phase while they overlap. That removes most of the warbling and
/// comb filtering of the naive approach while keeping the delay around half a window.
/// </para>
/// </summary>
public sealed class GranularPitchShifter
{
    private const int InterpolationMargin = 2;
    private const double SearchSeconds = 0.012;
    private const double CorrelationSeconds = 0.006;

    private readonly int _channels;
    private readonly float[][] _lines;
    private readonly float[] _analysis;
    private readonly int _mask;
    private readonly int _maxWindow;
    private readonly int _maxSearch;
    private readonly int _correlationLength;
    private readonly float[] _reference;
    private readonly float[] _candidates;
    private int _write;
    private double _phase;
    private double _delayA;
    private double _delayB;
    private int _windowFrames;

    public GranularPitchShifter(int channels, int maxWindowFrames, int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxWindowFrames, 16);
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1000);

        _channels = channels;
        _maxWindow = maxWindowFrames;
        _maxSearch = (int)(SearchSeconds * sampleRate);
        _correlationLength = (int)(CorrelationSeconds * sampleRate);
        _reference = new float[_correlationLength];
        _candidates = new float[_maxSearch + _correlationLength + 1];

        int length = (int)System.Numerics.BitOperations.RoundUpToPowerOf2(
            (uint)(maxWindowFrames + _maxSearch + _correlationLength + InterpolationMargin + 8));
        _mask = length - 1;
        _lines = new float[channels][];
        for (int ch = 0; ch < channels; ch++)
        {
            _lines[ch] = new float[length];
        }

        // Grain alignment looks at the average of all channels so the stereo image stays coherent.
        _analysis = channels == 1 ? _lines[0] : new float[length];
        _windowFrames = maxWindowFrames / 2;
        Reset();
    }

    /// <summary>Output frequency divided by input frequency.</summary>
    public double Ratio { get; set; } = 1;

    /// <summary>
    /// How far the read taps sweep through the delay line during one grain, in frames. Longer
    /// windows mean longer, smoother grains but more delay.
    /// </summary>
    public int WindowFrames
    {
        get => _windowFrames;
        set => _windowFrames = Math.Clamp(value, 16, _maxWindow);
    }

    public int LatencyFrames => LatencyForSearch(_windowFrames, _maxSearch);

    /// <summary>Average delay for a window length at a sample rate, in frames.</summary>
    public static int LatencyFor(int windowFrames, int sampleRate) =>
        LatencyForSearch(windowFrames, (int)(SearchSeconds * sampleRate));

    public void Reset()
    {
        foreach (var line in _lines)
        {
            Array.Clear(line);
        }

        Array.Clear(_analysis);
        _write = 0;
        _phase = 0;
        _delayA = InterpolationMargin + _windowFrames / 2.0;
        _delayB = _delayA;
    }

    /// <summary>Shifts interleaved audio in place.</summary>
    public void Process(Span<float> interleaved)
    {
        int channels = _channels;
        int frames = interleaved.Length / channels;
        double ratio = Ratio;
        double delayStep = 1.0 - ratio;
        double phaseStep = Math.Abs(1.0 - ratio) / _windowFrames;
        double maxDelay = InterpolationMargin + _windowFrames + SearchFrames;
        float channelScale = 1f / channels;

        for (int frame = 0, i = 0; frame < frames; frame++, i += channels)
        {
            _write = (_write + 1) & _mask;
            float sum = 0;
            for (int ch = 0; ch < channels; ch++)
            {
                float sample = interleaved[i + ch];
                _lines[ch][_write] = sample;
                sum += sample;
            }

            if (channels > 1)
            {
                _analysis[_write] = sum * channelScale;
            }

            _delayA = Math.Clamp(_delayA + delayStep, InterpolationMargin, maxDelay);
            _delayB = Math.Clamp(_delayB + delayStep, InterpolationMargin, maxDelay);

            double previous = _phase;
            _phase += phaseStep;
            if (_phase >= 1)
            {
                // Tap A is silent now: start its next grain in phase with tap B.
                _phase -= 1;
                _delayA = Splice(_delayB, ratio);
            }
            else if (previous < 0.5 && _phase >= 0.5)
            {
                // Tap B is silent now: start its next grain in phase with tap A.
                _delayB = Splice(_delayA, ratio);
            }

            double s = Math.Sin(Math.PI * _phase);
            float gainA = (float)(s * s);
            float gainB = 1f - gainA;

            for (int ch = 0; ch < channels; ch++)
            {
                var line = _lines[ch];
                interleaved[i + ch] = gainA * Read(line, _delayA) + gainB * Read(line, _delayB);
            }
        }
    }

    private int SearchFrames => Math.Min(_maxSearch, _windowFrames / 2);

    private static int LatencyForSearch(int windowFrames, int maxSearch) =>
        InterpolationMargin + windowFrames / 2 + Math.Min(maxSearch, windowFrames / 2) / 2;

    /// <summary>
    /// Picks the start delay of a new grain. When pitching up the delay shrinks during the grain so it
    /// starts one window back; when pitching down it grows so it starts near the write head. Within a
    /// search range the offset whose waveform best matches what the other tap plays is chosen.
    /// </summary>
    private double Splice(double otherDelay, double ratio)
    {
        double baseDelay = ratio > 1 ? InterpolationMargin + _windowFrames : InterpolationMargin;
        int search = SearchFrames;
        int length = Math.Min(_correlationLength, search);
        if (search < 2 || length < 8)
        {
            return baseDelay;
        }

        int mask = _mask;
        int referenceStart = _write - (int)Math.Round(otherDelay);
        int candidateStart = _write - (int)Math.Round(baseDelay);
        double referenceEnergy = 0;
        for (int j = 0; j < length; j++)
        {
            float value = _analysis[(referenceStart - j) & mask];
            _reference[j] = value;
            referenceEnergy += value * value;
        }

        if (referenceEnergy < 1e-9)
        {
            return baseDelay; // Silence: nothing to align with.
        }

        for (int j = 0; j <= search + length; j++)
        {
            _candidates[j] = _analysis[(candidateStart - j) & mask];
        }

        double energy = 0;
        for (int j = 0; j < length; j++)
        {
            energy += _candidates[j] * _candidates[j];
        }

        int best = 0;
        double bestScore = double.NegativeInfinity;
        for (int offset = 0; offset <= search; offset++)
        {
            double correlation = 0;
            for (int j = 0; j < length; j++)
            {
                correlation += _reference[j] * _candidates[offset + j];
            }

            double score = correlation / Math.Sqrt(energy + 1e-9);
            if (score > bestScore)
            {
                bestScore = score;
                best = offset;
            }

            // Slide the energy window one frame further into the past.
            float leaving = _candidates[offset];
            float entering = _candidates[offset + length];
            energy = Math.Max(0, energy - leaving * leaving + entering * entering);
        }

        return baseDelay + best;
    }

    private float Read(float[] line, double delay)
    {
        double position = _write - delay;
        int index = (int)Math.Floor(position);
        float t = (float)(position - index);
        int mask = _mask;

        float xm1 = line[(index - 1) & mask];
        float x0 = line[index & mask];
        float x1 = line[(index + 1) & mask];
        float x2 = line[(index + 2) & mask];

        // Catmull-Rom cubic interpolation.
        float c1 = 0.5f * (x1 - xm1);
        float c2 = xm1 - 2.5f * x0 + 2f * x1 - 0.5f * x2;
        float c3 = 0.5f * (x2 - xm1) + 1.5f * (x0 - x1);
        return ((c3 * t + c2) * t + c1) * t + x0;
    }
}
