using VoiceChanger.Core.Dsp;

namespace VoiceChanger.Core.Audio;

/// <summary>
/// Hands audio from a producer thread to a consumer thread that run on independent clocks (two
/// sound cards never run at exactly the same speed), converting the sample rate on the way.
/// <para>
/// Producer (<see cref="Write"/>): sample rate conversion → lock-free ring buffer. Consumer
/// (<see cref="Read"/>): ring buffer → fade-in after a dropout. The amount of audio waiting in the
/// ring buffer is the delay the bridge adds. Because audio arrives in blocks, the raw fill level
/// jumps around and hides slow clock drift, so the consumer estimates a continuous "virtual" fill:
/// the frames in the buffer plus the frames the producer has generated since its last write
/// (derived from timestamps). A proportional controller steers the resampler ratio by at most
/// 0.2 % so this level converges to one producer block plus one consumer block plus the safety
/// margin. That keeps the delay low and constant and compensates the drift without the periodic
/// dropouts a fixed buffer would cause.
/// </para>
/// </summary>
public sealed class ClockBridge
{
    private const int MaxChunkFrames = 1024;
    private const int FadeInFrames = 256;
    private const double ControlWindowSeconds = 0.1;
    private const double ControlTimeConstantSeconds = 2.0;
    private const double FillSmoothingSeconds = 0.5;
    private const double MaxCorrection = 0.002;
    private const double RingBufferSeconds = 1.0;
    private const double HardSkipSeconds = 0.06;
    private const double MaxProducerGapSeconds = 0.1;

    private readonly int _channels;
    private readonly int _destinationRate;
    private readonly Resampler _resampler;
    private readonly FloatRingBuffer _ring;
    private readonly float[] _resampleBuffer;
    private readonly int _safetyFrames;
    private readonly int _controlWindowFrames;
    private readonly TimeProvider _time;

    // Consumer state.
    private bool _primed;
    private int _fadeInPosition = FadeInFrames;
    private int _controlElapsedFrames;
    private double _consumerBlockFrames;
    private double _virtualFill = -1;
    private double _averageFill;

    // Shared state.
    private long _stepAdjustmentBits = BitConverter.DoubleToInt64Bits(1.0);
    private long _lastWriteTimestamp;
    private int _producerBlockFrames;
    private int _underruns;
    private int _overflows;
    private float _averageFillFrames;

