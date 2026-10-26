using VoiceChanger.Core.Dsp;

namespace VoiceChanger.Core.Tests;

public class MusicalScaleTests
{
    [Theory]
    [InlineData(69, "A4")]
    [InlineData(60, "C4")]
    [InlineData(61, "C#4")]
    [InlineData(59, "B3")]
    [InlineData(21, "A0")]
    [InlineData(11, "B-1")]
    public void Names_notes_with_octaves(int note, string expected) =>
        Assert.Equal(expected, MusicalScale.NoteName(note));

    [Theory]
    [InlineData(0, "0 semitones")]
    [InlineData(1, "+1 semitone")]
    [InlineData(-5, "-5 semitones")]
    [InlineData(12, "+1 octave")]
    [InlineData(-12, "-1 octave")]
    [InlineData(15, "+1 octave +3 semitones")]
    [InlineData(-13, "-1 octave -1 semitone")]
    [InlineData(24, "+2 octaves")]
    [InlineData(-23, "-1 octave -11 semitones")]
    public void Describes_intervals_in_octaves_and_semitones(int semitones, string expected) =>
        Assert.Equal(expected, MusicalScale.FormatInterval(semitones));

    [Fact]
    public void Converts_between_frequency_and_note()
    {
        Assert.Equal(69, MusicalScale.FrequencyToNote(440), 9);
        Assert.Equal(60, MusicalScale.FrequencyToNote(261.6256), 4);
        Assert.Equal(69, MusicalScale.FrequencyToNote(432, referenceA4: 432), 9);
        Assert.Equal(880, MusicalScale.NoteToFrequency(81), 6);
        Assert.Equal(69.5, MusicalScale.FrequencyToNote(MusicalScale.NoteToFrequency(69.5)), 9);
    }

    [Fact]
    public void Builds_scale_masks_in_any_key()
    {
        int cMajor = MusicalScale.ScaleMask(ScaleType.Major, 0);
        Assert.Equal(new[] { 60, 62, 64, 65, 67, 69, 71 }, Enumerable.Range(60, 12).Where(n => MusicalScale.IsInScale(n, cMajor)));

        int aMinor = MusicalScale.ScaleMask(ScaleType.NaturalMinor, 9);
        Assert.Equal(cMajor, aMinor);

        int fSharpPentatonic = MusicalScale.ScaleMask(ScaleType.MajorPentatonic, 6);
        Assert.Equal(new[] { 1, 3, 6, 8, 10 }, Enumerable.Range(0, 12).Where(n => MusicalScale.IsInScale(n, fSharpPentatonic)));
        Assert.Equal(0xFFF, MusicalScale.ScaleMask(ScaleType.Chromatic, 5));
    }

    [Fact]
    public void Snaps_to_the_nearest_scale_note()
    {
        int chromatic = MusicalScale.ScaleMask(ScaleType.Chromatic, 0);
        int cMajor = MusicalScale.ScaleMask(ScaleType.Major, 0);

        Assert.Equal(69, MusicalScale.Nearest(69.4, chromatic));
        Assert.Equal(70, MusicalScale.Nearest(69.6, chromatic));
        Assert.Equal(60, MusicalScale.Nearest(60.9, cMajor)); // C# is not in C major; C is closer than D
        Assert.Equal(62, MusicalScale.Nearest(61.1, cMajor));
        Assert.Equal(71, MusicalScale.Nearest(70.6, cMajor)); // between A and B (A# not allowed)
    }

    [Fact]
    public void Hysteresis_keeps_the_current_note_near_the_boundary()
    {
        int chromatic = MusicalScale.ScaleMask(ScaleType.Chromatic, 0);
        Assert.Equal(69, MusicalScale.Nearest(69.6, chromatic, current: 69, hysteresis: 0.25));
        Assert.Equal(70, MusicalScale.Nearest(69.8, chromatic, current: 69, hysteresis: 0.25));

        // A held note that is not in the scale is never kept.
        int cMajor = MusicalScale.ScaleMask(ScaleType.Major, 0);
        Assert.Equal(62, MusicalScale.Nearest(61.6, cMajor, current: 61, hysteresis: 1));
    }
}

