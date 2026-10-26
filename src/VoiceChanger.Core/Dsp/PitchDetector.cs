namespace VoiceChanger.Core.Dsp;

/// <summary>
/// Real-time monophonic pitch detector using the YIN algorithm (A. de Cheveigné and H. Kawahara, 2002).
/// <para>
/// The input is low-pass filtered and decimated to roughly 16 kHz, which is plenty for voice
/// fundamentals and keeps the cost low. Every <see cref="HopSeconds"/> the last 32 ms are analysed:
/// the YIN difference function is computed with FFTs, normalised, and the first dip below a
/// threshold gives the period, refined to a fraction of a sample with parabolic interpolation.
/// Quiet or noisy frames are reported as unvoiced, and single-frame outliers (octave jumps) are
/// removed with a median of three.
/// </para>
/// </summary>
public sealed class PitchDetector
{
    private const double AnalysisRate = 16000;
    private const double DipThreshold = 0.12;
    private const double VoicedThreshold = 0.3;

    private readonly int _decimation;
    private readonly double _rate;
    private readonly int _window;
    private readonly int _minLag;
    private readonly int _maxLag;
    private readonly int _hop;
    private readonly double _gate;
    private readonly BiquadCoefficients _lowPassA;
    private readonly BiquadCoefficients _lowPassB;
    private readonly float[] _history;
    private readonly float[] _frame;
    private readonly Fft _fft;
    private readonly float[] _windowRe;
    private readonly float[] _windowIm;
    private readonly float[] _frameRe;
    private readonly float[] _frameIm;
    private readonly double[] _energy;
    private readonly double[] _difference;
    private readonly double[] _raw;
    private readonly double[] _recent = new double[3];
    private double _a1, _a2, _b1, _b2;
    private int _historyCount;
    private int _historyWrite;
    private int _sinceAnalysis;
    private int _phase;
    private int _recentCount;

