namespace VoiceChanger.Core.Dsp;

/// <summary>Filter shapes supported by <see cref="BiquadCoefficients.Design"/>.</summary>
public enum BiquadType
{
    LowPass,
    HighPass,
    BandPass,
    Notch,
    Peaking,
    LowShelf,
    HighShelf,
}

/// <summary>
/// Normalised second order IIR coefficients (a0 == 1), designed with the formulas from
/// Robert Bristow-Johnson's "Audio EQ Cookbook".
/// </summary>
public readonly record struct BiquadCoefficients(double B0, double B1, double B2, double A1, double A2)
{
    /// <summary>Coefficients that pass the signal through unchanged.</summary>
    public static BiquadCoefficients Identity { get; } = new(1, 0, 0, 0, 0);

    public bool IsIdentity => B0 == 1 && B1 == 0 && B2 == 0 && A1 == 0 && A2 == 0;

    public static BiquadCoefficients Design(BiquadType type, double sampleRate, double frequency, double q, double gainDb = 0)
    {
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        }

        // Keep the design numerically sane: the bilinear transform is undefined at Nyquist.
        frequency = Math.Clamp(frequency, 1.0, sampleRate * 0.499);
        q = Math.Max(q, 0.01);

        double a = Math.Pow(10, gainDb / 40);
        double w0 = 2 * Math.PI * frequency / sampleRate;
        double cos = Math.Cos(w0);
        double alpha = Math.Sin(w0) / (2 * q);

        double b0, b1, b2, a0, a1, a2;
        switch (type)
        {
            case BiquadType.LowPass:
                b0 = (1 - cos) / 2; b1 = 1 - cos; b2 = (1 - cos) / 2;
                a0 = 1 + alpha; a1 = -2 * cos; a2 = 1 - alpha;
                break;
            case BiquadType.HighPass:
                b0 = (1 + cos) / 2; b1 = -(1 + cos); b2 = (1 + cos) / 2;
                a0 = 1 + alpha; a1 = -2 * cos; a2 = 1 - alpha;
                break;
            case BiquadType.BandPass:
                b0 = alpha; b1 = 0; b2 = -alpha;
                a0 = 1 + alpha; a1 = -2 * cos; a2 = 1 - alpha;
                break;
            case BiquadType.Notch:
                b0 = 1; b1 = -2 * cos; b2 = 1;
                a0 = 1 + alpha; a1 = -2 * cos; a2 = 1 - alpha;
                break;
            case BiquadType.Peaking:
                b0 = 1 + alpha * a; b1 = -2 * cos; b2 = 1 - alpha * a;
                a0 = 1 + alpha / a; a1 = -2 * cos; a2 = 1 - alpha / a;
                break;
            case BiquadType.LowShelf:
            {
                double s = 2 * Math.Sqrt(a) * alpha;
                b0 = a * ((a + 1) - (a - 1) * cos + s);
                b1 = 2 * a * ((a - 1) - (a + 1) * cos);
                b2 = a * ((a + 1) - (a - 1) * cos - s);
                a0 = (a + 1) + (a - 1) * cos + s;
                a1 = -2 * ((a - 1) + (a + 1) * cos);
                a2 = (a + 1) + (a - 1) * cos - s;
                break;
            }
            case BiquadType.HighShelf:
            {
                double s = 2 * Math.Sqrt(a) * alpha;
                b0 = a * ((a + 1) + (a - 1) * cos + s);
                b1 = -2 * a * ((a - 1) + (a + 1) * cos);
                b2 = a * ((a + 1) + (a - 1) * cos - s);
                a0 = (a + 1) - (a - 1) * cos + s;
                a1 = 2 * ((a - 1) - (a + 1) * cos);
                a2 = (a + 1) - (a - 1) * cos - s;
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(type), type, null);
        }

        return new BiquadCoefficients(b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0);
    }

    /// <summary>Linear magnitude of the filter's frequency response at <paramref name="frequency"/>.</summary>
    public double MagnitudeAt(double frequency, double sampleRate)
    {
        double w = 2 * Math.PI * frequency / sampleRate;
        double cos1 = Math.Cos(w), sin1 = Math.Sin(w);
        double cos2 = Math.Cos(2 * w), sin2 = Math.Sin(2 * w);

        // H(e^jw) = (b0 + b1 e^-jw + b2 e^-2jw) / (1 + a1 e^-jw + a2 e^-2jw)
        double numRe = B0 + B1 * cos1 + B2 * cos2;
        double numIm = -(B1 * sin1 + B2 * sin2);
        double denRe = 1 + A1 * cos1 + A2 * cos2;
        double denIm = -(A1 * sin1 + A2 * sin2);

        return Math.Sqrt((numRe * numRe + numIm * numIm) / (denRe * denRe + denIm * denIm));
    }
}

/// <summary>
/// A biquad filter (transposed direct form II) with independent state per channel of an
/// interleaved buffer. State is kept in double precision so low frequency filters stay quiet.
/// </summary>
public sealed class BiquadFilter
{
    private double[] _z1;
    private double[] _z2;

    public BiquadFilter(int channels)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        _z1 = new double[channels];
        _z2 = new double[channels];
    }

    public BiquadCoefficients Coefficients { get; set; } = BiquadCoefficients.Identity;

    public int Channels => _z1.Length;

    public void Reset()
    {
        Array.Clear(_z1);
        Array.Clear(_z2);
    }

    public void Process(Span<float> interleaved)
    {
        var c = Coefficients;
        int channels = _z1.Length;

        for (int ch = 0; ch < channels; ch++)
        {
            double z1 = _z1[ch], z2 = _z2[ch];
            for (int i = ch; i < interleaved.Length; i += channels)
            {
                double x = interleaved[i];
                double y = c.B0 * x + z1;
                z1 = c.B1 * x - c.A1 * y + z2;
                z2 = c.B2 * x - c.A2 * y;
                interleaved[i] = (float)y;
            }

            // Flush denormals so a decaying tail never slows the audio thread down.
            _z1[ch] = Math.Abs(z1) < 1e-25 ? 0 : z1;
            _z2[ch] = Math.Abs(z2) < 1e-25 ? 0 : z2;
        }
    }
}
