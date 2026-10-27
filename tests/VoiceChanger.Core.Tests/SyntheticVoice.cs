using VoiceChanger.Core.Dsp;

namespace VoiceChanger.Core.Tests;

/// <summary>
/// A source-filter voice for testing voice effects: Rosenberg glottal pulses with jitter and
/// shimmer, aspiration noise, and five formant resonators that glide between vowels. Unlike a sum
/// of sines it has what real voices have: harmonics that weaken and turn noisy towards the top.
/// </summary>
internal static class SyntheticVoice
{
    public const int Rate = 48000;

    private static readonly double[][] VowelFormants =
    [
        [730, 1090, 2440, 3400, 4500], // a
        [270, 2290, 3010, 3500, 4500], // i
        [300, 870, 2240, 3300, 4500],  // u
        [530, 1840, 2480, 3400, 4500], // e
        [570, 840, 2410, 3300, 4500],  // o
    ];

    private static readonly double[] Bandwidths = [80, 100, 140, 220, 300];

    /// <param name="contour">Pitch in Hz at a time in seconds (0 = silence).</param>
    /// <param name="vowelAt">Vowel index (fractional values blend) at a time.</param>
    /// <param name="breathDb">Level of the aspiration noise (relative; -24 is breathy, -40 clean).</param>
    /// <param name="formantScale">Multiplies every formant frequency (to make the "ideal" of a shift without formant preservation).</param>
    public static float[] Generate(double seconds, Func<double, double> contour, Func<double, double> vowelAt, double breathDb = -24, double jitter = 0.01, int seed = 3, double formantScale = 1)
    {
        int frames = (int)(seconds * Rate);
        var random = new Random(seed);
        var source = new double[frames];
        var breath = new double[frames];
        int i = 0;
        double amplitude = 0;
        while (i < frames)
        {
            double time = (double)i / Rate;
            double f0 = contour(time);
            if (f0 <= 0)
            {
                // Silence: noise floor only.
                source[i] = 0;
                breath[i] = 0;
                amplitude = Math.Max(0, amplitude - 1.0 / (0.02 * Rate));
                i++;
                continue;
            }

            amplitude = Math.Min(1, amplitude + 1.0 / (0.02 * Rate));
            double period = 1.0 / f0 * (1 + jitter * (random.NextDouble() * 2 - 1));
            int length = (int)Math.Round(period * Rate);
            double shimmer = 1 + 0.05 * (random.NextDouble() * 2 - 1);
            double tp = 0.4 * length, tn = 0.16 * length;
            double previous = 0;
            for (int n = 0; n < length && i < frames; n++, i++)
            {
                double flow = n <= tp ? 0.5 * (1 - Math.Cos(Math.PI * n / tp))
                    : n <= tp + tn ? Math.Cos(Math.PI * (n - tp) / (2 * tn))
                    : 0;
                // Radiation: the derivative of the flow.
                source[i] = (flow - previous) * shimmer * amplitude;
                previous = flow;
                // Aspiration is strongest while the glottis is open.
                breath[i] = (random.NextDouble() * 2 - 1) * (0.3 + flow) * amplitude;
            }
        }

        // Breath noise sits above ~1 kHz.
        var noise = breath.Select(v => (float)v).ToArray();
        new BiquadFilter(1) { Coefficients = BiquadCoefficients.Design(BiquadType.HighPass, Rate, 1000, 0.7) }.Process(noise);
        double sourceRms = Math.Sqrt(source.Average(v => v * v));
        double noiseRms = Math.Sqrt(noise.Average(v => (double)v * v));
        // Scaled so that -24 gives a clearly breathy voice and -40 a clean one (periodicity above 2 kHz).
        double noiseGain = sourceRms * Math.Pow(10, breathDb / 20) / Math.Max(noiseRms, 1e-12) * 10;
        var excitation = new double[frames];
        for (int n = 0; n < frames; n++)
        {
            excitation[n] = source[n] + noiseGain * noise[n];
        }

        // Cascade of formant resonators (Klatt), coefficients refreshed every 64 samples.
        var output = new double[frames];
        var y1 = new double[5];
        var y2 = new double[5];
        var coefficients = new (double A, double B, double C)[5];
        for (int n = 0; n < frames; n++)
        {
            if (n % 64 == 0)
            {
                double v = Math.Clamp(vowelAt((double)n / Rate), 0, VowelFormants.Length - 1);
                int a = (int)v, b = Math.Min(a + 1, VowelFormants.Length - 1);
                double mix = v - a;
                for (int f = 0; f < 5; f++)
                {
                    double frequency = (VowelFormants[a][f] * (1 - mix) + VowelFormants[b][f] * mix) * formantScale;
                    double bandwidth = Bandwidths[f] * formantScale;
                    double c = -Math.Exp(-2 * Math.PI * bandwidth / Rate);
                    double bb = 2 * Math.Exp(-Math.PI * bandwidth / Rate) * Math.Cos(2 * Math.PI * Math.Min(frequency, Rate * 0.45) / Rate);
                    coefficients[f] = (1 - bb - c, bb, c);
                }
            }

            double x = excitation[n];
            for (int f = 0; f < 5; f++)
            {
                double y = coefficients[f].A * x + coefficients[f].B * y1[f] + coefficients[f].C * y2[f];
                y2[f] = y1[f];
                y1[f] = y;
                x = y;
            }

            output[n] = x;
        }

        double peak = output.Max(Math.Abs);
        var result = new float[frames];
        for (int n = 0; n < frames; n++)
        {
            result[n] = (float)(output[n] / peak * 0.5 + 2e-4 * (random.NextDouble() * 2 - 1));
        }

        return result;
    }

