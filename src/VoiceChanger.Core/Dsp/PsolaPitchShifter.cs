namespace VoiceChanger.Core.Dsp;

/// <summary>
/// Chooses the pitch ratio for one grain of a <see cref="PsolaPitchShifter"/>.
/// </summary>
/// <param name="period">Period of the pulse the grain is cut around, in samples, or 0 for unvoiced audio.</param>
/// <param name="time">Position of the grain in the output stream, in samples.</param>
/// <returns>Output pitch divided by input pitch for this grain.</returns>
public delegate double PitchCorrection(double period, double time);

/// <summary>
/// Formant-preserving pitch shifter for voices: pitch-synchronous overlap-add of the excitation
/// (LP-PSOLA; after E. Moulines and F. Charpentier, 1990).
/// <para>
/// A voice is a train of glottal pulses, each one ringing through the vocal tract. The pitch is the
/// pulse rate; the formants are the shape of each ring. A linear predictor splits the input into
/// the two: the vocal tract (an all-pole filter, refitted every 5 ms and blended between fits) and
/// what drives it (the residual: mostly one sharp click per pulse, plus breath). This shifter marks
/// every pulse, cuts a two-period grain of the residual around it, lays the grains down again at the
/// new pulse rate (further apart to lower the pitch, closer together to raise it) and plays the
/// result through the vocal tract again. The formants stay exactly where they were, the high
/// harmonics stay as clean as in the original, and the vocal tract rings on between pulses just as
/// in a real lower voice. (Respacing grains of the sound itself, as plain TD-PSOLA does, leaves the
/// gaps between them nearly silent an octave down, which sounds gritty.) Unvoiced sounds (breath,
/// "s", "f") have no pulses and pass through unchanged, which is also what happens in a real voice
/// speaking lower or higher; with nothing shifted the filter undoes its own analysis exactly.
/// Playing the excitation through a vocal tract with its resonances moved shifts the formants on
/// their own (<see cref="FormantRatio"/>).
/// </para>
/// <para>
/// Pulses are found without a separate pitch detector: while nothing is voiced, the period of the
/// last few milliseconds is estimated with YIN; once a voice is found, a mark is put on the sharpest
/// click of the residual in its latest period (where the glottis closes, so that a grain reaching a
/// period either side stops just short of the neighbouring pulses) and marks are added backwards to
/// where the voice began (grains are laid down only just before they are played, so those marks are
/// still free to change). From then on each mark is placed where the period before it best matches
/// the period before the previous mark.
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
    private const double PredictionHopSeconds = 0.005;
    private const double PredictionWindowSeconds = 0.025;
    private const double OrderPerHertz = 1 / 1000.0;
    private const double SynthesisLimit = 8;

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
    private readonly LinearPredictor _predictor;
    private readonly double _emphasis;
    private readonly int _hop;
    private readonly int _slotMask;
    private readonly double[][] _tract;
    private readonly double[][] _playedTract;
    private readonly double[] _playedGain;
    private readonly double[] _reflection;
    private readonly double[][] _analysisState;
    private readonly double[][] _synthesisState;
    private readonly double[] _synthesisLast;
    private readonly float[][] _residual;
    private readonly int _latency;
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

        // Grains reach up to one period (plus half a period to the nearest mark) past their output
        // mark, marks are placed up to 1.25 periods behind the newest sample, and a grain is laid
        // down one period before it is played.
        _latency = (int)Math.Ceiling(_maxPeriod * 2.5) + 16;

        int order = Math.Clamp((int)Math.Round(sampleRate * OrderPerHertz), 12, 64);
        _hop = Math.Max(1, (int)Math.Round(sampleRate * PredictionHopSeconds));
        _predictor = new LinearPredictor(order, (int)Math.Round(sampleRate * PredictionWindowSeconds), sampleRate);
        _emphasis = _predictor.PreEmphasis;

        // Vocal tracts are kept from the newest sample back to the one being played.
        int slots = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)(_latency / _hop + 4));
        _slotMask = slots - 1;
        _tract = new double[slots][];
        _playedTract = new double[slots][];
        _playedGain = new double[slots];
        for (int i = 0; i < slots; i++)
        {
            _tract[i] = new double[order];
            _playedTract[i] = new double[order];
        }

        _reflection = new double[order];
        _residual = new float[channels][];
        _analysisState = new double[channels][];
        _synthesisState = new double[channels][];
        _synthesisLast = new double[channels];
        for (int ch = 0; ch < channels; ch++)
        {
            _residual[ch] = new float[inputLength];
            _analysisState[ch] = new double[order];
            _synthesisState[ch] = new double[order];
        }

        Reset();
    }

    /// <summary>Output pitch divided by input pitch (0.25 to 4).</summary>
    public double Ratio
    {
        get => _ratio;
        set => _ratio = Math.Clamp(value, 0.25, 4.0);
    }

    /// <summary>
    /// When set, called for every grain to choose its ratio instead of <see cref="Ratio"/>. It gets
    /// the measured period of the very pulse being moved, so a pitch correction lands exactly on
    /// time; and since a grain's spacing is that period divided by the ratio, a correction to a
    /// target pitch comes out exactly at that pitch even if the period was measured slightly off.
    /// Runs on the audio thread.
    /// </summary>
    public PitchCorrection? Correction { get; set; }

    /// <summary>How far the formants move, as a frequency ratio (1 = they stay put; 0.5 to 2).</summary>
    public double FormantRatio
    {
        get => _formantRatio;
        set => _formantRatio = Math.Clamp(value, 0.5, 2.0);
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

        foreach (var line in _residual)
        {
            Array.Clear(line);
        }

        for (int ch = 0; ch < _channels; ch++)
        {
            Array.Clear(_analysisState[ch]);
            Array.Clear(_synthesisState[ch]);
        }

        for (int i = 0; i <= _slotMask; i++)
        {
            Array.Clear(_tract[i]);
            Array.Clear(_playedTract[i]);
            _playedGain[i] = 1;
        }

        Array.Clear(_synthesisLast);
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

            if (_written % _hop == 0)
            {
                AnalyseVocalTract();
            }

            // The excitation: what the vocal tract cannot predict (pre-emphasis, then the inverse
            // filter as a lattice).
            Blend(_tract, null, _written, _reflection);
            for (int ch = 0; ch < channels; ch++)
            {
                float[] line = _input[ch];
                double[] state = _analysisState[ch];
                double forward = line[write] - _emphasis * line[(write - 1) & _inputMask];
                double backward = forward;
                for (int k = 0; k < state.Length; k++)
                {
                    double delayed = state[k];
                    state[k] = backward;
                    backward = delayed + _reflection[k] * forward;
                    forward += _reflection[k] * delayed;
                }

                _residual[ch][write] = (float)forward;
            }

            _written++;
            while (TryFindNextMark())
            {
            }

            while (TryPlaceGrain())
            {
            }

            if (_nextOutput < _written - 1 - _latency - _maxPeriod || _lastMark < _written - 4 * _maxPeriod)
            {
                // Grains or marks fell behind (should never happen): rather than going quiet,
                // start again from what is being played now.
                Resynchronise();
            }

            // Emit the sample that every grain overlapping it has been added to by now, played
            // through the vocal tract the excitation was taken from.
            long emit = _written - 1 - _latency;
            if (emit < 0)
            {
                for (int ch = 0; ch < channels; ch++)
                {
                    interleaved[i + ch] = 0;
                }

                continue;
            }

            int index = (int)(emit & _outputMask);
            double gain = Blend(_playedTract, _playedGain, emit, _reflection);
            for (int ch = 0; ch < channels; ch++)
            {
                double[] state = _synthesisState[ch];
                double forward = gain * _output[ch][index];
                for (int k = state.Length - 1; k >= 0; k--)
                {
                    forward -= _reflection[k] * state[k];
                    if (k + 1 < state.Length)
                    {
                        state[k + 1] = state[k] + _reflection[k] * forward;
                    }
                }

                state[0] = forward;
                double y = forward + _emphasis * _synthesisLast[ch];
                if (!(Math.Abs(y) < SynthesisLimit))
                {
                    // Out of bounds (should never happen): start the filter afresh.
                    Array.Clear(state);
                    y = 0;
                }

                _synthesisLast[ch] = y;
                interleaved[i + ch] = (float)y;
                _output[ch][index] = 0;
            }
        }
    }

    /// <summary>
    /// The vocal tract at a sample: a blend from the one fitted at the start of its hop to the one
    /// fitted at the start of the next, so that it changes smoothly. Returns the blended gain.
    /// </summary>
    private double Blend(double[][] tracts, double[]? gains, long sample, double[] reflection)
    {
        long hop = sample / _hop;
        double fraction = (double)(sample - hop * _hop) / _hop;
        int from = (int)((hop - 1) & _slotMask);
        int to = (int)(hop & _slotMask);
        double[] a = tracts[from], b = tracts[to];
        for (int k = 0; k < reflection.Length; k++)
        {
            reflection[k] = a[k] + fraction * (b[k] - a[k]);
        }

        return gains is null ? 1 : gains[from] + fraction * (gains[to] - gains[from]);
    }

    /// <summary>
    /// Fits the vocal tract to the latest stretch of input (ending at the sample just written), and
    /// the one to play the excitation through: the same, or the same with its formants moved.
    /// </summary>
    private void AnalyseVocalTract()
    {
        int slot = (int)(_written / _hop) & _slotMask;
        int write = (int)(_written & _inputMask);
        Span<double> frame = _predictor.Frame;
        int last = frame.Length - 1;
        for (int i = 0; i <= last; i++)
        {
            int at = write - (last - i);
            frame[i] = _analysis[at & _inputMask] - _emphasis * _analysis[(at - 1) & _inputMask];
        }

        _predictor.Analyse(_tract[slot]);
        if (_formantRatio == 1)
        {
            _tract[slot].CopyTo(_playedTract[slot], 0);
            _playedGain[slot] = 1;
        }
        else
        {
            _playedGain[slot] = _predictor.Warp(_formantRatio, _playedTract[slot]);
        }
    }

    private void Resynchronise()
    {
        long now = _written - 1 - _latency;
        _markStart = 0;
        _markCount = 0;
        _lastMark = now - _unvoicedPeriod;
        _lastOffset = 0;
        _lastPeriod = _unvoicedPeriod;
        _lastVoiced = false;
        _usedUpTo = long.MinValue;
        _nextOutput = now + 1;
        Resyncs++;
    }

    /// <summary>Number of times the shifter had to start again (for diagnostics; should stay 0).</summary>
    internal int Resyncs { get; private set; }

    private float Sample(long position) => _analysis[(int)(position & _inputMask)];

    /// <summary>Places the next analysis mark once enough input is there. Returns whether it did.</summary>
    private bool TryFindNextMark()
    {
        if (_lastVoiced)
        {
            // Follow the voice: the next pulse is about one period on and looks like the last one,
            // within the range of voices the shifter handles.
            int period = _lastPeriod;
            long low = _lastMark + Math.Max(_minPeriod, (int)(MinTrackRatio * period));
            long high = _lastMark + Math.Min(_maxPeriod, (int)Math.Ceiling(MaxTrackRatio * period));
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

            if (best >= 0 && low < high)
            {
                // Sub-sample position from a parabola through the correlation peak, so that the
                // pitch is exact even though marks fall on whole samples.
                double curvature = before - 2 * bestScore + after;
                double offset = best > low && curvature < 0 ? Math.Clamp(0.5 * (before - after) / curvature, -0.5, 0.5) : 0;

                // The search compared whole-sample positions, so it measured the period from the
                // last mark's whole sample; the exact mark keeps the last mark's fraction on top.
                double measured = best + offset - _lastMark;
                double exact = best + offset + _lastOffset;
                long nearest = (long)Math.Round(exact);
                AddMark(nearest, exact - nearest, measured, voiced: true);
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
            StartVoice(position, RefinePeriod(position, onset));
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
    private void StartVoice(long position, double exactPeriod)
    {
        int period = (int)Math.Round(exactPeriod);

        // The analysed stretch (one longest period) was voiced throughout: unvoiced marks in it that
        // are not in use yet make way for pulse marks.
        long start = Math.Max(position - _maxPeriod, _usedUpTo + 1);
        while (_markCount > 0 && Last.Position >= start && Last.Position > _usedUpTo && !Last.Voiced)
        {
            _markCount--;
        }

        // New marks must come after the ones that stay, at least half a period on.
        long floor = _markCount > 0 ? Math.Max(start, Last.Position + period / 2) : start;
        long searchFrom = Math.Max(position - period, floor);
        if (searchFrom >= position)
        {
            // No room for a pulse before this point: carry on and look again a little later.
            AddMark(Math.Max(position, floor), 0, _unvoicedPeriod, voiced: false);
            return;
        }

        // The pulse itself: the sharpest click of the excitation.
        long anchor = searchFrom;
        double loudest = -1;
        for (long p = searchFrom; p < position; p++)
        {
            double magnitude = 0;
            for (int ch = 0; ch < _channels; ch++)
            {
                magnitude += Math.Abs(_residual[ch][(int)(p & _inputMask)]);
            }

            if (magnitude > loudest)
            {
                loudest = magnitude;
                anchor = p;
            }
        }

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
            AddMark(earlier[i], 0, exactPeriod, voiced: true);
        }

        AddMark(anchor, 0, exactPeriod, voiced: true);
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

    /// <summary>
    /// Sharpens a coarse period estimate to a fraction of a sample: the lag around it at which the
    /// last period best matches the one before, refined with a parabola through the peak.
    /// </summary>
    private double RefinePeriod(long position, int coarse)
    {
        int low = Math.Max(_minPeriod, coarse - 2 * _stride);
        int high = Math.Min(_maxPeriod, coarse + 2 * _stride);
        int best = coarse;
        double bestScore = double.NegativeInfinity;
        for (int lag = low; lag <= high; lag++)
        {
            double score = Correlation(position, position - lag, lag);
            if (score > bestScore)
            {
                bestScore = score;
                best = lag;
            }
        }

        if (best <= low || best >= high)
        {
            return best;
        }

        double before = Correlation(position, position - (best - 1), best - 1);
        double after = Correlation(position, position - (best + 1), best + 1);
        double curvature = before - 2 * bestScore + after;
        return curvature < 0 ? best + Math.Clamp(0.5 * (before - after) / curvature, -0.5, 0.5) : best;
    }

    private void AddMark(long position, double offset, double period, bool voiced)
    {
        // Grain selection relies on marks being in order (should never be needed).
        if (_markCount > 0 && position <= Last.Position)
        {
            position = Last.Position + 1;
            offset = 0;
        }

        // A full queue means the output is not keeping up (should never happen): the oldest
        // mark is the least useful one.
        if (_markCount == MaxMarks)
        {
            _markStart = (_markStart + 1) % MaxMarks;
            _markCount--;
        }

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
        if (_written <= mark.Position + (long)Math.Ceiling(half) + 3)
        {
            return false;
        }

        // Voiced grains move to the new pulse rate (keeping the loudness of the voice the same);
        // unvoiced ones stay where they are. Both mark and grain positions have sub-sample precision.
        double ratio = Correction is { } correction
            ? Math.Clamp(correction(mark.Voiced ? half : 0, output), 0.25, 4.0)
            : _ratio;
        double spacing = mark.Voiced ? half / ratio : half;
        float gain = mark.Voiced ? (float)Math.Sqrt(1 / ratio) : 1f;
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
            double delay = _written - 1 - (centre + j);
            int outIndex = (int)(destination & _outputMask);
            for (int ch = 0; ch < _channels; ch++)
            {
                _output[ch][outIndex] += WaveformAligner.ReadCubic(_residual[ch], _inputMask, write, delay) * window;
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
