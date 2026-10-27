namespace VoiceChanger.Core.Dsp;

/// <summary>
/// Linear prediction (LPC) of a short stretch of audio: the all-pole filter that best predicts each
/// sample from the ones before it. For a voice that filter is the vocal tract, its resonances are
/// the formants, and what it cannot predict (the residual) is the glottal excitation.
/// <para>
/// Autocorrelation method with a Hann window, a Gaussian lag window and a little white noise
/// correction (which keep the filter stable and its peaks sensible), solved with Levinson-Durbin.
/// The audio is meant to be pre-emphasised first (<see cref="PreEmphasis"/>) so that the poles go
/// to the formants rather than to the voice's falling slope. The filter comes out as reflection
/// coefficients, for a lattice: those can be blended from one analysis to the next and the
/// filter stays stable.
/// </para>
/// <para>
/// <see cref="Warp"/> turns the latest analysis into the vocal tract with every resonance moved
/// by a factor, by stretching its spectrum and fitting a filter to that.
/// </para>
/// </summary>
internal sealed class LinearPredictor
{
    private const double LagBandwidthHz = 150;
    private const double WhiteNoiseCorrection = 1.0001;
    private const double PreEmphasisHz = 80;

    private readonly int _order;
    private readonly double[] _window;
    private readonly double[] _lagWindow;
    private readonly double[] _frame;
    private readonly double[] _autocorrelation;
    private readonly double[] _predictor;
    private readonly double[] _warped;
    private readonly double[] _scratch;
    private readonly Fft _fft;
    private readonly float[] _real;
    private readonly float[] _imag;
    private readonly double[] _spectrum;
    private double _error;

    /// <param name="order">Number of poles of the vocal tract.</param>
    /// <param name="windowLength">Length of the analysed stretch, in samples.</param>
    /// <param name="sampleRate">Sample rate, in Hz.</param>
    public LinearPredictor(int order, int windowLength, int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(order, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(windowLength, order + 1);
        _order = order;
        PreEmphasis = Math.Exp(-2 * Math.PI * PreEmphasisHz / sampleRate);
        _window = new double[windowLength];
        for (int i = 0; i < windowLength; i++)
        {
            _window[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * (i + 0.5) / windowLength);
        }

        _lagWindow = new double[order + 1];
        for (int k = 0; k <= order; k++)
        {
            double x = 2 * Math.PI * LagBandwidthHz * k / sampleRate;
            _lagWindow[k] = Math.Exp(-0.5 * x * x);
        }

        _frame = new double[windowLength];
        _autocorrelation = new double[order + 1];
        _predictor = new double[order];
        _warped = new double[order];
        _scratch = new double[order];

        // About 100 Hz per bin: the lag window already smooths the vocal tract more than that.
        int size = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Max(sampleRate / 160, 4 * (order + 1)));
        _fft = new Fft(size);
        _real = new float[size];
        _imag = new float[size];
        _spectrum = new double[size / 2 + 1];
    }

    /// <summary>Number of poles, and of reflection coefficients.</summary>
    public int Order => _order;

    /// <summary>
    /// The pre-emphasis to apply before analysing (and before the inverse filter): y[n] = x[n] − μ·x[n−1].
    /// </summary>
    public double PreEmphasis { get; }

    /// <summary>The pre-emphasised stretch to analyse; fill it, then call <see cref="Analyse"/>.</summary>
    public Span<double> Frame => _frame;

    /// <summary>
    /// Fits the vocal tract to <see cref="Frame"/>, as reflection coefficients. Returns the prediction
    /// error energy (0 for silence, in which case the coefficients are 0: no filter at all).
    /// </summary>
    public double Analyse(Span<double> reflection)
    {
        int length = _frame.Length;
        for (int i = 0; i < length; i++)
        {
            _frame[i] *= _window[i];
        }

        for (int k = 0; k <= _order; k++)
        {
            double sum = 0;
            for (int i = k; i < length; i++)
            {
                sum += _frame[i] * _frame[i - k];
            }

            _autocorrelation[k] = sum * _lagWindow[k];
        }

        _error = Solve(_autocorrelation, _predictor, reflection);
        return _error;
    }

    /// <summary>
    /// The vocal tract of the latest <see cref="Analyse"/> with every resonance moved by
    /// <paramref name="ratio"/> (above 1 = up), as reflection coefficients. Returns the gain that
    /// gives the residual of that analysis the level of the moved vocal tract.
    /// </summary>
    public double Warp(double ratio, Span<double> reflection)
    {
        if (_error <= 0)
        {
            reflection[.._order].Clear();
            return 1;
        }

        // Power spectrum of the vocal tract: error / |A|².
        int size = _fft.Size;
        Array.Clear(_real);
        Array.Clear(_imag);
        _real[0] = 1;
        for (int k = 0; k < _order; k++)
        {
            _real[k + 1] = (float)_predictor[k];
        }

        _fft.Forward(_real, _imag);
        int half = size / 2;
        for (int k = 0; k <= half; k++)
        {
            _spectrum[k] = _error / Math.Max((double)_real[k] * _real[k] + (double)_imag[k] * _imag[k], 1e-12);
        }

        // Stretched by the ratio (what lies beyond the top continues as it ends), then back to an
        // autocorrelation to fit the moved vocal tract to. It is the voice that is stretched, not
        // the pre-emphasis: that comes off before and goes back on after.
        for (int k = 0; k <= half; k++)
        {
            double source = Math.Min(k / ratio, half);
            int below = Math.Min((int)source, half - 1);
            double fraction = source - below;
            double value = _spectrum[below] + fraction * (_spectrum[below + 1] - _spectrum[below]);
            value *= Emphasis(Math.PI * k / half) / Emphasis(Math.PI * source / half);

            _real[k] = (float)value;
            if (k > 0 && k < half)
            {
                _real[size - k] = (float)value;
            }
        }

        Array.Clear(_imag);
        _fft.Inverse(_real, _imag);
        for (int k = 0; k <= _order; k++)
        {
            _autocorrelation[k] = _real[k] / size;
        }

        double error = Solve(_autocorrelation, _warped, reflection);
        return error > 0 ? Math.Sqrt(error / _error) : 1;
    }

    /// <summary>Power gain of the pre-emphasis at a frequency (radians per sample).</summary>
    private double Emphasis(double frequency) => 1 - 2 * PreEmphasis * Math.Cos(frequency) + PreEmphasis * PreEmphasis;

    /// <summary>
    /// Levinson-Durbin: the predictor for an autocorrelation (A(z) = 1 + Σ a[k]·z^−(k+1)), its
    /// reflection coefficients and its error energy.
    /// </summary>
    private double Solve(double[] autocorrelation, double[] a, Span<double> reflection)
    {
        Array.Clear(a);
        reflection[.._order].Clear();
        double error = autocorrelation[0] * WhiteNoiseCorrection;
        if (error < 1e-12)
        {
            return 0;
        }

        for (int i = 0; i < _order; i++)
        {
            double acc = autocorrelation[i + 1];
            for (int j = 0; j < i; j++)
            {
                acc += a[j] * autocorrelation[i - j];
            }

            double k = -acc / error;
            double remaining = error * (1 - k * k);
            if (remaining <= 0)
            {
                // Numerically singular (a pure tone): keep what was found so far.
                break;
            }

            for (int j = 0; j < i; j++)
            {
                _scratch[j] = a[j] + k * a[i - 1 - j];
            }

            Array.Copy(_scratch, a, i);
            a[i] = k;
            reflection[i] = k;
            error = remaining;
        }

        return error;
    }
}
