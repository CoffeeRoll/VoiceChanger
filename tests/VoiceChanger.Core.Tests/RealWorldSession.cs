using VoiceChanger.Core.Dsp;
using static VoiceChanger.Core.Tests.SignalHelpers;

namespace VoiceChanger.Core.Tests;

/// <summary>Long, varied input for robustness tests of real-time effects.</summary>
internal static class RealWorldSession
{
    /// <summary>
    /// Minutes of messy real-world input in random order: speech and singing (male and female,
    /// breathy and clean), vocal fry below the voice range, whistling above it, a chord, a glide out
    /// of range, claps and digital silence; with mains hum, a DC offset and a noise floor throughout.
    /// </summary>
    public static float[] Generate(int rate, double minutes, int seed)
    {
        var random = new Random(seed);
        var parts = new List<float[]>();
        double total = 0;
        while (total < minutes * 60)
        {
            int kind = random.Next(10);
            double seconds = 1.5 + random.NextDouble() * 3;
            double f0 = 90 + random.NextDouble() * 200;
            float[] part = kind switch
            {
                0 or 1 or 2 => SyntheticVoice.Generate(seconds, t => SyntheticVoice.Speech(t % 2.8, f0), SyntheticVoice.Vowels, breathDb: -20 - random.Next(20), seed: random.Next()),
                3 => SyntheticVoice.Generate(seconds, t => f0 * Math.Pow(2, (Math.Floor(t / 0.4) % 5 + 0.3 * Math.Sin(2 * Math.PI * 5.5 * t)) / 12), SyntheticVoice.Vowels, seed: random.Next()),
                4 => SyntheticVoice.Generate(seconds, _ => 55 + random.NextDouble() * 10, SyntheticVoice.Vowels, jitter: 0.08, seed: random.Next()), // vocal fry
                5 => Sine(1200 + random.NextDouble() * 800, SyntheticVoice.Rate, (int)(seconds * SyntheticVoice.Rate), amplitude: 0.3), // whistle
                6 => new float[(int)(seconds * SyntheticVoice.Rate)], // digital silence
                7 => Chord(seconds, random),
                8 => SyntheticVoice.Generate(seconds, t => 320 * Math.Pow(0.15, t / seconds), SyntheticVoice.Vowels, seed: random.Next()), // glide out of range
                _ => Claps(seconds, random),
            };
            parts.Add(part);
            total += seconds;
        }

        var session = parts.SelectMany(p => p).ToArray();
        if (rate != SyntheticVoice.Rate)
        {
            var resampler = new Resampler(SyntheticVoice.Rate, rate, 1);
            var converted = new float[resampler.MaxOutputFrames(session.Length) + 64];
            int produced = 0;
            for (int offset = 0; offset < session.Length; offset += 1024)
            {
                produced += resampler.Process(session.AsSpan(offset, Math.Min(1024, session.Length - offset)), converted.AsSpan(produced));
            }

            session = converted[..produced];
        }

        // Mains hum with harmonics, a DC offset and a noise floor under everything.
        for (int i = 0; i < session.Length; i++)
        {
            double t = (double)i / rate;
            session[i] += (float)(0.004 * Math.Sin(2 * Math.PI * 50 * t) + 0.002 * Math.Sin(2 * Math.PI * 150 * t) + 0.003 + 0.0003 * (random.NextDouble() * 2 - 1));
        }

        return session;
    }

    private static float[] Chord(double seconds, Random random)
    {
        int frames = (int)(seconds * SyntheticVoice.Rate);
        var x = new float[frames];
        double[] f = [196, 247, 294, 392];
        for (int i = 0; i < frames; i++)
        {
            double t = (double)i / SyntheticVoice.Rate;
            double v = 0;
            foreach (double frequency in f)
            {
                v += 2 * (t * frequency % 1) - 1;
            }

            x[i] = (float)(0.08 * v);
        }

        return x;
    }

    private static float[] Claps(double seconds, Random random)
    {
        int frames = (int)(seconds * SyntheticVoice.Rate);
        var x = new float[frames];
        for (int start = 0; start < frames; start += 8000 + random.Next(8000))
        {
            for (int i = 0; i < 2000 && start + i < frames; i++)
            {
                x[start + i] = (float)((random.NextDouble() * 2 - 1) * 0.9 * Math.Exp(-i / 300.0));
            }
        }

        return x;
    }
}
