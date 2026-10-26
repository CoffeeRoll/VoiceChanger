namespace VoiceChanger.Core.Dsp;

/// <summary>
/// Pitch shifter for small ratios that change all the time, as used for pitch correction.
/// <para>
/// A single read tap moves through a delay line at the speed of the ratio (a slowly varying delay is
/// a perfect pitch shift). Only when the tap is about to run out of delay line does a second tap take
/// over, starting at the far end of the line at a position whose waveform matches the current one,
/// with a short cross-fade. Unlike two-tap "rotating head" shifters, there is no comb filtering at
/// ratios close to 1, the ratio can cross 1 freely, and at exactly 1 it is a plain delay.
/// </para>
/// </summary>
public sealed class SplicingPitchShifter
{
    private const int Margin = 2;
    private const double RangeSeconds = 0.02;
    private const double SearchSeconds = 0.012;
    private const double FadeSeconds = 0.008;
    private const double CorrelationSeconds = 0.006;

    private readonly int _channels;
    private readonly int _margin;
    private readonly float[][] _lines;
    private readonly float[] _analysis;
    private readonly int _mask;
    private readonly int _range;
    private readonly int _search;
    private readonly int _fade;
    private readonly int _correlationLength;
    private readonly WaveformAligner _aligner;
    private int _write;
    private double _active;
    private double _incoming;
    private bool _fading;
    private int _fadePosition;
    private double _ratio = 1;

    /// <param name="extraDelaySeconds">
    /// Additional fixed delay, e.g. to line the audio up with a pitch detector that controls the ratio.
    /// </param>
    public SplicingPitchShifter(int channels, int sampleRate, double extraDelaySeconds = 0)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1000);
        ArgumentOutOfRangeException.ThrowIfNegative(extraDelaySeconds);

        _channels = channels;
        _margin = Margin + (int)(extraDelaySeconds * sampleRate);
        _range = (int)(RangeSeconds * sampleRate);
        _search = (int)(SearchSeconds * sampleRate);
        _fade = Math.Max(16, (int)(FadeSeconds * sampleRate));
        _correlationLength = (int)(CorrelationSeconds * sampleRate);
        _aligner = new WaveformAligner(_search, _correlationLength);

        int length = (int)System.Numerics.BitOperations.RoundUpToPowerOf2(
            (uint)(_margin + _range + 2 * _search + _correlationLength + 8));
        _mask = length - 1;
        _lines = new float[channels][];
        for (int ch = 0; ch < channels; ch++)
        {
            _lines[ch] = new float[length];
        }

        _analysis = channels == 1 ? _lines[0] : new float[length];
        Reset();
    }

    /// <summary>Output frequency divided by input frequency (0.5 to 2).</summary>
    public double Ratio
    {
        get => _ratio;
        set => _ratio = Math.Clamp(value, 0.5, 2.0);
    }

    /// <summary>Average delay through the shifter, in frames.</summary>
    public int LatencyFrames => _margin + (_range + _search) / 2;

    private double Lowest => _margin;

    private double Highest => _margin + _range + _search;

    public void Reset()
    {
        foreach (var line in _lines)
        {
            Array.Clear(line);
        }

        Array.Clear(_analysis);
        _write = 0;
        _active = LatencyFrames;
        _fading = false;
        _fadePosition = 0;
    }

    /// <summary>Shifts interleaved audio in place.</summary>
    public void Process(Span<float> interleaved)
    {
        int channels = _channels;
        int frames = interleaved.Length / channels;
        double rate = 1.0 - _ratio;
        double needed = Math.Abs(rate) * _fade + 2;
        float channelScale = 1f / channels;

        for (int frame = 0, i = 0; frame < frames; frame++, i += channels)
        {
            _write = (_write + 1) & _mask;
            float sum = 0;
            for (int ch = 0; ch < channels; ch++)
            {
                float sample = interleaved[i + ch];
                _lines[ch][_write] = sample;
                sum += sample;
            }

            if (channels > 1)
            {
                _analysis[_write] = sum * channelScale;
            }

            _active = Math.Clamp(_active + rate, Lowest, Highest + _search);
            if (_fading)
            {
                _incoming = Math.Clamp(_incoming + rate, Lowest, Highest + _search);
            }
            else if ((rate < 0 && _active - Lowest < needed) || (rate > 0 && Highest - _active < needed))
            {
                // Pitching up the delay shrinks, so restart near the far (old) end; pitching down
                // it grows, so restart near the write head. Align the new tap with the current one.
                double start = rate < 0 ? _margin + _range : _margin;
                _incoming = start + _aligner.FindOffset(_analysis, _mask, _write, _active, start, _search, _correlationLength);
                _fading = true;
                _fadePosition = 0;
            }

            if (!_fading)
            {
                for (int ch = 0; ch < channels; ch++)
                {
                    interleaved[i + ch] = WaveformAligner.ReadCubic(_lines[ch], _mask, _write, _active);
                }

                continue;
            }

            float t = (_fadePosition + 0.5f) / _fade;
            float gainIn = 0.5f - 0.5f * MathF.Cos(MathF.PI * t);
            float gainOut = 1f - gainIn;
            for (int ch = 0; ch < channels; ch++)
            {
                var line = _lines[ch];
                interleaved[i + ch] = gainOut * WaveformAligner.ReadCubic(line, _mask, _write, _active)
                                      + gainIn * WaveformAligner.ReadCubic(line, _mask, _write, _incoming);
            }

            if (++_fadePosition >= _fade)
            {
                _active = _incoming;
                _fading = false;
            }
        }
    }
}
