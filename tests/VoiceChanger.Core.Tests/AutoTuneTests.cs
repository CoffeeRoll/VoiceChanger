using VoiceChanger.Core.Dsp;
using VoiceChanger.Core.Effects;
using static VoiceChanger.Core.Tests.SignalHelpers;

namespace VoiceChanger.Core.Tests;

public class SplicingPitchShifterTests
{
    [Theory]
    [InlineData(30)]
    [InlineData(-50)]
    [InlineData(200)]
    [InlineData(-1200)]
    [InlineData(1200)]
    public void Shifts_by_the_requested_ratio_without_changing_level(double cents)
    {
        var shifter = new SplicingPitchShifter(1, 48000) { Ratio = Math.Pow(2, cents / 1200) };
        var signal = PitchDetectorTests.Voice(150, 48000, 96000);
        double inputLevel = Rms(signal.AsSpan(48000));
        ProcessInBlocks(signal, 1, 240, block => shifter.Process(block.Span));

        double expected = 150 * Math.Pow(2, cents / 1200);
        Assert.Equal(expected, DominantFrequency(signal.AsSpan(48000), 48000), expected * 0.003);
        Assert.Equal(1, Rms(signal.AsSpan(48000)) / inputLevel, 0.02);
    }

    [Fact]
    public void Ratio_of_one_is_a_pure_delay()
    {
        var shifter = new SplicingPitchShifter(2, 48000);
        var random = new Random(9);
        var signal = Enumerable.Range(0, 20000).Select(_ => (float)(random.NextDouble() - 0.5)).ToArray();
        var copy = (float[])signal.Clone();
        shifter.Process(signal);

        int delay = shifter.LatencyFrames * 2;
        for (int i = delay; i < signal.Length; i++)
        {
            Assert.Equal(copy[i - delay], signal[i], 5);
        }
    }

    [Fact]
    public void Handles_ratios_that_keep_crossing_one()
    {
        var shifter = new SplicingPitchShifter(1, 48000);
        var signal = PitchDetectorTests.Voice(200, 48000, 96000);
        double inputLevel = Rms(signal);
        for (int offset = 0, block = 0; offset < signal.Length; offset += 240, block++)
        {
            shifter.Ratio = block / 20 % 2 == 0 ? 1.03 : 0.97;
            shifter.Process(signal.AsSpan(offset, 240));
        }

        Assert.Equal(1, Rms(signal.AsSpan(24000)) / inputLevel, 0.03);
        Assert.All(signal, s => Assert.InRange(s, -0.6f, 0.6f));
    }

    [Fact]
    public void Extra_delay_adds_to_the_latency()
    {
        var plain = new SplicingPitchShifter(1, 48000);
        var delayed = new SplicingPitchShifter(1, 48000, extraDelaySeconds: 0.01);
        Assert.Equal(plain.LatencyFrames + 480, delayed.LatencyFrames);
    }
}

public class PartialPresetTests
{
    [Fact]
    public void Partial_presets_keep_unlisted_settings()
    {
        var autoTune = new AutoTuneEffect();
        autoTune.Key.Value = 9;
        autoTune.Scale.Value = (int)ScaleType.NaturalMinor;

        autoTune.ApplyPreset(autoTune.Presets.Single(p => p.Name == "Robot (hard tune)"));

        Assert.Equal(0, autoTune.RetuneSpeed.Value);
        Assert.Equal(9, autoTune.Key.Value);
        Assert.Equal((int)ScaleType.NaturalMinor, autoTune.Scale.Value);
        Assert.Equal("Robot (hard tune)", autoTune.FindMatchingPreset()?.Name);
    }

    [Fact]
    public void Full_presets_still_reset_unlisted_settings()
    {
        var eq = new EqualizerEffect();
        eq.Bands[0].Value = 5;
        eq.ApplyPreset(eq.Presets.Single(p => p.Name == "Treble boost"));
        Assert.Equal(0, eq.Bands[0].Value);
        Assert.Equal("Treble boost", eq.FindMatchingPreset()?.Name);

        eq.Bands[0].Value = 1;
        Assert.Null(eq.FindMatchingPreset());
    }
}