public class PitchDetectorTests
{
    /// <summary>A voice-like tone: a fundamental and harmonics falling off at 1/n.</summary>
    internal static float[] Voice(double frequency, int sampleRate, int frames, double fundamentalLevel = 1, double amplitude = 0.2)
    {
        var buffer = new float[frames];
        for (int i = 0; i < frames; i++)
        {
            double phase = 2 * Math.PI * frequency * i / sampleRate;
            double value = 0;
            for (int h = 1; h <= 12 && h * frequency < sampleRate / 2.2; h++)
            {
                value += (h == 1 ? fundamentalLevel : 1) * Math.Sin(h * phase) / h;
            }

            buffer[i] = (float)(amplitude * value);
        }

        return buffer;
    }

    private static List<double> Detect(PitchDetector detector, float[] signal, int skipFrames)
    {
        var estimates = new List<double>();
        for (int offset = 0; offset < signal.Length; offset += 256)
        {
            var block = signal.AsSpan(offset, Math.Min(256, signal.Length - offset));
            if (detector.Process(block) && offset >= skipFrames)
            {
                estimates.Add(detector.Frequency);
            }
        }

        return estimates;
    }

    [Theory]
    [InlineData(48000, 82.41)]
    [InlineData(48000, 146.8)]
    [InlineData(48000, 220)]
    [InlineData(48000, 440)]
    [InlineData(44100, 329.6)]
    [InlineData(44100, 98)]
    [InlineData(16000, 196)]
    [InlineData(96000, 261.6)]
    public void Detects_voice_pitch_within_a_few_cents(int sampleRate, double frequency)
    {
        var detector = new PitchDetector(sampleRate);
        var estimates = Detect(detector, Voice(frequency, sampleRate, sampleRate / 2), sampleRate / 10);

        Assert.NotEmpty(estimates);
        Assert.All(estimates, f => Assert.InRange(1200 * Math.Log2(f / frequency), -3, 3));
        Assert.True(detector.IsVoiced);
        Assert.True(detector.Confidence > 0.8);
    }

    [Theory]
    [InlineData(110)]
    [InlineData(180)]
    public void Finds_the_fundamental_even_when_it_is_weak(double frequency)
    {
        var detector = new PitchDetector(48000);
        var estimates = Detect(detector, Voice(frequency, 48000, 24000, fundamentalLevel: 0.1), 4800);
        Assert.All(estimates, f => Assert.InRange(1200 * Math.Log2(f / frequency), -5, 5));
    }

    [Fact]
    public void Reports_noise_and_silence_as_unvoiced()
    {
        var random = new Random(5);
        var noise = Enumerable.Range(0, 24000).Select(_ => (float)(random.NextDouble() - 0.5) * 0.3f).ToArray();
        Assert.All(Detect(new PitchDetector(48000), noise, 0), f => Assert.Equal(0, f));
        Assert.All(Detect(new PitchDetector(48000), new float[24000], 0), f => Assert.Equal(0, f));

        // A real tone that is too quiet is gated as well.
        Assert.All(Detect(new PitchDetector(48000), Voice(220, 48000, 24000, amplitude: 0.0005), 0), f => Assert.Equal(0, f));
    }

    [Fact]
    public void Follows_a_note_change_quickly()
    {
        var detector = new PitchDetector(48000);
        var signal = Voice(220, 48000, 24000).Concat(Voice(330, 48000, 24000)).ToArray();
        var estimates = Detect(detector, signal, 0);

        // 50 ms after the change the new note is reported.
        int changeIndex = (int)(0.5 / detector.HopSeconds);
        Assert.All(estimates.Skip(changeIndex + 5), f => Assert.InRange(f, 325, 335));
    }

    [Fact]
    public void Reset_forgets_the_previous_pitch()
    {
        var detector = new PitchDetector(48000);
        Detect(detector, Voice(220, 48000, 9600), 0);
        Assert.True(detector.IsVoiced);
        detector.Reset();
        Assert.False(detector.IsVoiced);
        Assert.Equal(0, detector.Frequency);
    }
}
