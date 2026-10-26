using VoiceChanger.Core.Dsp;
using VoiceChanger.Core.Effects;
using static VoiceChanger.Core.Tests.SignalHelpers;

namespace VoiceChanger.Core.Tests;

public class FormantTests
{
    private const int SampleRate = 48000;

    /// <summary>A vowel-like spectral envelope: three resonances (700, 1200, 2600 Hz) over a floor.</summary>
    private static double Envelope(double frequency)
    {
        double[] centres = [700, 1200, 2600];
        double[] widths = [250, 300, 400];
        double[] gains = [1, 0.6, 0.3];
        double value = 0.02;
        for (int i = 0; i < 3; i++)
        {
            value += gains[i] / Math.Sqrt(1 + Math.Pow((frequency - centres[i]) / (widths[i] / 2), 2));
        }

        return value;
    }

    /// <summary>A sung vowel: harmonics of <paramref name="f0"/> shaped by <see cref="Envelope"/>.</summary>
    private static float[] Vowel(double f0, double seconds)
    {
        int frames = (int)(seconds * SampleRate);
        int harmonics = (int)(7500 / f0);
        var signal = new double[frames];
        for (int h = 1; h <= harmonics; h++)
        {
            // Rotating phasor instead of Math.Sin per sample: same tone, far faster.
            double step = 2 * Math.PI * h * f0 / SampleRate;
            double stepCos = Math.Cos(step), stepSin = Math.Sin(step);
            double re = 1, im = 0, gain = 0.05 * Envelope(h * f0);
            for (int i = 0; i < frames; i++)
            {
                signal[i] += gain * im;
                (re, im) = (re * stepCos - im * stepSin, re * stepSin + im * stepCos);
            }
        }

        return signal.Select(v => (float)v).ToArray();
    }