public class AutoTuneEffectTests
{
    private const int SampleRate = 48000;

    private static (AutoTuneEffect Effect, float[] Output) Run(Func<double, double> frequencyAt, double seconds, Action<AutoTuneEffect>? setup = null, int channels = 1)
    {
        var effect = new AutoTuneEffect();
        setup?.Invoke(effect);
        effect.Prepare(SampleRate, channels);

        int frames = (int)(SampleRate * seconds);
        var signal = new float[frames * channels];
        double phase = 0;
        for (int i = 0; i < frames; i++)
        {
            phase += 2 * Math.PI * frequencyAt((double)i / SampleRate) / SampleRate;
            double value = 0;
            for (int h = 1; h <= 10; h++)
            {
                value += Math.Sin(h * phase) / h;
            }

            for (int ch = 0; ch < channels; ch++)
            {
                signal[i * channels + ch] = (float)(0.2 * value);
            }
        }

        ProcessInBlocks(signal, channels, 480, block => effect.Process(block.Span));
        return (effect, signal);
    }

    private static double Cents(double frequency, double reference) => 1200 * Math.Log2(frequency / reference);

    [Theory]
    [InlineData(452, 440)] // A4 sung 47 cents sharp
    [InlineData(428, 440)] // A4 sung 48 cents flat
    [InlineData(103, 103.83)] // low male voice, G#2
    public void Chromatic_mode_pulls_to_the_nearest_semitone(double sung, double expected)
    {
        var (effect, output) = Run(_ => sung, 1.2);
        Assert.InRange(Cents(DominantFrequency(output.AsSpan(SampleRate / 2), SampleRate), expected), -3, 3);

        var reading = effect.Reading;
        Assert.True(reading.IsVoiced);
        Assert.InRange(Cents(reading.InputFrequency, sung), -3, 3);
        Assert.Equal((int)Math.Round(MusicalScale.FrequencyToNote(expected)), reading.TargetNote);
        Assert.Equal(reading.TargetNote, reading.OutputNote, 0.05);
    }

    [Theory]
    [InlineData(0, ScaleType.Major, 270, 261.63)]          // C4 + 55 cents → C (C# not in C major)
    [InlineData(0, ScaleType.Major, 285, 293.66)]          // C#4 + 48 cents → D
    [InlineData(9, ScaleType.MinorPentatonic, 300, 293.66)] // A minor pentatonic has D, not D#/E
    public void Snaps_to_notes_of_the_key_and_scale(int key, ScaleType scale, double sung, double expected)
    {
        var (_, output) = Run(_ => sung, 1.2, e =>
        {
            e.Key.Value = key;
            e.Scale.Value = (int)scale;
        });
        Assert.InRange(Cents(DominantFrequency(output.AsSpan(SampleRate / 2), SampleRate), expected), -3, 3);
    }

    [Fact]
    public void Amount_transpose_and_reference_change_the_target()
    {
        var (_, none) = Run(_ => 452, 1.2, e => e.Amount.Value = 0);
        Assert.InRange(Cents(DominantFrequency(none.AsSpan(SampleRate / 2), SampleRate), 452), -3, 3);

        var (_, half) = Run(_ => 452, 1.2, e => e.Amount.Value = 50);
        Assert.InRange(Cents(DominantFrequency(half.AsSpan(SampleRate / 2), SampleRate), 452), -27, -20);

        var (transposed, octave) = Run(_ => 452, 1.2, e => e.Transpose.Value = 12);
        Assert.InRange(Cents(DominantFrequency(octave.AsSpan(SampleRate / 2), SampleRate), 880), -3, 3);
        Assert.Equal(81, transposed.Reading.TargetNote);

        var (_, baroque) = Run(_ => 440, 1.2, e => e.Reference.Value = 432);
        Assert.InRange(Cents(DominantFrequency(baroque.AsSpan(SampleRate / 2), SampleRate), 432), -3, 3);
    }

