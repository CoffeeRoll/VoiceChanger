namespace VoiceChanger.Core.Dsp;

/// <summary>
/// Formant-preserving pitch shifter for voices: pitch-synchronous overlap-add (TD-PSOLA,
/// E. Moulines and F. Charpentier, 1990).
/// <para>
/// A voice is a train of glottal pulses, each one ringing through the vocal tract. The pitch is the
/// pulse rate; the formants are the shape of each ring. This shifter marks every pulse in the input,
/// cuts a two-period grain around it and lays the grains down again at the new pulse rate: further
/// apart to lower the pitch, closer together to raise it. Each grain keeps its own shape, so the
/// formants stay exactly where they were and the high harmonics stay as clean as in the original,
/// for any shift. Unvoiced sounds (breath, "s", "f") have no pulses and pass through unchanged,
/// which is also what happens in a real voice speaking lower or higher.
/// </para>
/// <para>
/// Pulses are found without a separate pitch detector: while nothing is voiced, the period of the
/// last few milliseconds is estimated with YIN; once a voice is found, a mark is put on the loudest
/// point of its latest period and marks are added backwards to where the voice began (grains are
/// laid down only just before they are played, so those marks are still free to change). From then
/// on each mark is placed where the period before it best matches the period before the previous
/// mark. Grains can also be resampled, which moves the formants on their own
/// (<see cref="FormantRatio"/>).
/// </para>
/// </summary>
public sealed class PsolaPitchShifter
{
    private const double MinFrequency = 75;
    private const double MaxFrequency = 900;
    private const double UnvoicedSeconds = 0.005;
    private const double GateDb = -55;
    private const double OnsetThreshold = 0.2;
    private const double TrackingCorrelation = 0.5;
    private const double MinTrackRatio = 0.8;
    private const double MaxTrackRatio = 1.25;
    private const double AnalysisRate = 16000;
    private const int MaxMarks = 128;

    private readonly int _channels;
    private readonly int _minPeriod;
    private readonly int _maxPeriod;
    private readonly int _unvoicedPeriod;
    private readonly int _stride;
    private readonly float _gate;
    private readonly float[][] _input;
    private readonly float[] _analysis;
    private readonly float[][] _output;
    private readonly int _inputMask;
    private readonly int _outputMask;
    private readonly double[] _difference;
    private readonly Mark[] _marks = new Mark[MaxMarks];

    private int _latency;
    private long _written;
    private long _lastMark;
    private double _lastOffset;
    private int _lastPeriod;
    private bool _lastVoiced;
    private long _usedUpTo;
    private int _markStart;
    private int _markCount;
    private double _nextOutput;
    private double _ratio = 1;
    private double _formantRatio = 1;

    public PsolaPitchShifter(int channels, int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 8000);

        _channels = channels;
        _minPeriod = (int)(sampleRate / MaxFrequency);
        _maxPeriod = (int)Math.Ceiling(sampleRate / MinFrequency);
        _unvoicedPeriod = (int)(UnvoicedSeconds * sampleRate);
        _stride = Math.Max(1, (int)Math.Round(sampleRate / AnalysisRate));
        _gate = (float)Math.Pow(10, GateDb / 20);
        _difference = new double[_maxPeriod / _stride + 2];