    /// <summary>
    /// Speech-like intonation around <paramref name="baseHz"/>: four syllables a second that rise and
    /// fall by a few semitones over a slowly falling line, with short gaps between them.
    /// </summary>
    public static double Speech(double t, double baseHz)
    {
        double syllable = t * 4;
        double inSyllable = syllable - Math.Floor(syllable);
        if (inSyllable > 0.85 || t > 2.8)
        {
            return 0;
        }

        double semitones = -2 * t + 2.5 * Math.Sin(Math.PI * inSyllable) * (1 + 0.4 * Math.Sin(7 * Math.Floor(syllable)));
        return baseHz * Math.Pow(2, semitones / 12);
    }

    /// <summary>Vowels gliding from one to the next.</summary>
    public static double Vowels(double t) => 2 + 2 * Math.Sin(2 * Math.PI * 0.6 * t);

    /// <summary>Energy between two frequencies as a fraction of all energy, in dB (after the first 0.3 s).</summary>
    public static double BandShareDb(float[] signal, double low, double high)
    {
        const int size = 4096;
        var fft = new Fft(size);
        var real = new float[size];
        var imag = new float[size];
        double band = 0, total = 0;
        for (int start = (int)(0.3 * Rate); start + size <= signal.Length; start += size / 2)
        {
            for (int i = 0; i < size; i++)
            {
                real[i] = (float)(signal[start + i] * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / size)));
                imag[i] = 0;
            }

            fft.Forward(real, imag);
            for (int k = 1; k < size / 2; k++)
            {
                double power = (double)real[k] * real[k] + (double)imag[k] * imag[k];
                double frequency = (double)k * Rate / size;
                total += power;
                if (frequency >= low && frequency < high)
                {
                    band += power;
                }
            }
        }

        return 10 * Math.Log10(band / total);
    }

    /// <summary>
    /// Mean periodicity of the voiced frames as an autocorrelation harmonic-to-noise ratio (Boersma):
    /// 10·log10(r / (1 − r)) with r the best normalised autocorrelation for periods of 1.6 to 25 ms.
    /// Optionally only the part of the signal above <paramref name="highPass"/> Hz.
    /// </summary>
    public static double Periodicity(float[] signal, double highPass = 0)
    {
        if (highPass > 0)
        {
            signal = (float[])signal.Clone();
            var filter = new BiquadFilter(1) { Coefficients = BiquadCoefficients.Design(BiquadType.HighPass, Rate, highPass, 0.7) };
            filter.Process(signal);
            filter.Process(signal); // 24 dB/octave
        }

        // Boersma (1993): autocorrelation of the Hann-windowed frame divided by that of the window.
        int window = (int)(0.04 * Rate);
        int hop = (int)(0.01 * Rate);
        int minLag = (int)(0.0016 * Rate), maxLag = (int)(0.025 * Rate);
        const int size = 4096;
        var fft = new Fft(size);
        var real = new float[size];
        var imag = new float[size];
        var hann = new double[window];
        for (int n = 0; n < window; n++)
        {
            hann[n] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * (n + 0.5) / window);
        }

        Array.Clear(real);
        Array.Clear(imag);
        for (int n = 0; n < window; n++)
        {
            real[n] = (float)hann[n];
        }

        fft.Forward(real, imag);
        for (int k = 0; k < size; k++)
        {
            real[k] = real[k] * real[k] + imag[k] * imag[k];
            imag[k] = 0;
        }

        fft.Inverse(real, imag);
        var windowAc = real.Select(v => (double)v / real[0]).ToArray();

        var energies = new List<(double Energy, double Hnr)>();
        for (int start = (int)(0.3 * Rate); start + window < signal.Length; start += hop)
        {
            double e0 = 0, mean = 0;
            for (int n = 0; n < window; n++)
            {
                mean += signal[start + n];
            }

            mean /= window;
            Array.Clear(real);
            Array.Clear(imag);
            for (int n = 0; n < window; n++)
            {
                double v = signal[start + n] - mean;
                e0 += v * v;
                real[n] = (float)(v * hann[n]);
            }

            fft.Forward(real, imag);
            for (int k = 0; k < size; k++)
            {
                real[k] = real[k] * real[k] + imag[k] * imag[k];
                imag[k] = 0;
            }

            fft.Inverse(real, imag);
            double zero = real[0];
            double best = 0;
            for (int lag = minLag; lag <= maxLag; lag++)
            {
                double r = real[lag] / zero / windowAc[lag];
                best = Math.Max(best, r);
            }

            best = Math.Clamp(best, 1e-4, 0.9999);
            energies.Add((e0, 10 * Math.Log10(best / (1 - best))));
        }

        double loudest = energies.Max(e => e.Energy);
        var voiced = energies.Where(e => e.Energy > loudest * 0.03).Select(e => e.Hnr).ToList();
        return voiced.Average();
    }
}
