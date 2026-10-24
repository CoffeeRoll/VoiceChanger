using VoiceChanger.Core.Dsp;
using VoiceChanger.Core.Effects;

namespace VoiceChanger.Core.Audio;

/// <summary>
/// Moves audio from a capture stream to a render stream that run on independent clocks.
/// <para>
/// Capture thread (<see cref="ProcessInput"/>): channel conversion → input gain → effect chain →
/// sample rate conversion → lock-free ring buffer. Render thread (<see cref="RenderOutput"/>): ring
/// buffer → channel conversion → output gain.
/// </para>
/// <para>
/// The amount of audio waiting in the ring buffer is the main source of added delay. Because audio
/// arrives in blocks, the raw fill level jumps around and hides slow clock drift, so the render side
/// estimates a continuous "virtual" fill: the frames in the buffer plus the frames the input device
/// has captured since its last callback (derived from timestamps). A proportional controller steers
/// the resampler ratio by at most 0.2 % so this level converges to one input block plus one output
/// block plus <c>safetyMilliseconds</c>. That keeps the delay low and constant and compensates the
/// drift between two independent devices without the periodic dropouts a fixed buffer would cause.
/// </para>
/// </summary>
public sealed class AudioProcessor
{
    private const int MaxChunkFrames = 1024;
    private const int FadeInFrames = 256;
    private const double ControlWindowSeconds = 0.1;
    private const double ControlTimeConstantSeconds = 2.0;
    private const double FillSmoothingSeconds = 0.5;
    private const double MaxCorrection = 0.002;
    private const double RingBufferSeconds = 1.0;
    private const double HardSkipSeconds = 0.06;
    private const double MaxCaptureGapSeconds = 0.1;

    private readonly AudioFormat _input;
    private readonly AudioFormat _output;
    private readonly int _channels;
    private readonly EffectChain _chain;
    private readonly Resampler _resampler;
    private readonly FloatRingBuffer _ring;
    private readonly float[] _processBuffer;
    private readonly float[] _resampleBuffer;
    private readonly int _safetyFrames;
    private readonly int _controlWindowFrames;
    private readonly TimeProvider _time;
    private float[] _renderBuffer;

    // Render thread state.
    private bool _primed;
    private int _fadeInPosition = FadeInFrames;
    private int _controlElapsedFrames;
    private double _renderBlockFrames;
    private double _virtualFill = -1;
    private double _averageFill;

    // Shared state.
    private long _stepAdjustmentBits = BitConverter.DoubleToInt64Bits(1.0);
    private long _lastCaptureTimestamp;
    private int _captureBlockFrames;
    private float _inputGain = 1;
    private float _outputGain = 1;
    private int _underruns;
    private int _overflows;
    private float _averageFillFrames;

    public AudioProcessor(
        AudioFormat input,
        AudioFormat output,
        int processingChannels,
        EffectChain chain,
        int safetyMilliseconds = 5,
        TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(processingChannels, 1);
        ArgumentNullException.ThrowIfNull(chain);
        ValidateFormat(input, nameof(input));
        ValidateFormat(output, nameof(output));

        _input = input;
        _output = output;
        _channels = processingChannels;
        _chain = chain;
        _resampler = new Resampler(input.SampleRate, output.SampleRate, processingChannels);
        _ring = new FloatRingBuffer((int)(output.SampleRate * RingBufferSeconds) * processingChannels);
        _processBuffer = new float[MaxChunkFrames * processingChannels];
        _resampleBuffer = new float[_resampler.MaxOutputFrames(MaxChunkFrames) * processingChannels];
        _renderBuffer = new float[4096 * processingChannels];
        _safetyFrames = Math.Max(0, safetyMilliseconds) * output.SampleRate / 1000;
        _controlWindowFrames = (int)(output.SampleRate * ControlWindowSeconds);
        _time = timeProvider ?? TimeProvider.System;
    }

    public AudioFormat InputFormat => _input;

    public AudioFormat OutputFormat => _output;

    public int ProcessingChannels => _channels;

    public PeakMeter InputMeter { get; } = new();

    public PeakMeter OutputMeter { get; } = new();

    /// <summary>Linear gain applied before the effects.</summary>
    public float InputGain
    {
        get => Volatile.Read(ref _inputGain);
        set => Volatile.Write(ref _inputGain, value);
    }

    /// <summary>Linear gain applied after the effects.</summary>
    public float OutputGain
    {
        get => Volatile.Read(ref _outputGain);
        set => Volatile.Write(ref _outputGain, value);
    }

    /// <summary>Number of times the output ran dry and had to insert silence.</summary>
    public int Underruns => Volatile.Read(ref _underruns);

    /// <summary>Number of input blocks (partially) dropped because the buffer was full.</summary>
    public int Overflows => Volatile.Read(ref _overflows);

    /// <summary>Average amount of audio waiting between input and output.</summary>
    public double BufferedMilliseconds => Volatile.Read(ref _averageFillFrames) * 1000.0 / _output.SampleRate;

    /// <summary>Current clock drift compensation in parts per million.</summary>
    public double DriftCorrectionPpm => (StepAdjustment - 1) * 1e6;

    /// <summary>Fixed delay of the resampler and the active effects in milliseconds.</summary>
    public double ProcessingLatencyMilliseconds =>
        (_resampler.LatencyFrames + _chain.LatencyFrames) * 1000.0 / _input.SampleRate;

