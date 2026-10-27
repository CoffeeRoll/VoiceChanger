namespace VoiceChanger.Core.Dsp;

/// <summary>
/// Formant-preserving pitch shifter for voices that works on the spectrum: a phase vocoder per
/// channel that rebuilds the harmonics at the new pitch under the voice's own spectral envelope
/// (<see cref="PhaseVocoder.PreserveFormants"/>). It suits raising a voice, where moving pulses
/// closer together makes them overlap; going down, the upper formants have to be rebuilt from the
/// weak, breathy top of the original spectrum, which <see cref="PsolaPitchShifter"/> avoids.
/// <para>
/// With a <see cref="Correction"/>, the ratio is chosen for every frame from the period of the voice
/// around the middle of that frame, found by a pitch detector that runs on the input delayed to
/// line up with it.
/// </para>
/// </summary>
public sealed class SpectralVoiceShifter : IVoiceShifter
{
    private const double FrameSeconds = 0.032;
    private const double DetectorWindowSeconds = 0.032;

    private readonly int _channels;
    private readonly int _sampleRate;
    private readonly int _frameSize;
    private readonly PhaseVocoder[] _vocoders;
    private readonly PitchDetector _detector;
    private readonly float[] _delay;
    private readonly float[] _mono;
    private int _delayIndex;
    private long _position;
    private double _ratio = 1;
    private double _formantRatio = 1;
    private PitchCorrection? _correction;

    public SpectralVoiceShifter(int channels, int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 8000);

        _channels = channels;
        _sampleRate = sampleRate;
        _frameSize = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)(FrameSeconds * sampleRate));
        _vocoders = new PhaseVocoder[channels];
        for (int ch = 0; ch < channels; ch++)
        {
            _vocoders[ch] = new PhaseVocoder(_frameSize, sampleRate) { PreserveFormants = true };
        }

        // Frames are a hop apart; the detector looks at the last 32 ms and reports the middle of
        // its last three estimates, so its input lags the frame's middle by the rest.
        int hop = _vocoders[0].SamplesUntilFrame;
        _detector = new PitchDetector(sampleRate, windowSeconds: DetectorWindowSeconds, hopSeconds: (double)hop / sampleRate);
        int delay = _frameSize / 2 - (int)(DetectorWindowSeconds / 2 * sampleRate) - hop;
        _delay = new float[Math.Max(1, delay)];
        _mono = new float[hop];
        Reset();
    }

    public double Ratio
    {
        get => _ratio;
        set
        {
            _ratio = Math.Clamp(value, 0.25, 4.0);
            if (_correction is null)
            {
                foreach (var vocoder in _vocoders)
                {
                    vocoder.Ratio = _ratio;
                }
            }
        }
    }

    public PitchCorrection? Correction
    {
        get => _correction;
        set
        {
            _correction = value;
            Ratio = _ratio;
        }
    }

    public double FormantRatio
    {
        get => _formantRatio;
        set
        {
            _formantRatio = Math.Clamp(value, 0.5, 2.0);
            foreach (var vocoder in _vocoders)
            {
                vocoder.FormantRatio = _formantRatio;
            }
        }
    }

    public int LatencyFrames => PhaseVocoder.LatencyFor(_frameSize);

    public void Reset()
    {
        foreach (var vocoder in _vocoders)
        {
            vocoder.Reset();
            vocoder.Ratio = _ratio;
        }

        _detector.Reset();
        Array.Clear(_delay);
        _delayIndex = 0;
        _position = 0;
    }

    public void Process(Span<float> interleaved)
    {
        int channels = _channels;
        int frames = interleaved.Length / channels;
        for (int done = 0; done < frames;)
        {
            // Up to the sample that completes the next frame, so its ratio can be chosen first.
            int until = _vocoders[0].SamplesUntilFrame;
            int count = Math.Min(until, frames - done);
            var segment = interleaved.Slice(done * channels, count * channels);
            if (_correction is { } correction)
            {
                Detect(segment, count);
                if (count == until)
                {
                    double period = _detector.IsVoiced ? _sampleRate / _detector.Frequency : 0;
                    double ratio = Math.Clamp(correction(period, _position + count - _frameSize / 2), 0.25, 4.0);
                    foreach (var vocoder in _vocoders)
                    {
                        vocoder.Ratio = ratio;
                    }
                }
            }

            for (int ch = 0; ch < channels; ch++)
            {
                _vocoders[ch].Process(segment, ch, channels);
            }

            _position += count;
            done += count;
        }
    }

    /// <summary>Feeds the detector the delayed mono mix of a segment.</summary>
    private void Detect(ReadOnlySpan<float> segment, int count)
    {
        float scale = 1f / _channels;
        for (int i = 0; i < count; i++)
        {
            float sum = 0;
            for (int ch = 0; ch < _channels; ch++)
            {
                sum += segment[i * _channels + ch];
            }

            _mono[i] = _delay[_delayIndex];
            _delay[_delayIndex] = sum * scale;
            _delayIndex = (_delayIndex + 1) % _delay.Length;
        }

        _detector.Process(_mono.AsSpan(0, count));
    }
}
