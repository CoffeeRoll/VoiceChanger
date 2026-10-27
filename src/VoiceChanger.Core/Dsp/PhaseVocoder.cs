namespace VoiceChanger.Core.Dsp;

/// <summary>
/// Single channel frequency-domain pitch shifter: a phase-locked phase vocoder time-stretch followed
/// by resampling.
/// <para>
/// Every analysis frame (hop <c>Ha</c>) is re-synthesised with hop <c>Hs ≈ Ha · ratio</c>, which
/// stretches the sound in time without changing its pitch. Phases are propagated with identity phase
/// locking (J. Laroche and M. Dolson, 1999): only spectral peaks get a new phase, the bins around a
/// peak keep their phase relationship to it, which avoids most of the "phasiness" of plain vocoders.
/// The stretched signal is then resampled by exactly the ratio, bringing the duration back and moving
/// every frequency by the ratio; hops are whole samples, so they carry their rounding over from frame
/// to frame. The ratio can change with every frame. When raising the pitch, what would end up above
/// the Nyquist frequency is removed from the spectrum first. Weighted overlap-add keeps the level
/// constant for any ratio.
/// </para>
/// <para>
/// Resampling moves the spectral envelope (the formants that give a voice its character) together
/// with the pitch, which is what makes a voice an octave up sound like a chipmunk. With
/// <see cref="PreserveFormants"/> each frame's envelope is estimated by cepstral smoothing and the
/// magnitudes are re-weighted before resampling so that, afterwards, the envelope is back where it
/// was (or moved by <see cref="FormantRatio"/>), while the harmonics carry the new pitch. The envelope
/// is a "true envelope" (A. Röbel and X. Rodet, 2005): cepstral smoothing repeated until the curve
/// rests on the harmonic peaks rather than averaging them with the valleys between them, computed
/// on a peak-held, decimated copy of the spectrum to keep it cheap.
/// </para>
/// </summary>
public sealed class PhaseVocoder
{
    private const int Oversampling = 8;
    private const int QueueMargin = 32;
    private const double DriftCorrection = 0.1;
    private const double AliasFraction = 0.95;
    private const int AliasTaper = 8;

    // Envelope smoothing keeps quefrencies below ~1.2 ms: enough detail for formants while ignoring
    // the harmonic ripple of voices up to roughly 800 Hz.
    private const double LifterSeconds = 0.0012;
    private const double EnvelopeBinHz = 90;
    // A few true-envelope iterations are enough (and, measured on synthetic vowels, more accurate
    // than full convergence, which over-fits the peaks). Formants move slowly, so the envelope is
    // refreshed every other frame.
    private const int EnvelopeIterations = 4;
    private const int EnvelopeInterval = 2;
    private const float EnvelopeTolerance = 0.23f; // natural log units, about 2 dB
    private const float MaxBoost = 8f;        // +18 dB
    private const float MaxCut = 1f / 64;     // −36 dB

    private readonly int _size;
    private readonly int _half;
    private readonly int _hop;
    private readonly int _latency;
    private readonly Fft _fft;
    private readonly float[] _window;
    private readonly float[] _inFifo;
    private readonly float[] _real;
    private readonly float[] _imag;
    private readonly float[] _magnitude;
    private readonly double[] _analysisPhase;
    private readonly double[] _lastAnalysisPhase;
    private readonly double[] _synthesisPhase;
    private readonly double[] _omega;
    private readonly int[] _peaks;
    private readonly float[] _signalSum;
    private readonly float[] _weightSum;
    private readonly float[] _stretched;
    private readonly float[] _resampled;
    private readonly float[] _queue;
    private readonly Fft _envelopeFft;
    private readonly int _decimation;
    private readonly float[] _coarseLog;
    private readonly float[] _coarseFit;
    private readonly float[] _cepstrumRe;
    private readonly float[] _cepstrumIm;
    private readonly float[] _envelope;
    private readonly float[] _correction;
    private readonly int _lifter;

    private readonly Resampler _resampler;
    private int _synthesisHop;
    private double _frameRatio = 1;
    private double _stretchDebt;
    private int _rover;
    private int _envelopeCountdown;
    private int _queueStart;
    private int _queueCount;
    private double _ratio = 1;

    public PhaseVocoder(int frameSize, int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(frameSize, 64);
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1000);

        _fft = new Fft(frameSize);
        _size = frameSize;
        _half = frameSize / 2;
        _hop = frameSize / Oversampling;
        _latency = frameSize - _hop;

