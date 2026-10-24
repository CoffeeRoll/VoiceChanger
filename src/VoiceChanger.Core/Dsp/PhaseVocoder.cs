namespace VoiceChanger.Core.Dsp;

/// <summary>
/// Single channel frequency-domain pitch shifter: a phase-locked phase vocoder time-stretch followed
/// by resampling.
/// <para>
/// Every analysis frame (hop <c>Ha</c>) is re-synthesised with hop <c>Hs = Ha · ratio</c>, which
/// stretches the sound in time without changing its pitch. Phases are propagated with identity phase
/// locking (J. Laroche and M. Dolson, 1999): only spectral peaks get a new phase, the bins around a
/// peak keep their phase relationship to it, which avoids most of the "phasiness" of plain vocoders.
/// The stretched signal is then resampled by <c>Ha / Hs</c>, bringing the duration back and moving
/// every frequency by exactly the ratio. Weighted overlap-add keeps the level constant for any ratio.
/// </para>
/// </summary>
public sealed class PhaseVocoder
{
    private const int Oversampling = 8;
    private const int QueueMargin = 32;

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

    private Resampler _resampler;
    private int _synthesisHop;
    private int _rover;
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

        _synthesisHop = _hop;
        _resampler = new Resampler(_hop, _hop, 1);
        Reset();
    }

    public int FrameSize => _size;

    /// <summary>Output frequency divided by input frequency (0.25 to 4).</summary>
    public double Ratio
    {
        get => _ratio;
        set => _ratio = Math.Clamp(value, 0.25, 4.0);
    }

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
        int synthesisHop = Math.Clamp((int)Math.Round(_hop * _ratio), 1, _size / 2);
        if (synthesisHop != _synthesisHop)
        {
            // Resample from the stretched rate back to the original one: Hs samples in, Ha out.
            _synthesisHop = synthesisHop;
            _resampler = new Resampler(synthesisHop, _hop, 1);
        }

        for (int k = 0; k < _size; k++)
        {
            _real[k] = _inFifo[k] * _window[k];
            _imag[k] = 0;
        }

        _fft.Forward(_real, _imag);
        Analyse();
        Synthesise(FindPeaks());
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

        for (int k = 0; k <= _half; k++)
        {
            double phase = _synthesisPhase[k];
            _real[k] = (float)(_magnitude[k] * Math.Cos(phase));
            _imag[k] = (float)(_magnitude[k] * Math.Sin(phase));
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

    private static double Wrap(double phase) => Math.IEEERemainder(phase, 2 * Math.PI);
}