    /// <summary>
    /// RMS difference (dB) between the harmonic levels of a steady tone at <paramref name="f0"/> and
    /// the envelope <paramref name="expected"/>, after removing the overall level difference.
    /// </summary>
    private static double EnvelopeError(float[] output, double f0, Func<double, double> expected)
    {
        const int size = 32768;
        var real = new float[size];
        var imag = new float[size];
        for (int i = 0; i < size; i++)
        {
            real[i] = (float)(output[output.Length - size + i] * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / size)));
        }

        new Fft(size).Forward(real, imag);
        var differences = new List<double>();
        for (int h = 1; h * f0 < 3500; h++)
        {
            double frequency = h * f0;
            if (frequency < 250)
            {
                continue;
            }

            int bin = (int)Math.Round(frequency * size / SampleRate);
            double peak = 0;
            for (int k = bin - 3; k <= bin + 3; k++)
            {
                peak = Math.Max(peak, Math.Sqrt(real[k] * real[k] + imag[k] * imag[k]));
            }

            differences.Add(20 * Math.Log10(peak + 1e-12) - 20 * Math.Log10(expected(frequency)));
        }

        double offset = differences.Average();
        return Math.Sqrt(differences.Average(d => (d - offset) * (d - offset)));
    }

    /// <summary>
    /// Fundamental frequency of the last half second. (The loudest partial of a vowel is the harmonic
    /// nearest a formant, not the fundamental, so a spectral peak would be misleading.)
    /// </summary>
    private static double Pitch(float[] signal)
    {
        var detector = new PitchDetector(SampleRate);
        detector.Process(signal.AsSpan(signal.Length - SampleRate / 2));
        Assert.True(detector.IsVoiced);
        return detector.Frequency;
    }

    private static float[] RunVocoder(float[] signal, double ratio, bool preserve, double formantRatio = 1)
    {
        var vocoder = new PhaseVocoder(2048, SampleRate) { Ratio = ratio, PreserveFormants = preserve, FormantRatio = formantRatio };
        ProcessInBlocks(signal, 1, 480, block => vocoder.Process(block.Span, 0, 1));
        return signal;
    }

    [Theory]
    [InlineData(110, 12)]
    [InlineData(160, -12)]
    [InlineData(230, 12)]
    [InlineData(230, -7)]
    [InlineData(160, 24)]
    public void Preserving_formants_keeps_the_envelope_in_place(double f0, int semitones)
    {
        double ratio = Math.Pow(2, semitones / 12.0);

        var shifted = RunVocoder(Vowel(f0, 2), ratio, preserve: false);
        var preserved = RunVocoder(Vowel(f0, 2), ratio, preserve: true);

        // Without preservation the envelope moves with the pitch...
        Assert.InRange(EnvelopeError(shifted, f0 * ratio, f => Envelope(f / ratio)), 0, 1);

        // ...with it, the harmonics follow the original envelope instead.
        double kept = EnvelopeError(preserved, f0 * ratio, Envelope);
        double moved = EnvelopeError(preserved, f0 * ratio, f => Envelope(f / ratio));
        Assert.InRange(kept, 0, 2.5);
        Assert.True(moved > kept + 2, $"kept {kept:0.0} dB, moved {moved:0.0} dB");

        // The pitch itself is still shifted.
        Assert.Equal(f0 * ratio, Pitch(preserved), f0 * ratio * 0.01);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(-4)]
    public void Formant_shift_moves_the_envelope_but_not_the_pitch(double formantSemitones)
    {
        double formantRatio = Math.Pow(2, formantSemitones / 12);
        var output = RunVocoder(Vowel(160, 2), 1, preserve: true, formantRatio);

        Assert.InRange(EnvelopeError(output, 160, f => Envelope(f / formantRatio)), 0, 2);
        Assert.True(EnvelopeError(output, 160, Envelope) > 2.5);
        Assert.Equal(160, Pitch(output), 1.6);
    }

    [Fact]
    public void Preserving_formants_keeps_a_sensible_level()
    {
        var input = Vowel(160, 2);
        double inputLevel = Rms(input.AsSpan(SampleRate));
        foreach (int semitones in new[] { 12, -12 })
        {
            var output = RunVocoder((float[])input.Clone(), Math.Pow(2, semitones / 12.0), preserve: true);
            Assert.InRange(Rms(output.AsSpan(SampleRate)) / inputLevel, 0.5, 2);
        }
    }

    [Fact]
    public void Effect_switches_to_the_phase_vocoder_when_preserving_formants()
    {
        var effect = new PitchShiftEffect();
        effect.Semitones.Value = 12;
        effect.Prepare(SampleRate, 1);
        Assert.Equal(PitchShiftEffect.GranularMode, effect.EffectiveMode);
        int granularLatency = effect.LatencyFrames;

        effect.PreserveFormants.Value = 1;
        Assert.Equal(PitchShiftEffect.PhaseVocoderMode, effect.EffectiveMode);
        Assert.Equal(PhaseVocoder.LatencyFor(2048), effect.LatencyFrames);
        Assert.True(effect.LatencyFrames > granularLatency);

        // The algorithm choice no longer applies, the formant shift now does.
        Assert.False(effect.Mode.IsAvailable);
        Assert.True(effect.FormantShift.IsAvailable);
        effect.PreserveFormants.Value = 0;
        Assert.True(effect.Mode.IsAvailable);
        Assert.False(effect.FormantShift.IsAvailable);
    }

    [Fact]
    public void Effect_applies_a_formant_shift_without_any_pitch_change()
    {
        var effect = new PitchShiftEffect();
        effect.PreserveFormants.Value = 1;
        effect.FormantShift.Value = 4;
        effect.Prepare(SampleRate, 1);
        Assert.True(effect.IsShifting);

        var signal = Vowel(160, 2);
        ProcessInBlocks(signal, 1, 480, block => effect.Process(block.Span));
        double formantRatio = Math.Pow(2, 4 / 12.0);
        Assert.InRange(EnvelopeError(signal, 160, f => Envelope(f / formantRatio)), 0, 2);

        // With no formant shift and no pitch change it stays a perfect pass-through.
        var idle = new PitchShiftEffect();
        idle.PreserveFormants.Value = 1;
        idle.Prepare(SampleRate, 1);
        var tone = Sine(220, SampleRate, 4800);
        var copy = (float[])tone.Clone();
        idle.Process(tone);
        Assert.Equal(copy, tone);
        Assert.Equal(0, idle.LatencyFrames);
    }

    [Fact]
    public void Natural_presets_turn_formant_preservation_on()
    {
        var effect = new PitchShiftEffect();
        effect.ApplyPreset(effect.Presets.Single(p => p.Name == "Natural octave up"));
        Assert.Equal(12, effect.Semitones.Value);
        Assert.True(effect.PreserveFormants.BoolValue);

        effect.ApplyPreset(effect.Presets.Single(p => p.Name == "Chipmunk"));
        Assert.False(effect.PreserveFormants.BoolValue);

        effect.ApplyPreset(effect.Presets.Single(p => p.Name == "Bigger voice"));
        Assert.Equal(-3, effect.FormantShift.Value);
        Assert.Equal("Bigger voice", effect.FindMatchingPreset()?.Name);
    }
}