    /// <param name="sampleRate">Rate of the samples passed to <see cref="Process"/>.</param>
    /// <param name="minFrequency">Lowest pitch to look for, in Hz.</param>
    /// <param name="maxFrequency">Highest pitch to look for, in Hz.</param>
    /// <param name="gateDb">Frames quieter than this (RMS, dBFS) are unvoiced.</param>
    /// <param name="windowSeconds">Length of the analysed window. Longer is steadier, shorter reacts faster.</param>
    /// <param name="hopSeconds">Time between estimates.</param>
    public PitchDetector(
        int sampleRate,
        double minFrequency = 60,
        double maxFrequency = 1100,
        double gateDb = -50,
        double windowSeconds = 0.032,
        double hopSeconds = 0.01)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 4000);
        if (!(minFrequency > 0 && maxFrequency > minFrequency))
        {
            throw new ArgumentException("The frequency range is invalid.");
        }

        _decimation = Math.Max(1, (int)Math.Round(sampleRate / AnalysisRate));
        _rate = (double)sampleRate / _decimation;
        _window = (int)Math.Round(windowSeconds * _rate);
        _minLag = Math.Max(2, (int)Math.Floor(_rate / maxFrequency));
        _maxLag = (int)Math.Ceiling(_rate / minFrequency);
        _hop = Math.Max(1, (int)Math.Round(hopSeconds * _rate));
        HopSeconds = _hop / _rate;
        _gate = Math.Pow(10, gateDb / 20);

        // 4th order Butterworth low-pass well below the decimated Nyquist frequency.
        double cutoff = 0.4 * _rate;
        _lowPassA = BiquadCoefficients.Design(BiquadType.LowPass, sampleRate, cutoff, 0.5412);
        _lowPassB = BiquadCoefficients.Design(BiquadType.LowPass, sampleRate, cutoff, 1.3066);

        int span = _window + _maxLag + 1;
        _history = new float[span];
        _frame = new float[span];
        int size = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)span);
        _fft = new Fft(size);
        _windowRe = new float[size];
        _windowIm = new float[size];
        _frameRe = new float[size];
        _frameIm = new float[size];
        _energy = new double[span + 1];
        _difference = new double[_maxLag + 2];
        _raw = new double[_maxLag + 2];
    }

    /// <summary>Time between estimates.</summary>
    public double HopSeconds { get; }

    /// <summary>Latest pitch in Hz, or 0 when the last frame was unvoiced.</summary>
    public double Frequency { get; private set; }

    public bool IsVoiced => Frequency > 0;

    /// <summary>How periodic the last frame was, from 0 (noise) to 1 (perfectly periodic).</summary>
    public double Confidence { get; private set; }

    public void Reset()
    {
        _a1 = _a2 = _b1 = _b2 = 0;
        Array.Clear(_history);
        _historyCount = 0;
        _historyWrite = 0;
        _sinceAnalysis = 0;
        _phase = 0;
        _recentCount = 0;
        Frequency = 0;
        Confidence = 0;
    }

    /// <summary>Feeds mono samples. Returns <c>true</c> when a new estimate was produced.</summary>
    public bool Process(ReadOnlySpan<float> mono)
    {
        bool updated = false;
        foreach (float input in mono)
        {
            float sample = input;
            if (_decimation > 1)
            {
                sample = LowPass(sample);
                if (++_phase < _decimation)
                {
                    continue;
                }

                _phase = 0;
            }

            _history[_historyWrite] = sample;
            _historyWrite = (_historyWrite + 1) % _history.Length;
            _historyCount = Math.Min(_historyCount + 1, _history.Length);
            if (++_sinceAnalysis >= _hop && _historyCount == _history.Length)
            {
                _sinceAnalysis = 0;
                Analyse();
                updated = true;
            }
        }

        return updated;
    }

    /// <summary>Two cascaded biquads (transposed direct form II), inlined for speed.</summary>
    private float LowPass(float x)
    {
        var a = _lowPassA;
        double y = a.B0 * x + _a1;
        _a1 = a.B1 * x - a.A1 * y + _a2;
        _a2 = a.B2 * x - a.A2 * y;

        var b = _lowPassB;
        double z = b.B0 * y + _b1;
        _b1 = b.B1 * y - b.A1 * z + _b2;
        _b2 = b.B2 * y - b.A2 * z;
        return (float)z;
    }

    private void Analyse()
    {
        // Unroll the circular history, oldest sample first.
        int span = _history.Length;
        int oldest = _historyWrite;
        Array.Copy(_history, oldest, _frame, 0, span - oldest);
        Array.Copy(_history, 0, _frame, span - oldest, oldest);

        // Prefix sums of squares give every window's energy in O(1).
        _energy[0] = 0;
        for (int i = 0; i < span; i++)
        {
            _energy[i + 1] = _energy[i] + (double)_frame[i] * _frame[i];
        }

        double windowEnergy = _energy[_window];
        if (Math.Sqrt(windowEnergy / _window) < _gate)
        {
            Publish(0, 0);
            return;
        }

        // Cross-correlation of the first window with the whole frame: r(τ) = Σ x[j]·x[j+τ].
        Array.Clear(_windowRe);
        Array.Clear(_windowIm);
        Array.Clear(_frameRe);
        Array.Clear(_frameIm);
        _frame.AsSpan(0, _window).CopyTo(_windowRe);
        _frame.AsSpan().CopyTo(_frameRe);
        _fft.Forward(_windowRe, _windowIm);
        _fft.Forward(_frameRe, _frameIm);
        for (int k = 0; k < _fft.Size; k++)
        {
            // conj(W) · F
            float re = _windowRe[k] * _frameRe[k] + _windowIm[k] * _frameIm[k];
            float im = _windowRe[k] * _frameIm[k] - _windowIm[k] * _frameRe[k];
            _frameRe[k] = re;
            _frameIm[k] = im;
        }

        _fft.Inverse(_frameRe, _frameIm);
        double scale = 1.0 / _fft.Size;

        // YIN difference d(τ) = Σ (x[j] − x[j+τ])² and its cumulative mean normalised form d'(τ).
        _difference[0] = 1;
        double runningSum = 0;
        for (int lag = 1; lag <= _maxLag; lag++)
        {
            double shifted = _energy[lag + _window] - _energy[lag];
            double d = Math.Max(0, windowEnergy + shifted - 2 * _frameRe[lag] * scale);
            _raw[lag] = d;
            runningSum += d;
            _difference[lag] = runningSum > 0 ? d * lag / runningSum : 1;
        }

        int best = -1;
        for (int lag = _minLag; lag < _maxLag; lag++)
        {
            if (_difference[lag] < DipThreshold)
            {
                while (lag + 1 < _maxLag && _difference[lag + 1] < _difference[lag])
                {
                    lag++;
                }

                best = lag;
                break;
            }
        }

        if (best < 0)
        {
            // No clear dip: fall back to the global minimum, which may still be voiced.
            best = _minLag;
            for (int lag = _minLag + 1; lag < _maxLag; lag++)
            {
                if (_difference[lag] < _difference[best])
                {
                    best = lag;
                }
            }
        }

        double value = _difference[best];
        if (value > VoicedThreshold || best <= _minLag || best >= _maxLag - 1)
        {
            Publish(0, Math.Max(0, 1 - value));
            return;
        }

        // Parabolic interpolation of the raw difference around the dip for sub-sample precision.
        double left = _raw[best - 1];
        double centre = _raw[best];
        double right = _raw[best + 1];
        double denominator = left - 2 * centre + right;
        double offset = Math.Abs(denominator) > 1e-12 ? 0.5 * (left - right) / denominator : 0;
        double period = best + Math.Clamp(offset, -1, 1);
        Publish(_rate / period, 1 - value);
    }

    private void Publish(double frequency, double confidence)
    {
        Confidence = confidence;
        if (frequency <= 0)
        {
            _recentCount = 0;
            Frequency = 0;
            return;
        }

        // Median of the last three voiced estimates removes isolated octave errors.
        _recent[0] = _recent[1];
        _recent[1] = _recent[2];
        _recent[2] = frequency;
        _recentCount = Math.Min(3, _recentCount + 1);
        if (_recentCount < 3)
        {
            // At the start of a note the window still contains part of what came before; only
            // report a pitch once two estimates agree to within a semitone.
            bool settled = _recentCount == 2 && Math.Abs(Math.Log2(_recent[2] / _recent[1])) < 1.0 / 12;
            Frequency = settled ? frequency : 0;
            return;
        }

        double a = _recent[0], b = _recent[1], c = _recent[2];
        Frequency = Math.Max(Math.Min(a, b), Math.Min(Math.Max(a, b), c));
    }
}
