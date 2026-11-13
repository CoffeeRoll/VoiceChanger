namespace VoiceChanger.Core.Dsp;

/// <summary>A key worked out from what was sung.</summary>
/// <param name="Root">Pitch class of the key note, from 0 (C) to 11 (B).</param>
/// <param name="Scale"><see cref="ScaleType.Major"/> or <see cref="ScaleType.NaturalMinor"/>.</param>
/// <param name="Confidence">From 0 (a guess between several keys) to 1 (clearly this key).</param>
public sealed record DetectedKey(int Root, ScaleType Scale, double Confidence)
{
    public override string ToString() => $"{MusicalScale.NoteNames[Root]} {(Scale == ScaleType.Major ? "major" : "minor")}";
}

/// <summary>
/// Works out the key of a melody: how long the voice spent on each of the twelve notes, compared
/// with how often each note of a key is heard in music in that key (the profiles of C. L. Krumhansl
/// and E. J. Kessler, 1982), for all twelve major and twelve minor keys.
/// </summary>
public sealed class KeyFinder
{
    // How strongly each note of a key belongs to it, from the key note up.
    private static readonly double[] MajorProfile = [6.35, 2.23, 3.48, 2.33, 4.38, 4.09, 2.52, 5.19, 2.39, 3.66, 2.29, 2.88];
    private static readonly double[] MinorProfile = [6.33, 2.68, 3.52, 5.38, 2.60, 3.53, 2.54, 4.75, 3.98, 2.69, 3.34, 3.17];

    /// <summary>Pitches further than this (in semitones) from a note are slides between notes and don't count.</summary>
    private const double InTune = 0.35;

    private readonly double[] _seconds = new double[12];

    /// <summary>Seconds of voice taken in so far (on or near a note).</summary>
    public double VoicedSeconds { get; private set; }

    /// <summary>Time spent on each pitch class (0 = C), in seconds.</summary>
    public IReadOnlyList<double> Histogram => _seconds;

    public void Reset()
    {
        Array.Clear(_seconds);
        VoicedSeconds = 0;
    }

    /// <summary>Takes in a stretch of voice: its pitch (a fractional MIDI note) and how long it lasted.</summary>
    public void Add(double note, double seconds)
    {
        if (!double.IsFinite(note) || seconds <= 0)
        {
            return;
        }

        int nearest = (int)Math.Round(note);
        if (Math.Abs(note - nearest) > InTune)
        {
            return;
        }

        _seconds[MusicalScale.PitchClass(nearest)] += seconds;
        VoicedSeconds += seconds;
    }

    /// <summary>The key that fits best, or null before anything was sung.</summary>
    public DetectedKey? Estimate() => Estimate(_seconds);

    /// <summary>The key that fits a pitch-class histogram best (12 values, C first), or null when it is empty.</summary>
    public static DetectedKey? Estimate(IReadOnlyList<double> histogram)
    {
        ArgumentNullException.ThrowIfNull(histogram);
        if (histogram.Count != 12 || histogram.Sum() <= 0)
        {
            return null;
        }

        double best = double.NegativeInfinity, second = double.NegativeInfinity;
        int bestRoot = 0;
        var bestScale = ScaleType.Major;
        foreach (var (scale, profile) in new[] { (ScaleType.Major, MajorProfile), (ScaleType.NaturalMinor, MinorProfile) })
        {
            for (int root = 0; root < 12; root++)
            {
                double fit = Correlation(histogram, profile, root);
                if (fit > best)
                {
                    second = best;
                    best = fit;
                    bestRoot = root;
                    bestScale = scale;
                }
                else if (fit > second)
                {
                    second = fit;
                }
            }
        }

        // A relative major and minor share their notes, so the runner-up is often close: a clear lead
        // over it, with a good fit, makes for confidence.
        double confidence = Math.Clamp((best - second) / 0.15, 0, 1) * Math.Clamp(best / 0.6, 0, 1);
        return new DetectedKey(bestRoot, bestScale, confidence);
    }

    /// <summary>Pearson correlation of the histogram with a key profile starting on <paramref name="root"/>.</summary>
    private static double Correlation(IReadOnlyList<double> histogram, double[] profile, int root)
    {
        double meanH = histogram.Average(), meanP = profile.Average();
        double cross = 0, varH = 0, varP = 0;
        for (int pc = 0; pc < 12; pc++)
        {
            double h = histogram[pc] - meanH;
            double p = profile[MusicalScale.PitchClass(pc - root)] - meanP;
            cross += h * p;
            varH += h * h;
            varP += p * p;
        }

        return varH > 0 ? cross / Math.Sqrt(varH * varP) : 0;
    }
}
