namespace VoiceChanger.Core.Dsp;

/// <summary>
/// In-place iterative radix-2 complex FFT with precomputed twiddle factors and bit reversal table.
/// The forward transform uses e^(-i2πkn/N); the inverse is unscaled (divide by N to round-trip).
/// </summary>
public sealed class Fft
{
    private readonly int[] _bitReverse;
    private readonly double[] _cos;
    private readonly double[] _sin;

    public Fft(int size)
    {
        if (size < 2 || (size & (size - 1)) != 0)
        {
            throw new ArgumentException("FFT size must be a power of two greater than one.", nameof(size));
        }

        Size = size;
        int bits = System.Numerics.BitOperations.Log2((uint)size);

        _bitReverse = new int[size];
        for (int i = 0; i < size; i++)
        {
            int reversed = 0;
            for (int b = 0; b < bits; b++)
            {
                reversed |= ((i >> b) & 1) << (bits - 1 - b);
            }

            _bitReverse[i] = reversed;
        }

        _cos = new double[size / 2];
        _sin = new double[size / 2];
        for (int i = 0; i < size / 2; i++)
        {
            double angle = 2 * Math.PI * i / size;
            _cos[i] = Math.Cos(angle);
            _sin[i] = Math.Sin(angle);
        }
    }

    public int Size { get; }

    public void Forward(Span<float> real, Span<float> imag) => Transform(real, imag, inverse: false);

    public void Inverse(Span<float> real, Span<float> imag) => Transform(real, imag, inverse: true);

    private void Transform(Span<float> real, Span<float> imag, bool inverse)
    {
        int n = Size;
        if (real.Length < n || imag.Length < n)
        {
            throw new ArgumentException("Buffers are smaller than the FFT size.");
        }

        for (int i = 0; i < n; i++)
        {
            int j = _bitReverse[i];
            if (j > i)
            {
                (real[i], real[j]) = (real[j], real[i]);
                (imag[i], imag[j]) = (imag[j], imag[i]);
            }
        }

        double sign = inverse ? 1 : -1;
        for (int length = 2; length <= n; length <<= 1)
        {
            int half = length >> 1;
            int twiddleStep = n / length;
            for (int start = 0; start < n; start += length)
            {
                for (int k = 0; k < half; k++)
                {
                    double wr = _cos[k * twiddleStep];
                    double wi = sign * _sin[k * twiddleStep];

                    int even = start + k;
                    int odd = even + half;

                    double oddRe = real[odd] * wr - imag[odd] * wi;
                    double oddIm = real[odd] * wi + imag[odd] * wr;

                    real[odd] = (float)(real[even] - oddRe);
                    imag[odd] = (float)(imag[even] - oddIm);
                    real[even] = (float)(real[even] + oddRe);
                    imag[even] = (float)(imag[even] + oddIm);
                }
            }
        }
    }
}