    [Fact]
    public void Instant_retune_flattens_vibrato_and_slow_retune_keeps_it()
    {
        static double ResidualVibrato(float[] output)
        {
            var detector = new PitchDetector(SampleRate, windowSeconds: 0.02, hopSeconds: 0.0025);
            var cents = new List<double>();
            for (int offset = SampleRate / 2; offset < output.Length; offset += 120)
            {
                if (detector.Process(output.AsSpan(offset, 120)) && detector.IsVoiced && offset > SampleRate)
                {
                    cents.Add(Cents(detector.Frequency, 220));
                }
            }

            return (cents.Max() - cents.Min()) / 2;
        }

        // A3 with a ±40 cent, 5 Hz vibrato.
        static double Vibrato(double t) => 220 * Math.Pow(2, 40 / 1200.0 * Math.Sin(2 * Math.PI * 5 * t));

        var (_, hard) = Run(Vibrato, 2, e => e.RetuneSpeed.Value = 0);
        var (_, natural) = Run(Vibrato, 2, e => e.RetuneSpeed.Value = 200);

        Assert.InRange(ResidualVibrato(hard), 0, 6);
        Assert.InRange(ResidualVibrato(natural), 30, 50);
    }

    [Fact]
    public void Retune_speed_controls_how_fast_a_new_note_is_reached()
    {
        // Sing A4 + 40 cents, then the correction has to swing from ~0 to -40 cents.
        var (fast, _) = Run(_ => 450.3, 0.12, e => e.RetuneSpeed.Value = 0);
        var (slow, _) = Run(_ => 450.3, 0.12, e => e.RetuneSpeed.Value = 300);

        Assert.Equal(69, fast.Reading.OutputNote, 0.05);
        Assert.InRange(slow.Reading.OutputNote, 69.15, 69.4);
    }

    [Fact]
    public void Silence_and_noise_pass_without_correction()
    {
        var effect = new AutoTuneEffect();
        effect.Prepare(SampleRate, 1);
        var random = new Random(4);
        var noise = Enumerable.Range(0, SampleRate).Select(_ => (float)(random.NextDouble() - 0.5) * 0.2f).ToArray();
        double inputLevel = Rms(noise);
        ProcessInBlocks(noise, 1, 480, block => effect.Process(block.Span));

        var reading = effect.Reading;
        Assert.False(reading.IsVoiced);
        Assert.True(double.IsNaN(reading.InputNote));
        Assert.Equal(1, Rms(noise.AsSpan(4800)) / inputLevel, 0.05);
    }

    [Fact]
    public void Works_on_stereo_input()
    {
        var (effect, output) = Run(_ => 452, 1.2, channels: 2);
        var left = Channel(output, 2, 0);
        var right = Channel(output, 2, 1);
        Assert.InRange(Cents(DominantFrequency(left.AsSpan(SampleRate / 2), SampleRate), 440), -3, 3);
        Assert.Equal(left, right);
        Assert.True(effect.Reading.IsVoiced);
    }

    [Fact]
    public void Reading_sequence_advances_while_processing()
    {
        var effect = new AutoTuneEffect();
        Assert.Equal(0, effect.Reading.Sequence);
        effect.Prepare(SampleRate, 1);
        effect.Process(new float[480]);
        effect.Process(new float[480]);
        Assert.Equal(2, effect.Reading.Sequence);
        Assert.Equal(effect.GetParameter("reference").Value, effect.Reading.ReferenceA4);
    }

    [Fact]
    public async Task Reading_is_never_torn_while_the_audio_thread_writes()
    {
        var effect = new AutoTuneEffect();
        effect.Prepare(SampleRate, 1);
        var block = PitchDetectorTests.Voice(330, SampleRate, 480);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        var writer = Task.Run(() =>
        {
            while (!cancellation.IsCancellationRequested)
            {
                effect.Process((float[])block.Clone());
            }
        }, TestContext.Current.CancellationToken);

        while (!cancellation.IsCancellationRequested)
        {
            var reading = effect.Reading;

            // Voiced readings always carry a consistent set of values.
            if (reading.IsVoiced)
            {
                Assert.False(double.IsNaN(reading.InputNote));
                Assert.True(reading.InputFrequency > 0);
            }
            else
            {
                Assert.True(double.IsNaN(reading.InputNote));
            }
        }

        await writer;
    }
}
