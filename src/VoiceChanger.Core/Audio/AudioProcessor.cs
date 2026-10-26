using VoiceChanger.Core.Dsp;
using VoiceChanger.Core.Effects;

namespace VoiceChanger.Core.Audio;

/// <summary>
/// Moves audio from a capture stream to a render stream that run on independent clocks.
/// <para>
/// Capture thread (<see cref="ProcessInput"/>): channel conversion → input gain → effect chain →
/// <see cref="ClockBridge"/> (sample rate conversion and drift compensation). Render thread
/// (<see cref="RenderOutput"/>): bridge → channel conversion → output gain.
/// </para>
/// </summary>
public sealed class AudioProcessor
{
    private const int MaxChunkFrames = 1024;

    private readonly AudioFormat _input;
    private readonly AudioFormat _output;
    private readonly int _channels;
    private readonly EffectChain _chain;
    private readonly ClockBridge _bridge;
    private float[] _processBuffer;
    private float[] _renderBuffer;
    private float _inputGain = 1;
    private float _outputGain = 1;

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
        _bridge = new ClockBridge(input.SampleRate, output.SampleRate, processingChannels, safetyMilliseconds, timeProvider);
        _processBuffer = new float[4096 * processingChannels];
        _renderBuffer = new float[4096 * processingChannels];
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
    public int Underruns => _bridge.Underruns;

    /// <summary>Number of input blocks (partially) dropped because the buffer was full.</summary>
    public int Overflows => _bridge.Overflows;

    /// <summary>Average amount of audio waiting between input and output.</summary>
    public double BufferedMilliseconds => _bridge.BufferedMilliseconds;

    /// <summary>Current clock drift compensation in parts per million.</summary>
    public double DriftCorrectionPpm => _bridge.DriftCorrectionPpm;

    /// <summary>Fixed delay of the resampler and the active effects in milliseconds.</summary>
    public double ProcessingLatencyMilliseconds =>
        _bridge.ResamplerLatencyMilliseconds + _chain.LatencyFrames * 1000.0 / _input.SampleRate;

    /// <summary>Capture thread entry point: processes interleaved samples in the input format.</summary>
    public void ProcessInput(ReadOnlySpan<float> interleaved)
    {
        int inputChannels = _input.Channels;
        int frames = interleaved.Length / inputChannels;
        if (frames == 0)
        {
            return;
        }

        if (_processBuffer.Length < frames * _channels)
        {
            _processBuffer = new float[frames * _channels];
        }

        float gain = InputGain;
        for (int offset = 0; offset < frames; offset += MaxChunkFrames)
        {
            int count = Math.Min(MaxChunkFrames, frames - offset);
            var block = _processBuffer.AsSpan(offset * _channels, count * _channels);
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
        }

        // One write per callback so the bridge sees the real input block size.
        _bridge.Write(_processBuffer.AsSpan(0, frames * _channels));
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
        _bridge.Read(block);
        ChannelMapper.Map(block, _channels, interleaved, outputChannels, frames);
        ApplyGainAndClamp(interleaved, OutputGain);
        OutputMeter.Update(interleaved);
    }

    /// <summary>Applies a gain and clamps to full scale so nothing louder ever reaches a device.</summary>
    internal static void ApplyGainAndClamp(Span<float> samples, float gain)
    {
        for (int i = 0; i < samples.Length; i++)
        {
            float sample = samples[i] * gain;
            samples[i] = sample > 1f ? 1f : sample < -1f ? -1f : sample;
        }
    }

    internal static bool IsFinite(ReadOnlySpan<float> block)
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

    internal static void ValidateFormat(AudioFormat format, string name)
    {
        if (format.SampleRate < 1000 || format.Channels < 1)
        {
            throw new ArgumentException($"Unsupported audio format: {format}.", name);
        }
    }
}