        _window = new float[frameSize];
        for (int k = 0; k < frameSize; k++)
        {
            _window[k] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * k / frameSize));
        }

        _inFifo = new float[frameSize];
        _real = new float[frameSize];
        _imag = new float[frameSize];
        _magnitude = new float[_half + 1];
        _analysisPhase = new double[_half + 1];
        _lastAnalysisPhase = new double[_half + 1];
        _synthesisPhase = new double[_half + 1];
        _omega = new double[_half + 1];
        _peaks = new int[_half + 1];

        // Synthesis hops are at most half a frame (ratio 4 with 8x overlap).
        _signalSum = new float[frameSize * 2];
        _weightSum = new float[frameSize * 2];
        _stretched = new float[frameSize];
        _resampled = new float[frameSize * 4];
        _queue = new float[frameSize * 4];

        // The envelope is estimated on bins of roughly EnvelopeBinHz (a power-of-two decimation).
        double binHz = (double)sampleRate / frameSize;
        _decimation = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Max(1, (int)Math.Round(EnvelopeBinHz / binHz)));
        _decimation = Math.Min(_decimation, _half / 32);
        int coarseBins = _half / _decimation;
        _envelopeFft = new Fft(coarseBins * 2);
        _coarseLog = new float[coarseBins + 1];
        _coarseFit = new float[coarseBins + 1];
        _cepstrumRe = new float[coarseBins * 2];
        _cepstrumIm = new float[coarseBins * 2];
        _envelope = new float[_half + 1];
        _correction = new float[_half + 1];
        // Decimating the spectrum shortens the cepstrum but keeps its time scale (1 / sampleRate per
        // quefrency step), so the cut-off in steps is the same as for the full spectrum.
        _lifter = Math.Clamp((int)Math.Round(LifterSeconds * sampleRate), 4, coarseBins / 2);

        // Full band: the spectrum is already band-limited when the pitch goes up.
        _resampler = new Resampler(1, 1, 1);
        Reset();
    }

    public int FrameSize => _size;

    /// <summary>Samples still to come before the next frame is processed (the last one triggers it).</summary>
    public int SamplesUntilFrame => _size - _rover;

    /// <summary>Output frequency divided by input frequency (0.25 to 4).</summary>
    public double Ratio
    {
        get => _ratio;
        set => _ratio = Math.Clamp(value, 0.25, 4.0);
    }

    /// <summary>Keep the spectral envelope (formants) in place instead of shifting it with the pitch.</summary>
    public bool PreserveFormants { get; set; }

    /// <summary>
    /// With <see cref="PreserveFormants"/>: how far to move the formants, as a frequency ratio
    /// (1 = keep them where they are, above 1 = smaller/brighter voice, below 1 = bigger/darker voice).
    /// </summary>
    public double FormantRatio { get; set; } = 1;

    public int LatencyFrames => LatencyFor(_size);

    /// <summary>Delay of a vocoder with the given frame size, in frames.</summary>
    public static int LatencyFor(int frameSize) => frameSize - frameSize / Oversampling + QueueMargin + 16;

    public void Reset()
    {
        Array.Clear(_inFifo);
        Array.Clear(_lastAnalysisPhase);
        Array.Clear(_synthesisPhase);
        Array.Clear(_signalSum);
        Array.Clear(_weightSum);
        _resampler.Reset();
        _rover = _latency;
        _envelopeCountdown = 0;
        _synthesisHop = _hop;
        _stretchDebt = 0;

        // Prime the output queue with the samples played before the first frame is ready.
        Array.Clear(_queue);
        _queueStart = 0;
        _queueCount = _hop + QueueMargin;
    }

    /// <summary>Shifts one channel of an interleaved buffer in place.</summary>
    public void Process(Span<float> interleaved, int channel, int channels)
    {
        for (int i = channel; i < interleaved.Length; i += channels)
        {
            _inFifo[_rover++] = interleaved[i];
            interleaved[i] = Dequeue();

            if (_rover >= _size)
            {
                _rover = _latency;
                ProcessFrame();
                Array.Copy(_inFifo, _hop, _inFifo, 0, _latency);
            }
        }
    }

    private float Dequeue()
    {
        if (_queueCount == 0)
        {
            return 0;
        }

        float value = _queue[_queueStart];
        _queueStart = (_queueStart + 1) % _queue.Length;
        _queueCount--;
        return value;
    }

    private void Enqueue(ReadOnlySpan<float> samples)
    {
        foreach (float sample in samples)
        {
            if (_queueCount == _queue.Length)
            {
                return;
            }

            _queue[(_queueStart + _queueCount) % _queue.Length] = sample;
            _queueCount++;
        }
    }

    private void ProcessFrame()
    {
        // Resampling by the ratio moves every frequency by exactly that much; the stretch supplies
        // the samples for it, Ha · ratio per frame on average. Should rounding ever pile up, the
        // output queue is nudged back to its usual fill.
        double ratio = _ratio;
        _frameRatio = ratio;
        _stretchDebt += (_hop - DriftCorrection * (_queueCount - QueueMargin)) * ratio;
        _synthesisHop = Math.Clamp((int)Math.Round(_stretchDebt), 1, _size / 2);
        _stretchDebt = Math.Clamp(_stretchDebt - _synthesisHop, -1, 1);
        _resampler.StepAdjustment = ratio;

        for (int k = 0; k < _size; k++)
        {
            _real[k] = _inFifo[k] * _window[k];
            _imag[k] = 0;
        }

        _fft.Forward(_real, _imag);
        Analyse();
        int peaks = FindPeaks();
        UpdateFormantCorrection();
        Synthesise(peaks);
        OverlapAdd();

        int produced = _resampler.Process(_stretched.AsSpan(0, _synthesisHop), _resampled);
        Enqueue(_resampled.AsSpan(0, produced));
    }

    /// <summary>Magnitude, phase and instantaneous frequency (radians per sample) of every bin.</summary>
    private void Analyse()
    {
        double expected = 2 * Math.PI * _hop / _size;
        for (int k = 0; k <= _half; k++)
        {
            double re = _real[k];
            double im = _imag[k];
            double phase = Math.Atan2(im, re);
            double deviation = Wrap(phase - _lastAnalysisPhase[k] - k * expected);

            _lastAnalysisPhase[k] = phase;
            _analysisPhase[k] = phase;
            _magnitude[k] = (float)Math.Sqrt(re * re + im * im);
            _omega[k] = (k * expected + deviation) / _hop;
        }
    }

    private int FindPeaks()
    {
        float loudest = 0;
        for (int k = 0; k <= _half; k++)
        {
            loudest = Math.Max(loudest, _magnitude[k]);
        }

        float floor = loudest * 1e-5f;
        int count = 0;
        for (int k = 1; k < _half; k++)
        {
            float m = _magnitude[k];
            if (m > floor && m > _magnitude[k - 1] && m >= _magnitude[k + 1])
            {
                _peaks[count++] = k;
            }
        }

        return count;
    }

    /// <summary>New phases: peaks advance by Hs at their own frequency, other bins stay locked to their peak.</summary>
    private void Synthesise(int peakCount)
    {
        double hop = _synthesisHop;
        if (peakCount == 0)
        {
            for (int k = 0; k <= _half; k++)
            {
                _synthesisPhase[k] = Wrap(_synthesisPhase[k] + hop * _omega[k]);
            }
        }

        for (int p = 0; p < peakCount; p++)
        {
            int peak = _peaks[p];
            int low = p == 0 ? 0 : (_peaks[p - 1] + peak) / 2 + 1;
            int high = p == peakCount - 1 ? _half : (peak + _peaks[p + 1]) / 2;

            double peakPhase = Wrap(_synthesisPhase[peak] + hop * _omega[peak]);
            double rotation = peakPhase - _analysisPhase[peak];
            for (int k = low; k <= high; k++)
            {
                _synthesisPhase[k] = Wrap(_analysisPhase[k] + rotation);
            }
        }

        // Raising the pitch moves the top of the spectrum past the Nyquist frequency: leave it out.
        int top = _frameRatio > 1 ? (int)(_half * AliasFraction / _frameRatio) : _half + AliasTaper;
        for (int k = 0; k <= _half; k++)
        {
            double phase = _synthesisPhase[k];
            double magnitude = _magnitude[k] * _correction[k];
            if (k > top - AliasTaper)
            {
                magnitude *= k >= top ? 0 : 0.5 + 0.5 * Math.Cos(Math.PI * (k - (top - AliasTaper)) / AliasTaper);
            }

            _real[k] = (float)(magnitude * Math.Cos(phase));
            _imag[k] = (float)(magnitude * Math.Sin(phase));
        }

        // Mirror into a Hermitian spectrum so the inverse transform is real.
        _imag[0] = 0;
        _imag[_half] = 0;
        for (int k = 1; k < _half; k++)
        {
            _real[_size - k] = _real[k];
            _imag[_size - k] = -_imag[k];
        }
    }

    /// <summary>Overlap-adds the frame at the synthesis hop and emits <c>Hs</c> stretched samples.</summary>
    private void OverlapAdd()
    {
        _fft.Inverse(_real, _imag);

        float scale = 1f / _size;
        for (int k = 0; k < _size; k++)
        {
            float w = _window[k];
            _signalSum[k] += _real[k] * scale * w;
            _weightSum[k] += w * w;
        }

        int hop = _synthesisHop;
        for (int k = 0; k < hop; k++)
        {
            _stretched[k] = _signalSum[k] / Math.Max(_weightSum[k], 1e-2f);
        }

        int remaining = _signalSum.Length - hop;
        Array.Copy(_signalSum, hop, _signalSum, 0, remaining);
        Array.Copy(_weightSum, hop, _weightSum, 0, remaining);
        Array.Clear(_signalSum, remaining, hop);
        Array.Clear(_weightSum, remaining, hop);
    }

    /// <summary>
    /// Per-bin gain that turns the frame's envelope E(f) into E(f · ratio / formantRatio). After the
    /// final resampling scales every frequency by the ratio, the envelope ends up at E(f / formantRatio).
    /// </summary>
    private void UpdateFormantCorrection()
    {
        if (!PreserveFormants)
        {
            Array.Fill(_correction, 1f);
            return;
        }

        if (_envelopeCountdown-- <= 0)
        {
            _envelopeCountdown = EnvelopeInterval - 1;
            EstimateEnvelope();
        }

        double warp = _frameRatio / FormantRatio;
        for (int k = 0; k <= _half; k++)
        {
            double source = Math.Min(k * warp, _half);
            int index = (int)source;
            float t = (float)(source - index);
            float target = index >= _half ? _envelope[_half] : _envelope[index] + t * (_envelope[index + 1] - _envelope[index]);
            float gain = target / Math.Max(_envelope[k], 1e-12f);
            _correction[k] = Math.Clamp(gain, MaxCut, MaxBoost);
        }
    }

    /// <summary>
    /// "True envelope" of the frame: the log spectrum is peak-held onto coarse bins, then smoothed
    /// cepstrally again and again, each time lifting the data to the smoothed curve wherever it lies
    /// below it, until the curve passes (within ~2 dB) over every peak.
    /// </summary>
    private void EstimateEnvelope()
    {
        float loudest = 0;
        for (int k = 0; k <= _half; k++)
        {
            loudest = Math.Max(loudest, _magnitude[k]);
        }

        float floor = MathF.Log(Math.Max(loudest * 1e-5f, 1e-12f));
        int coarseBins = _coarseLog.Length - 1;
        for (int c = 0; c <= coarseBins; c++)
        {
            int start = Math.Max(0, c * _decimation - _decimation / 2);
            int end = Math.Min(_half, c * _decimation + _decimation / 2);
            float peak = 0;
            for (int k = start; k <= end; k++)
            {
                peak = Math.Max(peak, _magnitude[k]);
            }

            _coarseLog[c] = Math.Max(MathF.Log(Math.Max(peak, 1e-12f)), floor);
            _coarseFit[c] = _coarseLog[c];
        }

        for (int iteration = 0; iteration < EnvelopeIterations; iteration++)
        {
            SmoothCepstrally(_coarseFit);

            float worst = 0;
            for (int c = 0; c <= coarseBins; c++)
            {
                float above = _coarseLog[c] - _coarseFit[c];
                if (above > 0)
                {
                    worst = Math.Max(worst, above);
                    _coarseFit[c] = _coarseLog[c];
                }
            }

            if (worst < EnvelopeTolerance)
            {
                break;
            }
        }

        // Back to full resolution, interpolating between coarse bin centres.
        for (int k = 0; k <= _half; k++)
        {
            float position = (float)k / _decimation;
            int index = Math.Min((int)position, coarseBins - 1);
            float t = position - index;
            _envelope[k] = MathF.Exp(_coarseFit[index] + t * (_coarseFit[index + 1] - _coarseFit[index]));
        }
    }

    /// <summary>Replaces a half spectrum (log magnitudes) by its low-quefrency part.</summary>
    private void SmoothCepstrally(float[] values)
    {
        int bins = values.Length - 1;
        int size = bins * 2;
        for (int c = 0; c <= bins; c++)
        {
            _cepstrumRe[c] = values[c];
            if (c > 0 && c < bins)
            {
                _cepstrumRe[size - c] = values[c];
            }
        }

        Array.Clear(_cepstrumIm);
        _envelopeFft.Forward(_cepstrumRe, _cepstrumIm);

        // Keep low quefrencies only, with a short raised-cosine taper to avoid ringing.
        int taper = Math.Max(2, _lifter / 4);
        for (int n = 0; n < size; n++)
        {
            int quefrency = Math.Min(n, size - n);
            float weight = quefrency <= _lifter - taper
                ? 1f
                : quefrency >= _lifter
                    ? 0f
                    : 0.5f + 0.5f * MathF.Cos(MathF.PI * (quefrency - (_lifter - taper)) / taper);
            _cepstrumRe[n] *= weight;
            _cepstrumIm[n] *= weight;
        }

        _envelopeFft.Inverse(_cepstrumRe, _cepstrumIm);
        float scale = 1f / size;
        for (int c = 0; c <= bins; c++)
        {
            values[c] = _cepstrumRe[c] * scale;
        }
    }

    private static double Wrap(double phase) => Math.IEEERemainder(phase, 2 * Math.PI);
}
