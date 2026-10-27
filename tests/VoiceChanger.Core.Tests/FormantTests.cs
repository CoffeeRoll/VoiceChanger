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

    /// <summary>A plain pitch shift: the formants move with the pitch.</summary>
    private static float[] RunVocoder(float[] signal, double ratio)
    {
        var vocoder = new PhaseVocoder(2048, SampleRate) { Ratio = ratio };
        ProcessInBlocks(signal, 1, 480, block => vocoder.Process(block.Span, 0, 1));
        return signal;
    }

    /// <summary>A formant-preserving pitch shift, optionally with a formant shift of its own.</summary>
    private static float[] RunVoice(float[] signal, double ratio, double formantRatio = 1, VoiceShiftMethod method = VoiceShiftMethod.Pulse)
    {
        var shifter = VoiceShifter.Create(method, 1, SampleRate);
        shifter.Ratio = ratio;
        shifter.FormantRatio = formantRatio;
        ProcessInBlocks(signal, 1, 480, block => shifter.Process(block.Span));
        return signal;
    }

    private static float[] RunPsola(float[] signal, double ratio, double formantRatio = 1) => RunVoice(signal, ratio, formantRatio);

    [Theory]
    [InlineData(110, 12, VoiceShiftMethod.Pulse)]
    [InlineData(160, -12, VoiceShiftMethod.Pulse)]
    [InlineData(230, 12, VoiceShiftMethod.Pulse)]
    [InlineData(230, -7, VoiceShiftMethod.Pulse)]
    [InlineData(160, 24, VoiceShiftMethod.Pulse)]
    [InlineData(110, 12, VoiceShiftMethod.Spectral)]
    [InlineData(160, -12, VoiceShiftMethod.Spectral)]
    [InlineData(230, 12, VoiceShiftMethod.Spectral)]
    [InlineData(230, -7, VoiceShiftMethod.Spectral)]
    [InlineData(160, 24, VoiceShiftMethod.Spectral)]
    public void Preserving_formants_keeps_the_envelope_in_place(double f0, int semitones, VoiceShiftMethod method)
    {
        double ratio = Math.Pow(2, semitones / 12.0);

        var shifted = RunVocoder(Vowel(f0, 2), ratio);
        var preserved = RunVoice(Vowel(f0, 2), ratio, method: method);

        // Without preservation the envelope moves with the pitch...
        Assert.InRange(EnvelopeError(shifted, f0 * ratio, f => Envelope(f / ratio)), 0, 1);

        // ...with it, the harmonics follow the original envelope instead.
        double kept = EnvelopeError(preserved, f0 * ratio, Envelope);
        double moved = EnvelopeError(preserved, f0 * ratio, f => Envelope(f / ratio));
        // Whole octaves are almost exact (about 0.5 dB); between them neighbouring grains overlap by
        // part of a period, which colours the envelope a little (about 3 dB for a high voice at -7).
        Assert.InRange(kept, 0, 3.5);
        Assert.True(moved > kept + 2, $"kept {kept:0.0} dB, moved {moved:0.0} dB");

        // The pitch itself is still shifted.
        Assert.Equal(f0 * ratio, Pitch(preserved), f0 * ratio * 0.01);
    }

    [Theory]
    [InlineData(440, 3)]
    [InlineData(440, -5)]
    [InlineData(330, 12)]
    [InlineData(130, -12)]
    public void Pitch_comes_out_exact_and_steady(double f0, int semitones)
    {
        // Marks and grains are placed with sub-sample precision: even a high voice, whose periods
        // are only about a hundred samples long, comes out without period-to-period jitter.
        double expected = f0 * Math.Pow(2, semitones / 12.0);
        var output = RunPsola(Vowel(f0, 2), Math.Pow(2, semitones / 12.0));

        var detector = new PitchDetector(SampleRate, windowSeconds: 0.02, hopSeconds: 0.0025);
        var cents = new List<double>();
        for (int offset = SampleRate / 2; offset < output.Length; offset += 120)
        {
            if (detector.Process(output.AsSpan(offset, 120)) && detector.IsVoiced)
            {
                cents.Add(1200 * Math.Log2(detector.Frequency / expected));
            }
        }

        Assert.True(cents.Count > 300);
        Assert.InRange(cents.Average(), -2, 2);
        Assert.InRange(cents.Max() - cents.Min(), 0, 6);
    }

    [Theory]
    [InlineData(4, VoiceShiftMethod.Pulse)]
    [InlineData(-4, VoiceShiftMethod.Pulse)]
    [InlineData(4, VoiceShiftMethod.Spectral)]
    [InlineData(-4, VoiceShiftMethod.Spectral)]
    public void Formant_shift_moves_the_envelope_but_not_the_pitch(double formantSemitones, VoiceShiftMethod method)
    {
        double formantRatio = Math.Pow(2, formantSemitones / 12);
        var output = RunVoice(Vowel(160, 2), 1, formantRatio, method);

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
            var output = RunPsola((float[])input.Clone(), Math.Pow(2, semitones / 12.0));
            Assert.InRange(Rms(output.AsSpan(SampleRate)) / inputLevel, 0.5, 2);
        }
    }

    [Theory]
    [InlineData(110, -12)]
    [InlineData(110, -7)]
    [InlineData(210, -12)]
    [InlineData(210, -5)]
    public void A_breathy_voice_keeps_its_clarity_when_shifted_down(double f0, int semitones)
    {
        // Real voices have weak, noisy harmonics at the top. A spectral method has to rebuild the
        // upper formants of a lower voice from exactly that region, which used to turn them into
        // breath noise (or leave them out). Moving pulses instead keeps them as clear as in a real
        // voice at the lower pitch.
        double ratio = Math.Pow(2, semitones / 12.0);
        var input = SyntheticVoice.Generate(3, t => SyntheticVoice.Speech(t, f0), SyntheticVoice.Vowels);
        var ideal = SyntheticVoice.Generate(3, t => SyntheticVoice.Speech(t, f0) * ratio, SyntheticVoice.Vowels);
        var output = RunPsola((float[])input.Clone(), ratio);

        // The upper formants (1.5 to 4 kHz) keep their share of the sound...
        double expected = SyntheticVoice.BandShareDb(ideal, 1500, 4000);
        Assert.InRange(SyntheticVoice.BandShareDb(output, 1500, 4000), expected - 3, expected + 3);

        // ...and are as harmonic as in a real voice at that pitch, overall and above 1 kHz.
        Assert.True(SyntheticVoice.Periodicity(output) > SyntheticVoice.Periodicity(ideal) - 1.5);
        Assert.True(SyntheticVoice.Periodicity(output, 1000) > SyntheticVoice.Periodicity(ideal, 1000) - 1.5);
    }

    [Fact]
    public void Breath_and_silence_pass_through_unchanged()
    {
        // Noise has no pulses to move, so it comes out as it went in (just delayed).
        var noise = new float[SampleRate];
        var random = new Random(5);
        for (int i = 0; i < noise.Length; i++)
        {
            noise[i] = (float)(0.1 * (random.NextDouble() * 2 - 1));
        }

        var shifter = new PsolaPitchShifter(1, SampleRate) { Ratio = 0.5 };
        var output = (float[])noise.Clone();
        ProcessInBlocks(output, 1, 480, block => shifter.Process(block.Span));

        int delay = shifter.LatencyFrames;
        double error = 0, energy = 0;
        for (int i = SampleRate / 4; i < noise.Length; i++)
        {
            double difference = output[i] - noise[i - delay];
            error += difference * difference;
            energy += noise[i - delay] * noise[i - delay];
        }

        Assert.True(error < energy * 1e-6, $"error {10 * Math.Log10(error / energy):0} dB");
    }

    [Fact]
    public void Effect_switches_to_the_voice_algorithm_when_preserving_formants()
    {
        var effect = new PitchShiftEffect();
        effect.Semitones.Value = -12;
        effect.Prepare(SampleRate, 1);
        Assert.Equal(PitchShiftEffect.GranularMode, effect.EffectiveMode);
        int granularLatency = effect.LatencyFrames;

        effect.PreserveFormants.Value = 1;
        Assert.Equal(PitchShiftEffect.VoicePulseMode, effect.EffectiveMode);
        Assert.Equal(new PsolaPitchShifter(1, SampleRate).LatencyFrames, effect.LatencyFrames);
        Assert.InRange(effect.LatencyFrames * 1000.0 / SampleRate, 25, 40);
        Assert.True(effect.LatencyFrames > granularLatency);

        // The algorithm choice and window no longer apply, the voice method and formant shift now do.
        Assert.False(effect.Mode.IsAvailable);
        Assert.False(effect.WindowMilliseconds.IsAvailable);
        Assert.True(effect.FormantShift.IsAvailable);
        Assert.True(effect.VoiceMethod.IsAvailable);

        effect.VoiceMethod.Value = (int)VoiceShiftMethod.Spectral;
        Assert.Equal(PitchShiftEffect.VoiceSpectralMode, effect.EffectiveMode);
        Assert.Equal(new SpectralVoiceShifter(1, SampleRate).LatencyFrames, effect.LatencyFrames);
        Assert.InRange(effect.LatencyFrames * 1000.0 / SampleRate, 30, 45);

        effect.PreserveFormants.Value = 0;
        Assert.Equal(PitchShiftEffect.GranularMode, effect.EffectiveMode);
        Assert.True(effect.Mode.IsAvailable);
        Assert.True(effect.WindowMilliseconds.IsAvailable);
        Assert.False(effect.FormantShift.IsAvailable);
        Assert.False(effect.VoiceMethod.IsAvailable);
    }

    [Theory]
    [InlineData(VoiceShiftMethod.Pulse)]
    [InlineData(VoiceShiftMethod.Spectral)]
    public void Effect_shifts_with_either_voice_method(VoiceShiftMethod method)
    {
        var effect = new PitchShiftEffect();
        effect.Semitones.Value = 7;
        effect.PreserveFormants.Value = 1;
        effect.VoiceMethod.Value = (int)method;
        effect.Prepare(SampleRate, 2);

        var signal = new float[SampleRate * 2 * 2];
        var vowel = Vowel(160, 2);
        for (int i = 0; i < vowel.Length; i++)
        {
            signal[2 * i] = signal[2 * i + 1] = vowel[i];
        }

        ProcessInBlocks(signal, 2, 480, block => effect.Process(block.Span));
        var left = Channel(signal, 2, 0);
        Assert.Equal(left, Channel(signal, 2, 1));
        Assert.Equal(160 * Math.Pow(2, 7 / 12.0), Pitch(left), 160 * Math.Pow(2, 7 / 12.0) * 0.01);
        Assert.InRange(EnvelopeError(left, 160 * Math.Pow(2, 7 / 12.0), Envelope), 0, 3.5);
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

        // Going up sounds smoothest rebuilt from the spectrum, going down clearest moving pulses.
        Assert.Equal((int)VoiceShiftMethod.Spectral, effect.VoiceMethod.IntValue);
        effect.ApplyPreset(effect.Presets.Single(p => p.Name == "Smaller voice"));
        Assert.Equal((int)VoiceShiftMethod.Spectral, effect.VoiceMethod.IntValue);

        effect.ApplyPreset(effect.Presets.Single(p => p.Name == "Chipmunk"));
        Assert.False(effect.PreserveFormants.BoolValue);

        effect.ApplyPreset(effect.Presets.Single(p => p.Name == "Bigger voice"));
        Assert.Equal(-3, effect.FormantShift.Value);
        Assert.Equal("Bigger voice", effect.FindMatchingPreset()?.Name);

        // Voices that are simply lower keep sounding like a person, not a slowed-down recording.
        foreach (string name in new[] { "Lower voice", "Deep", "Natural octave down", "Bigger voice" })
        {
            effect.ApplyPreset(effect.Presets.Single(p => p.Name == name));
            Assert.True(effect.Semitones.Value < 0);
            Assert.True(effect.PreserveFormants.BoolValue, name);
            Assert.Equal((int)VoiceShiftMethod.Pulse, effect.VoiceMethod.IntValue);
        }

        effect.ApplyPreset(effect.Presets.Single(p => p.Name == "Monster"));
        Assert.False(effect.PreserveFormants.BoolValue);
    }
}