    public ClockBridge(int sourceRate, int destinationRate, int channels, int safetyMilliseconds = 5, TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sourceRate, 1000);
        ArgumentOutOfRangeException.ThrowIfLessThan(destinationRate, 1000);
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);

        _channels = channels;
        _destinationRate = destinationRate;
        SourceRate = sourceRate;
        _resampler = new Resampler(sourceRate, destinationRate, channels);
        _ring = new FloatRingBuffer((int)(destinationRate * RingBufferSeconds) * channels);
        _resampleBuffer = new float[_resampler.MaxOutputFrames(MaxChunkFrames) * channels];
        _safetyFrames = Math.Max(0, safetyMilliseconds) * destinationRate / 1000;
        _controlWindowFrames = (int)(destinationRate * ControlWindowSeconds);
        _time = timeProvider ?? TimeProvider.System;
    }

    public int Channels => _channels;

    public int SourceRate { get; }

    public int DestinationRate => _destinationRate;

    /// <summary>Number of times the consumer ran dry and got silence.</summary>
    public int Underruns => Volatile.Read(ref _underruns);

    /// <summary>Number of writes (partially) dropped because the buffer was full.</summary>
    public int Overflows => Volatile.Read(ref _overflows);

    /// <summary>Average amount of audio waiting in the bridge.</summary>
    public double BufferedMilliseconds => Volatile.Read(ref _averageFillFrames) * 1000.0 / _destinationRate;

    /// <summary>Current clock drift compensation in parts per million.</summary>
    public double DriftCorrectionPpm => (StepAdjustment - 1) * 1e6;

    /// <summary>Fixed delay of the sample rate converter.</summary>
    public double ResamplerLatencyMilliseconds => _resampler.LatencyFrames * 1000.0 / SourceRate;

    private double StepAdjustment => BitConverter.Int64BitsToDouble(Volatile.Read(ref _stepAdjustmentBits));

    /// <summary>Producer side: queues interleaved samples at the source rate.</summary>
    public void Write(ReadOnlySpan<float> interleaved)
    {
        int frames = interleaved.Length / _channels;
        if (frames == 0)
        {
            return;
        }

        _resampler.StepAdjustment = StepAdjustment;
        int queued = 0;
        for (int offset = 0; offset < frames; offset += MaxChunkFrames)
        {
            int count = Math.Min(MaxChunkFrames, frames - offset);
            int produced = _resampler.Process(interleaved.Slice(offset * _channels, count * _channels), _resampleBuffer) * _channels;
            int space = _ring.Free / _channels * _channels;
            int written = _ring.Write(_resampleBuffer.AsSpan(0, Math.Min(produced, space)));
            if (written < produced)
            {
                Interlocked.Increment(ref _overflows);
            }

            queued += written / _channels;
        }

        Volatile.Write(ref _producerBlockFrames, queued);
        Volatile.Write(ref _lastWriteTimestamp, _time.GetTimestamp());
    }

    /// <summary>Consumer side: fills the whole buffer at the destination rate (with silence when there is nothing).</summary>
    public void Read(Span<float> interleaved)
    {
        int frames = interleaved.Length / _channels;
        if (frames == 0)
        {
            return;
        }

        var block = interleaved[..(frames * _channels)];
        int available = _ring.Count / _channels;
        _averageFill += (available - _averageFill) * 0.05;
        Volatile.Write(ref _averageFillFrames, (float)_averageFill);

        // Largest recent block sizes, decaying slowly so a single big callback is forgotten.
        _consumerBlockFrames = Math.Max(frames, _consumerBlockFrames * 0.995);
        double target = _consumerBlockFrames + Volatile.Read(ref _producerBlockFrames) + _safetyFrames;

        if (!_primed)
        {
            // Wait until there is enough audio for one producer block, this read and the safety margin.
            if (available < target)
            {
                block.Clear();
                return;
            }

            _ring.Skip((available - (int)target) * _channels);
            available = (int)target;
            _primed = true;
            _fadeInPosition = 0;
            _virtualFill = -1;
            _controlElapsedFrames = 0;
        }

        UpdateDriftControl(available, frames, target);

        int read = _ring.Read(block[..(Math.Min(available, frames) * _channels)]) / _channels;
        if (read < frames)
        {
            block[(read * _channels)..].Clear();
            _primed = false;
            Interlocked.Increment(ref _underruns);
        }

        ApplyFadeIn(block, read);
    }

    private void UpdateDriftControl(int available, int frames, double target)
    {
        // Frames produced since the last write have not reached the buffer yet, but they exist:
        // counting them turns the saw-tooth fill level into a smooth, drift revealing signal.
        double elapsed = (double)(_time.GetTimestamp() - Volatile.Read(ref _lastWriteTimestamp)) / _time.TimestampFrequency;
        double pending = Math.Clamp(elapsed, 0, MaxProducerGapSeconds) * _destinationRate;
        double fill = available + pending;

        double smoothing = Math.Min(1.0, frames / (FillSmoothingSeconds * _destinationRate));
        _virtualFill = _virtualFill < 0 ? fill : _virtualFill + (fill - _virtualFill) * smoothing;

        _controlElapsedFrames += frames;
        if (_controlElapsedFrames < _controlWindowFrames)
        {
            return;
        }

        _controlElapsedFrames = 0;
        double excess = _virtualFill - target;
        if (excess > HardSkipSeconds * _destinationRate && available - frames > excess)
        {
            // Far too much audio queued up (e.g. after a hiccup): drop it instead of slowly draining.
            _ring.Skip((int)excess * _channels);
            _fadeInPosition = 0;
            _virtualFill -= (int)excess;
            excess = 0;
        }

        // Proportional controller: drain (or refill) the excess over the time constant.
        double correction = Math.Clamp(excess / (ControlTimeConstantSeconds * _destinationRate), -MaxCorrection, MaxCorrection);
        Volatile.Write(ref _stepAdjustmentBits, BitConverter.DoubleToInt64Bits(1 + correction));
    }

    private void ApplyFadeIn(Span<float> block, int frames)
    {
        if (_fadeInPosition >= FadeInFrames)
        {
            return;
        }

        for (int frame = 0; frame < frames && _fadeInPosition < FadeInFrames; frame++, _fadeInPosition++)
        {
            float gain = (float)_fadeInPosition / FadeInFrames;
            for (int ch = 0; ch < _channels; ch++)
            {
                block[frame * _channels + ch] *= gain;
            }
        }
    }
}
