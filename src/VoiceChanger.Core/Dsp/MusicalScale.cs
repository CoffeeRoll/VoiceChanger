namespace VoiceChanger.Core.Dsp;

/// <summary>Scales an auto-tune can snap to.</summary>
public enum ScaleType
{
    Chromatic,
    Major,
    NaturalMinor,
    HarmonicMinor,
    MajorPentatonic,
    MinorPentatonic,
    Blues,
    Dorian,
    Mixolydian,
}

/// <summary>Note, frequency and scale helpers. Notes are MIDI numbers: 60 is middle C (C4), 69 is A4.</summary>
public static class MusicalScale
{
    public static IReadOnlyList<string> NoteNames { get; } = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];

    public static IReadOnlyList<string> ScaleNames { get; } =
        ["Chromatic", "Major", "Minor", "Harmonic minor", "Major pentatonic", "Minor pentatonic", "Blues", "Dorian", "Mixolydian"];

    public static double FrequencyToNote(double frequency, double referenceA4 = 440) =>
        69 + 12 * Math.Log2(frequency / referenceA4);

    public static double NoteToFrequency(double note, double referenceA4 = 440) =>
        referenceA4 * Math.Pow(2, (note - 69) / 12);

    /// <summary>Pitch class from 0 (C) to 11 (B).</summary>
    public static int PitchClass(int note) => ((note % 12) + 12) % 12;

    /// <summary>Name with octave, e.g. "A4" or "C#3".</summary>
    public static string NoteName(int note) => NoteNames[PitchClass(note)] + (int)Math.Floor(note / 12.0 - 1);

    /// <summary>Bit mask of the pitch classes in a scale (bit 0 = C).</summary>
    public static int ScaleMask(ScaleType scale, int root)
    {
        int[] steps = scale switch
        {
            ScaleType.Chromatic => [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11],
            ScaleType.Major => [0, 2, 4, 5, 7, 9, 11],
            ScaleType.NaturalMinor => [0, 2, 3, 5, 7, 8, 10],
            ScaleType.HarmonicMinor => [0, 2, 3, 5, 7, 8, 11],
            ScaleType.MajorPentatonic => [0, 2, 4, 7, 9],
            ScaleType.MinorPentatonic => [0, 3, 5, 7, 10],
            ScaleType.Blues => [0, 3, 5, 6, 7, 10],
            ScaleType.Dorian => [0, 2, 3, 5, 7, 9, 10],
            ScaleType.Mixolydian => [0, 2, 4, 5, 7, 9, 10],
            _ => throw new ArgumentOutOfRangeException(nameof(scale), scale, null),
        };

        int mask = 0;
        foreach (int step in steps)
        {
            mask |= 1 << PitchClass(root + step);
        }

        return mask;
    }

    public static bool IsInScale(int note, int mask) => (mask & (1 << PitchClass(note))) != 0;

    /// <summary>
    /// The scale note closest to <paramref name="note"/>. To avoid flickering between two notes when
    /// the voice sits halfway between them, <paramref name="current"/> is kept unless another note is
    /// closer by more than <paramref name="hysteresis"/> semitones.
    /// </summary>
    public static int Nearest(double note, int mask, int? current = null, double hysteresis = 0)
    {
        if ((mask & 0xFFF) == 0)
        {
            throw new ArgumentException("The scale must contain at least one note.", nameof(mask));
        }

        int centre = (int)Math.Round(note);
        int best = centre;
        double bestDistance = double.MaxValue;
        for (int candidate = centre - 6; candidate <= centre + 6; candidate++)
        {
            double distance = Math.Abs(candidate - note);
            if (IsInScale(candidate, mask) && distance < bestDistance)
            {
                best = candidate;
                bestDistance = distance;
            }
        }

        if (current is int held && held != best && IsInScale(held, mask) &&
            Math.Abs(held - note) <= bestDistance + hysteresis)
        {
            return held;
        }

        return best;
    }
}
