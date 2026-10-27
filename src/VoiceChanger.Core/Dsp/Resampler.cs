namespace VoiceChanger.Core.Dsp;

/// <summary>
/// Streaming band-limited (windowed sinc) sample rate converter for interleaved audio.
/// <para>
/// The conversion ratio can be nudged at run time through <see cref="StepAdjustment"/>, which the
/// audio processor uses to compensate for clock drift between two independent audio devices.
/// The converter introduces a fixed delay of <see cref="LatencyFrames"/> input frames.
/// </para>
/// </summary>
public sealed class Resampler
{
    private const int Phases = 256;

    private readonly int _channels;
    private readonly int _halfTaps;
    private readonly int _taps;
    private readonly double _baseStep;
    private readonly float[] _table;
    private readonly float[] _coefficients;

    private float[] _buffer;
    private int _bufferedFrames;
    private double _position;

    public Resampler(int inputRate, int outputRate, int channels, int halfTaps = 16)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(inputRate, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(outputRate, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(halfTaps, 2);

        InputRate = inputRate;
        OutputRate = outputRate;
        _channels = channels;
        _halfTaps = halfTaps;
        _taps = halfTaps * 2;
        _baseStep = (double)inputRate / outputRate;
        _coefficients = new float[_taps];
        // When downsampling, cut slightly below the new Nyquist frequency to avoid aliasing.
        // Otherwise use the full band, which makes 1:1 conversion an exact delay.
        _table = BuildTable(outputRate >= inputRate ? 1.0 : 0.96 * outputRate / inputRate);
        _buffer = new float[Math.Max(4096, _taps * 4) * channels];
        Reset();
    }

    public int InputRate { get; }

    public int OutputRate { get; }

    public int Channels => _channels;

    /// <summary>Delay introduced by the interpolation filter, in input frames.</summary>
    public int LatencyFrames => _halfTaps;

    /// <summary>
    /// Multiplier applied to the input/output step. Values above 1 consume input faster
    /// (fewer output frames), values below 1 consume it slower. The anti-aliasing filter is chosen
    /// for the rates given at construction, so well above 1 the input must already be band-limited.
    /// </summary>
    public double StepAdjustment { get; set; } = 1.0;

    /// <summary>Upper bound of the number of frames <see cref="Process"/> can produce for <paramref name="inputFrames"/>.</summary>
    public int MaxOutputFrames(int inputFrames) =>
        (int)Math.Ceiling((inputFrames + _taps) / (_baseStep * 0.9)) + 2;

    public void Reset()
    {
        // Pre-roll with silence so the first real input frame sits at the centre of the kernel.
        Array.Clear(_buffer);
        _bufferedFrames = _halfTaps - 1;
        _position = _halfTaps - 1;
    }

    /// <summary>
    /// Consumes all of <paramref name="input"/> and writes converted frames to <paramref name="output"/>.
    /// Returns the number of frames written.
    /// </summary>
    public int Process(ReadOnlySpan<float> input, Span<float> output)
    {
        int channels = _channels;
        int inputFrames = input.Length / channels;
        EnsureCapacity(_bufferedFrames + inputFrames);
        input[..(inputFrames * channels)].CopyTo(_buffer.AsSpan(_bufferedFrames * channels));
        _bufferedFrames += inputFrames;

        double step = _baseStep * StepAdjustment;
        int maxOutput = output.Length / channels;
        int produced = 0;
        float[] coefficients = _coefficients;
        float[] buffer = _buffer;

        while (produced < maxOutput)
        {
            int centre = (int)_position;
            if (centre + _halfTaps >= _bufferedFrames)
            {
                break;
            }

            double phase = (_position - centre) * Phases;
            int row = (int)phase;
            float t = (float)(phase - row);
            int row0 = row * _taps;
            int row1 = row0 + _taps;
            for (int j = 0; j < _taps; j++)
            {
                float a = _table[row0 + j];
                coefficients[j] = a + t * (_table[row1 + j] - a);
            }

            int first = (centre - _halfTaps + 1) * channels;
            int outIndex = produced * channels;
            for (int ch = 0; ch < channels; ch++)
            {
                float sum = 0;
                int index = first + ch;
                for (int j = 0; j < _taps; j++, index += channels)
                {
                    sum += coefficients[j] * buffer[index];
                }

                output[outIndex + ch] = sum;
            }

            produced++;
            _position += step;
        }

        // Discard input frames that no future output can reference any more.
        int discard = Math.Clamp((int)_position - _halfTaps + 1, 0, _bufferedFrames);
        if (discard > 0)
        {
            buffer.AsSpan(discard * channels, (_bufferedFrames - discard) * channels).CopyTo(buffer);
            _bufferedFrames -= discard;
            _position -= discard;
        }

        return produced;
    }

    private void EnsureCapacity(int frames)
    {
        int needed = frames * _channels;
        if (needed > _buffer.Length)
        {
            Array.Resize(ref _buffer, Math.Max(needed, _buffer.Length * 2));
        }
    }

    private float[] BuildTable(double cutoff)
    {
        var table = new float[(Phases + 1) * _taps];
        for (int p = 0; p <= Phases; p++)
        {
            double fraction = (double)p / Phases;
            double sum = 0;
            for (int j = 0; j < _taps; j++)
            {
                double distance = fraction + (_halfTaps - 1) - j;
                double value = cutoff * Sinc(cutoff * distance) * Blackman(distance / _halfTaps);
                table[p * _taps + j] = (float)value;
                sum += value;
            }

            // Normalise every phase to unity DC gain.
            for (int j = 0; j < _taps; j++)
            {
                table[p * _taps + j] = (float)(table[p * _taps + j] / sum);
            }
        }

        return table;
    }

    private static double Sinc(double x) => Math.Abs(x) < 1e-9 ? 1.0 : Math.Sin(Math.PI * x) / (Math.PI * x);

    private static double Blackman(double x) =>
        Math.Abs(x) >= 1 ? 0 : 0.42 + 0.5 * Math.Cos(Math.PI * x) + 0.08 * Math.Cos(2 * Math.PI * x);
}