        // History for the analysis (two periods back from a mark that is up to one period old) and
        // for grains read around marks that lag the input by the latency.
        int inputLength = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)(8 * _maxPeriod + 64));
        int outputLength = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)(6 * _maxPeriod + 64));
        _inputMask = inputLength - 1;
        _outputMask = outputLength - 1;
        _input = new float[channels][];
        _output = new float[channels][];
        for (int ch = 0; ch < channels; ch++)
        {
            _input[ch] = new float[inputLength];
            _output[ch] = new float[outputLength];
        }

        _analysis = channels == 1 ? _input[0] : new float[inputLength];
        _latency = LatencyFor(1);
        Reset();
    }

    /// <summary>Output pitch divided by input pitch (0.25 to 4).</summary>
    public double Ratio
    {
        get => _ratio;
        set => _ratio = Math.Clamp(value, 0.25, 4.0);
    }

    /// <summary>
    /// How far the formants move, as a frequency ratio (1 = they stay put; 0.5 to 2). Raising it
    /// beyond about +4 semitones needs a longer look-ahead: <see cref="LatencyFrames"/> grows and
    /// the shifter restarts.
    /// </summary>
    public double FormantRatio
    {
        get => _formantRatio;
        set
        {
            _formantRatio = Math.Clamp(value, 0.5, 2.0);
            int latency = LatencyFor(_formantRatio);
            if (latency != _latency)
            {
                _latency = latency;
                Reset();
            }
        }
    }

    /// <summary>Delay through the shifter, in frames.</summary>
    public int LatencyFrames => _latency;

    public void Reset()
    {
        foreach (var line in _input)
        {
            Array.Clear(line);
        }

        foreach (var line in _output)
        {
            Array.Clear(line);
        }

        Array.Clear(_analysis);
        _written = 0;
        _lastMark = -_unvoicedPeriod;
        _lastOffset = 0;
        _lastPeriod = _unvoicedPeriod;
        _lastVoiced = false;
        _usedUpTo = long.MinValue;
        _markStart = 0;
        _markCount = 0;
        _nextOutput = 0;
    }

    /// <summary>Shifts interleaved audio in place.</summary>
    public void Process(Span<float> interleaved)
    {
        int channels = _channels;
        float channelScale = 1f / channels;
        for (int i = 0; i + channels <= interleaved.Length; i += channels)
        {
            int write = (int)(_written & _inputMask);
            float sum = 0;
            for (int ch = 0; ch < channels; ch++)
            {
                float sample = interleaved[i + ch];
                _input[ch][write] = sample;
                sum += sample;
            }

            if (channels > 1)
            {
                _analysis[write] = sum * channelScale;
            }

            _written++;
            while (TryFindNextMark())
            {
            }

            while (TryPlaceGrain())
            {
            }

            // Emit the sample that every grain overlapping it has been added to by now.
            long emit = _written - 1 - _latency;
            int index = (int)(emit & _outputMask);
            for (int ch = 0; ch < channels; ch++)
            {
                interleaved[i + ch] = emit < 0 ? 0 : _output[ch][index];
                _output[ch][index] = 0;
            }
        }
    }

    /// <summary>
    /// The look-ahead needed for a formant ratio: grains reach up to one period (times the formant
    /// ratio, plus half a period to the nearest mark) past their output mark, marks are placed up to
    /// 1.25 periods behind the newest sample, and a grain is laid down one period before it is played.
    /// </summary>
    private int LatencyFor(double formantRatio) =>
        (int)Math.Ceiling(_maxPeriod * (1 + Math.Max(MaxTrackRatio, formantRatio + 0.5))) + 16;

    private float Sample(long position) => _analysis[(int)(position & _inputMask)];

    /// <summary>Places the next analysis mark once enough input is there. Returns whether it did.</summary>
    private bool TryFindNextMark()
    {
        if (_markCount == MaxMarks)
        {
            return false;
        }

        if (_lastVoiced)
        {
            // Follow the voice: the next pulse is about one period on and looks like the last one.
            int period = _lastPeriod;
            long low = _lastMark + (int)(MinTrackRatio * period);
            long high = _lastMark + (int)Math.Ceiling(MaxTrackRatio * period);
            if (_written <= high)
            {
                return false;
            }

            long best = -1;
            double bestScore = TrackingCorrelation;
            double before = 0, after = 0, previous = 0;
            for (long candidate = low; candidate <= high + 1; candidate++)
            {
                double score = Correlation(_lastMark, candidate, period);
                if (candidate == best + 1)
                {
                    after = score;
                }

                if (candidate <= high && score > bestScore)
                {
                    bestScore = score;
                    best = candidate;
                    before = previous;
                }

                previous = score;
            }

            if (best >= 0)
            {
                // Sub-sample position from a parabola through the correlation peak, so that the
                // pitch is exact even though marks fall on whole samples.
                double curvature = before - 2 * bestScore + after;
                double offset = best > low && curvature < 0 ? Math.Clamp(0.5 * (before - after) / curvature, -0.5, 0.5) : 0;
                AddMark(best, offset, best + offset - (_lastMark + _lastOffset), voiced: true);
                return true;
            }

            // The voice stopped (or turned into noise).
            AddMark(_lastMark + _unvoicedPeriod, 0, _unvoicedPeriod, voiced: false);
            return true;
        }

        long position = _lastMark + _unvoicedPeriod;
        if (_written <= position)
        {
            return false;
        }

        int onset = EstimatePeriod(position);
        if (onset > 0)
        {
            StartVoice(position, onset);
            return true;
        }

        AddMark(position, 0, _unvoicedPeriod, voiced: false);
        return true;
    }

    /// <summary>
    /// A voice was found in the samples before <paramref name="position"/>: marks its loudest
    /// point in the latest period, then replaces the unvoiced marks that are not in use yet by
    /// pulse marks going back to where the voice began.
    /// </summary>
    private void StartVoice(long position, int period)
    {
        long anchor = position - period;
        float loudest = -1;
        for (long p = position - period; p < position; p++)
        {
            float magnitude = Math.Abs(Sample(p));
            if (magnitude > loudest)
            {
                loudest = magnitude;
                anchor = p;
            }
        }

        // The analysed stretch (one longest period) was voiced throughout.
        long start = Math.Max(position - _maxPeriod, _usedUpTo + 1);
        while (_markCount > 0 && Last.Position >= start && Last.Position > _usedUpTo && !Last.Voiced)
        {
            _markCount--;
        }

        long floor = _markCount > 0 ? Math.Max(start, Last.Position + period / 2) : start;
        Span<long> earlier = stackalloc long[16];
        int count = 0;
        long next = anchor;
        while (count < earlier.Length && next - period >= floor)
        {
            // Refine each step back by matching the period before it with the one before the next mark.
            long guess = next - period;
            long best = guess;
            double bestScore = double.NegativeInfinity;
            for (long candidate = Math.Max(floor, guess - period / 8); candidate <= guess + period / 8 && candidate < next; candidate++)
            {
                double score = Correlation(next, candidate, period);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = candidate;
                }
            }

            earlier[count++] = best;
            next = best;
        }

        for (int i = count - 1; i >= 0; i--)
        {
            AddMark(earlier[i], 0, period, voiced: true);
        }

        AddMark(anchor, 0, period, voiced: true);
    }

    private Mark Last => _marks[(_markStart + _markCount - 1) % MaxMarks];

    /// <summary>Normalised correlation of the period before <paramref name="a"/> with the period before <paramref name="b"/>.</summary>
    private double Correlation(long a, long b, int length)
    {
        double cross = 0, energyA = 0, energyB = 0;
        for (int j = 1; j <= length; j++)
        {
            float x = Sample(a - j);
            float y = Sample(b - j);
            cross += x * y;
            energyA += x * x;
            energyB += y * y;
        }

        if (energyA < _gate * _gate * length || energyB < _gate * _gate * length)
        {
            return 0;
        }

        return cross / Math.Sqrt(energyA * energyB);
    }

    /// <summary>
    /// Period (in samples) of the audio just before <paramref name="position"/>, or 0 when it is not
    /// voiced. YIN's cumulative mean normalised difference on every <c>stride</c>-th sample: coarse,
    /// but the tracking refines the period from the next mark on.
    /// </summary>
    private int EstimatePeriod(long position)
    {
        int stride = _stride;
        int length = _maxPeriod / stride;
        int maxLag = _maxPeriod / stride;
        int minLag = Math.Max(2, _minPeriod / stride);

        double energy = 0;
        for (int j = 1; j <= length; j++)
        {
            float x = Sample(position - j * stride);
            energy += x * x;
        }

        if (energy < _gate * _gate * length)
        {
            return 0;
        }

        double running = 0;
        for (int lag = 1; lag <= maxLag; lag++)
        {
            double d = 0;
            for (int j = 1; j <= length; j++)
            {
                float delta = Sample(position - j * stride) - Sample(position - (j + lag) * stride);
                d += delta * delta;
            }

            running += d;
            _difference[lag] = running > 0 ? d * lag / running : 1;
        }

        for (int lag = minLag; lag < maxLag; lag++)
        {
            if (_difference[lag] < OnsetThreshold)
            {
                while (lag + 1 < maxLag && _difference[lag + 1] < _difference[lag])
                {
                    lag++;
                }

                return lag * stride;
            }
        }

        return 0;
    }

    private void AddMark(long position, double offset, double period, bool voiced)
    {
        _marks[(_markStart + _markCount) % MaxMarks] = new Mark(position, offset, period, voiced);
        _markCount++;
        _lastMark = position;
        _lastOffset = offset;
        _lastPeriod = (int)Math.Round(period);
        _lastVoiced = voiced;
    }

    /// <summary>Adds the grain for the next output mark once its analysis mark and samples are there.</summary>
    private bool TryPlaceGrain()
    {
        if (_markCount == 0)
        {
            return false;
        }

        double output = _nextOutput;
        long target = (long)Math.Round(output);
        if (_lastMark < target || target > _written - 1 - _latency + _maxPeriod)
        {
            // A closer mark may still come, or it is too early: lay grains down only just before
            // they are played, so that marks can still be revised when a voice starts.
            return false;
        }

        // Marks far behind the output can go; then pick the one nearest the output mark.
        while (_markCount > 1 && Math.Abs(_marks[(_markStart + 1) % MaxMarks].Position - target) <= Math.Abs(_marks[_markStart].Position - target))
        {
            _markStart = (_markStart + 1) % MaxMarks;
            _markCount--;
        }

        var mark = _marks[_markStart];
        double half = mark.Period;
        double formant = mark.Voiced ? _formantRatio : 1;
        if (_written <= mark.Position + (long)Math.Ceiling(half * formant) + 3)
        {
            return false;
        }

        // Voiced grains move to the new pulse rate (keeping the loudness of the voice the same);
        // unvoiced ones stay where they are. Both mark and grain positions have sub-sample precision.
        double spacing = mark.Voiced ? half / _ratio : half;
        float gain = mark.Voiced ? (float)Math.Sqrt(1 / _ratio) : 1f;
        double centre = mark.Position + mark.Offset;
        long firstWritable = _written - 1 - _latency;
        int write = (int)((_written - 1) & _inputMask);
        for (long destination = (long)Math.Floor(output - half) + 1; destination < output + half; destination++)
        {
            if (destination < firstWritable)
            {
                continue; // Too late for this sample (only after a restart).
            }

            double j = destination - output;
            float window = gain * (float)(0.5 + 0.5 * Math.Cos(Math.PI * j / half));
            double delay = _written - 1 - (centre + j * formant);
            int outIndex = (int)(destination & _outputMask);
            for (int ch = 0; ch < _channels; ch++)
            {
                _output[ch][outIndex] += WaveformAligner.ReadCubic(_input[ch], _inputMask, write, delay) * window;
            }
        }

        _usedUpTo = Math.Max(_usedUpTo, mark.Position);
        _nextOutput += spacing;
        return true;
    }

    /// <param name="Position">Sample the mark is on.</param>
    /// <param name="Offset">Sub-sample correction of the position (−0.5 to 0.5).</param>
    /// <param name="Period">Distance to the previous mark: the grain extends this far either side.</param>
    private readonly record struct Mark(long Position, double Offset, double Period, bool Voiced);
}