    private double StepAdjustment => BitConverter.Int64BitsToDouble(Volatile.Read(ref _stepAdjustmentBits));

    /// <summary>Capture thread entry point: processes interleaved samples in the input format.</summary>
    public void ProcessInput(ReadOnlySpan<float> interleaved)
    {
        int inputChannels = _input.Channels;
        int frames = interleaved.Length / inputChannels;
        if (frames == 0)
        {
            return;
        }

        _resampler.StepAdjustment = StepAdjustment;
        float gain = InputGain;
        int queued = 0;

        for (int offset = 0; offset < frames; offset += MaxChunkFrames)
        {
            int count = Math.Min(MaxChunkFrames, frames - offset);
            var block = _processBuffer.AsSpan(0, count * _channels);
            ChannelMapper.Map(interleaved.Slice(offset * inputChannels, count * inputChannels), inputChannels, block, _channels, count);

            if (gain != 1f)
            {
                for (int i = 0; i < block.Length; i++)
                {
                    block[i] *= gain;
                }
            }

            InputMeter.Update(block);
            _chain.Process(block);

            if (!IsFinite(block))
            {
                // An effect blew up (should never happen, but never send garbage to the speakers).
                block.Clear();
                _chain.ResetEffects();
            }

            int produced = _resampler.Process(block, _resampleBuffer) * _channels;
            int space = _ring.Free / _channels * _channels;
            int written = _ring.Write(_resampleBuffer.AsSpan(0, Math.Min(produced, space)));
            if (written < produced)
            {
                Interlocked.Increment(ref _overflows);
            }

            queued += written / _channels;
        }

        Volatile.Write(ref _captureBlockFrames, queued);
        Volatile.Write(ref _lastCaptureTimestamp, _time.GetTimestamp());
    }

    /// <summary>Render thread entry point: fills the whole buffer with interleaved samples in the output format.</summary>
    public void RenderOutput(Span<float> interleaved)
    {
        int outputChannels = _output.Channels;
        int frames = interleaved.Length / outputChannels;
        if (frames == 0)
        {
            return;
        }

        if (_renderBuffer.Length < frames * _channels)
        {
            _renderBuffer = new float[frames * _channels];
        }

        var block = _renderBuffer.AsSpan(0, frames * _channels);
        int available = _ring.Count / _channels;
        _averageFill += (available - _averageFill) * 0.05;
        Volatile.Write(ref _averageFillFrames, (float)_averageFill);

        // Largest recent block sizes, decaying slowly so a single big callback is forgotten.
        _renderBlockFrames = Math.Max(frames, _renderBlockFrames * 0.995);
        double target = _renderBlockFrames + Volatile.Read(ref _captureBlockFrames) + _safetyFrames;

        if (!_primed)
        {
            // Wait until there is enough audio for one input block, this callback and the safety margin.
            if (available < target)
            {
                interleaved.Clear();
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
        ChannelMapper.Map(block, _channels, interleaved, outputChannels, frames);

        float gain = OutputGain;
        for (int i = 0; i < interleaved.Length; i++)
        {
            float sample = interleaved[i] * gain;
            interleaved[i] = sample > 1f ? 1f : sample < -1f ? -1f : sample;
        }

        OutputMeter.Update(interleaved);
    }

    private void UpdateDriftControl(int available, int frames, double target)
    {
        // Frames captured since the last input callback have not reached the buffer yet, but they
        // exist: counting them turns the saw-tooth fill level into a smooth, drift revealing signal.
        double elapsed = (double)(_time.GetTimestamp() - Volatile.Read(ref _lastCaptureTimestamp)) / _time.TimestampFrequency;
        double pending = Math.Clamp(elapsed, 0, MaxCaptureGapSeconds) * _output.SampleRate;
        double fill = available + pending;

        double smoothing = Math.Min(1.0, frames / (FillSmoothingSeconds * _output.SampleRate));
        _virtualFill = _virtualFill < 0 ? fill : _virtualFill + (fill - _virtualFill) * smoothing;

        _controlElapsedFrames += frames;
        if (_controlElapsedFrames < _controlWindowFrames)
        {
            return;
        }

        _controlElapsedFrames = 0;
        double excess = _virtualFill - target;
        if (excess > HardSkipSeconds * _output.SampleRate && available - frames > excess)
        {
            // Far too much audio queued up (e.g. after a hiccup): drop it instead of slowly draining.
            _ring.Skip((int)excess * _channels);
            _fadeInPosition = 0;
            _virtualFill -= (int)excess;
            excess = 0;
        }

        // Proportional controller: drain (or refill) the excess over the time constant.
        double correction = Math.Clamp(excess / (ControlTimeConstantSeconds * _output.SampleRate), -MaxCorrection, MaxCorrection);
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

    private static bool IsFinite(ReadOnlySpan<float> block)
    {
        foreach (float sample in block)
        {
            if (!float.IsFinite(sample))
            {
                return false;
            }
        }

        return true;
    }

    private static void ValidateFormat(AudioFormat format, string name)
    {
        if (format.SampleRate < 1000 || format.Channels < 1)
        {
            throw new ArgumentException($"Unsupported audio format: {format}.", name);
        }
    }
}
