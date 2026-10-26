using VoiceChanger.Core.Effects;
using static VoiceChanger.Core.Tests.SignalHelpers;

namespace VoiceChanger.Core.Tests;

public class EffectParameterTests
{
    [Fact]
    public void Values_are_clamped_and_rounded()
    {
        var p = EffectParameter.Continuous("gain", "Gain", -12, 12, 0, "dB", decimals: 1);
        p.Value = 20;
        Assert.Equal(12, p.Value);
        p.Value = 3.14159f;
        Assert.Equal(3.1f, p.Value);
        p.Value = float.NaN;
        Assert.Equal(0, p.Value);

        var i = EffectParameter.Integer("st", "Semitones", -24, 24, 0);
        i.Value = 4.6f;
        Assert.Equal(5, i.IntValue);

        var t = EffectParameter.Toggle("on", "On", false);
        t.Value = 0.7f;
        Assert.True(t.BoolValue);
    }

    [Fact]
    public void ValueChanged_fires_only_on_real_changes()
    {
        var p = EffectParameter.Continuous("x", "X", 0, 1, 0.5f, decimals: 2);
        int count = 0;
        p.ValueChanged += (_, _) => count++;
        p.Value = 0.5f;
        p.Value = 0.25f;
        p.Value = 0.25f;
        Assert.Equal(1, count);
    }

    [Fact]
    public void Logarithmic_normalisation_round_trips()
    {
        var p = EffectParameter.Continuous("f", "Freq", 20, 20000, 1000, "Hz", decimals: 0, scale: ParameterScale.Logarithmic);
        Assert.Equal(0.5, p.ToNormalized(632.456f), 3);
        Assert.Equal(632, p.FromNormalized(0.5));
        Assert.Equal(20, p.FromNormalized(-1));
        Assert.Equal(20000, p.FromNormalized(2));
    }

    [Fact]
    public void Choice_formats_its_label()
    {
        var p = EffectParameter.Choice("mode", "Mode", ["A", "B", "C"], defaultIndex: 1);
        Assert.Equal("B", p.FormattedValue);
        p.Value = 2;
        Assert.Equal("C", p.FormattedValue);
    }
}

public class EqualizerTests
{
    [Fact]
    public void Flat_equalizer_is_transparent()
    {
        var eq = new EqualizerEffect();
        eq.Prepare(48000, 2);
        var random = new Random(7);
        var signal = Enumerable.Range(0, 4096).Select(_ => (float)(random.NextDouble() - 0.5)).ToArray();
        var copy = (float[])signal.Clone();

        eq.Process(signal);
        Assert.Equal(copy, signal);
    }

    [Fact]
    public void Band_gain_shows_up_in_response_and_audio()
    {
        var eq = new EqualizerEffect();
        eq.Bands[5].Value = 9; // 1 kHz
        eq.Prepare(48000, 1);

        Span<double> response = stackalloc double[2];
        eq.GetResponse([1000.0, 60.0], response, 48000);
        Assert.Equal(9, response[0], 1);
        Assert.InRange(response[1], -0.5, 0.5);

        var signal = Sine(1000, 48000, 48000, amplitude: 0.1);
        eq.Process(signal);
        double gainDb = 20 * Math.Log10(Rms(signal.AsSpan(24000)) / (0.1 / Math.Sqrt(2)));
        Assert.Equal(9, gainDb, 0);
    }

    [Fact]
    public void Parameter_changes_apply_on_the_next_block()
    {
        var eq = new EqualizerEffect();
        eq.Prepare(48000, 1);

        eq.OutputGain.Value = -6;
        var signal = Enumerable.Repeat(0.5f, 64).ToArray();
        eq.Process(signal);
        Assert.Equal(0.5 * Math.Pow(10, -6 / 20.0), signal[10], 4);
    }

    [Fact]
    public void Telephone_preset_cuts_lows_and_highs()
    {
        var eq = new EqualizerEffect();
        eq.ApplyPreset(eq.Presets.Single(p => p.Name == "Telephone"));

        Span<double> response = stackalloc double[3];
        eq.GetResponse([80.0, 1500.0, 10000.0], response, 48000);
        Assert.True(response[0] < -15, $"80 Hz: {response[0]:0.0} dB");
        Assert.True(response[1] > 3, $"1.5 kHz: {response[1]:0.0} dB");
        Assert.True(response[2] < -15, $"10 kHz: {response[2]:0.0} dB");
    }

    [Fact]
    public void Bands_above_nyquist_are_ignored_at_low_sample_rates()
    {
        var eq = new EqualizerEffect();
        foreach (var band in eq.Bands)
        {
            band.Value = 12;
        }

        eq.Prepare(16000, 1);
        var signal = Sine(300, 16000, 16000, amplitude: 0.01);
        eq.Process(signal);
        Assert.All(signal, s => Assert.True(float.IsFinite(s)));
    }

    [Fact]
    public void Applying_preset_resets_unlisted_parameters()
    {
        var eq = new EqualizerEffect();
        eq.Bands[0].Value = 5;
        eq.ApplyPreset(eq.Presets.Single(p => p.Name == "Treble boost"));
        Assert.Equal(0, eq.Bands[0].Value);
        Assert.Equal(6, eq.Bands[9].Value);
    }
}

public class PitchShiftTests
{
    public static TheoryData<int, int, double> Shifts => new()
    {
        { PitchShiftEffect.GranularMode, 12, 2.0 },
        { PitchShiftEffect.GranularMode, -12, 0.5 },
        { PitchShiftEffect.GranularMode, 7, Math.Pow(2, 7 / 12.0) },
        { PitchShiftEffect.PhaseVocoderMode, 12, 2.0 },
        { PitchShiftEffect.PhaseVocoderMode, -12, 0.5 },
        { PitchShiftEffect.PhaseVocoderMode, 5, Math.Pow(2, 5 / 12.0) },
        { PitchShiftEffect.GranularMode, 24, 4.0 },
        { PitchShiftEffect.GranularMode, -24, 0.25 },
        { PitchShiftEffect.PhaseVocoderMode, 24, 4.0 },
        { PitchShiftEffect.PhaseVocoderMode, -24, 0.25 },
    };

    [Theory]
    [MemberData(nameof(Shifts))]
    public void Shifts_a_tone_by_the_requested_interval(int mode, int semitones, double ratio)
    {
        const int sampleRate = 48000;
        var effect = new PitchShiftEffect();
        effect.Mode.Value = mode;
        effect.Semitones.Value = semitones;
        effect.Prepare(sampleRate, 1);

        const double input = 330;
        var signal = Sine(input, sampleRate, sampleRate * 2, amplitude: 0.5);
        ProcessInBlocks(signal, 1, 480, block => effect.Process(block.Span));

        var steady = signal.AsSpan(sampleRate);
        double measured = DominantFrequency(steady, sampleRate);
        Assert.Equal(input * ratio, measured, input * ratio * 0.01);

        double level = Rms(steady) / (0.5 / Math.Sqrt(2));
        Assert.InRange(level, 0.6, 1.4);
    }

    [Theory]
    [InlineData(PitchShiftEffect.GranularMode)]
    [InlineData(PitchShiftEffect.PhaseVocoderMode)]
    public void Zero_shift_passes_the_signal_through(int mode)
    {
        var effect = new PitchShiftEffect();
        effect.Mode.Value = mode;
        effect.Prepare(48000, 2);

        var signal = Sine(440, 48000, 4800, channels: 2);
        var copy = (float[])signal.Clone();
        effect.Process(signal);
        Assert.Equal(copy, signal);
        Assert.Equal(0, effect.LatencyFrames);
    }

    [Fact]
    public void Mix_blends_dry_and_shifted_signal()
    {
        var effect = new PitchShiftEffect();
        effect.Semitones.Value = 12;
        effect.Mix.Value = 50;
        effect.Prepare(48000, 1);

        var signal = Sine(300, 48000, 96000);
        ProcessInBlocks(signal, 1, 512, block => effect.Process(block.Span));

        // Both the original and the octave should be present.
        var steady = signal.AsSpan(48000);
        var real = new float[32768];
        var imag = new float[32768];
        steady[..32768].CopyTo(real);
        new Dsp.Fft(32768).Forward(real, imag);
        double Magnitude(double hz)
        {
            int bin = (int)Math.Round(hz * 32768 / 48000);
            double best = 0;
            for (int k = bin - 2; k <= bin + 2; k++)
            {
                best = Math.Max(best, Math.Sqrt(real[k] * real[k] + imag[k] * imag[k]));
            }

            return best;
        }

        double original = Magnitude(300);
        double octave = Magnitude(600);
        Assert.InRange(octave / original, 0.4, 2.5);
    }

    [Fact]
    public void Survives_noise_and_parameter_changes_while_running()
    {
        var effect = new PitchShiftEffect();
        effect.Prepare(44100, 2);
        var random = new Random(11);
        var block = new float[441 * 2];

        for (int i = 0; i < 400; i++)
        {
            for (int s = 0; s < block.Length; s++)
            {
                block[s] = (float)(random.NextDouble() * 2 - 1);
            }

            if (i % 50 == 0)
            {
                effect.Semitones.Value = random.Next(-24, 25);
                effect.Cents.Value = random.Next(-100, 101);
                effect.WindowMilliseconds.Value = random.Next(10, 101);
                effect.Mode.Value = random.Next(0, 2);
                effect.PreserveFormants.Value = random.Next(0, 2);
                effect.FormantShift.Value = random.Next(-12, 13);
            }

            effect.Process(block);
            Assert.All(block, s => Assert.InRange(s, -4f, 4f));
        }
    }

    [Fact]
    public void Reports_latency_for_each_algorithm()
    {
        var effect = new PitchShiftEffect();
        effect.Semitones.Value = 3;
        effect.WindowMilliseconds.Value = 40;
        effect.Prepare(48000, 1);

        // Half a window plus half of the 12 ms alignment search plus the interpolation margin.
        Assert.Equal(2 + 960 + 288, effect.LatencyFrames);

        effect.Mode.Value = PitchShiftEffect.PhaseVocoderMode;
        Assert.Equal(Dsp.PhaseVocoder.LatencyFor(2048), effect.LatencyFrames);
        Assert.InRange(effect.LatencyFrames, 1700, 2000);
    }
}

public class EffectChainTests
{
    private sealed class GainEffect(float gain) : AudioEffect("gain", "Gain")
    {
        public int ResetCount { get; private set; }

        protected override void OnPrepare()
        {
        }

        protected override void OnParametersChanged()
        {
        }

        protected override void OnReset() => ResetCount++;

        protected override void ProcessCore(Span<float> interleaved)
        {
            for (int i = 0; i < interleaved.Length; i++)
            {
                interleaved[i] *= gain;
            }
        }
    }

    private static float[] Ones(int count) => Enumerable.Repeat(1f, count).ToArray();

    [Fact]
    public void Effects_run_in_order_and_cross_fade_when_switched()
    {
        var chain = new EffectChain();
        var gain = new GainEffect(2);
        chain.Add(gain);
        chain.Prepare(48000, 1);

        // First block fades in from dry to wet.
        var first = Ones(100);
        chain.Process(first);
        Assert.Equal(1.01f, first[0], 3);
        Assert.Equal(2f, first[^1], 3);

        var steady = Ones(100);
        chain.Process(steady);
        Assert.All(steady, s => Assert.Equal(2f, s));

        // Disabling fades back to dry, then bypasses completely.
        gain.IsEnabled = false;
        var fadeOut = Ones(100);
        chain.Process(fadeOut);
        Assert.Equal(1f, fadeOut[^1], 3);
        Assert.True(fadeOut[0] > 1.9f);

        var bypassed = Ones(100);
        chain.Process(bypassed);
        Assert.All(bypassed, s => Assert.Equal(1f, s));
    }

    [Fact]
    public void Master_switch_bypasses_every_effect()
    {
        var chain = new EffectChain();
        chain.Add(new GainEffect(2));
        chain.Add(new GainEffect(3));
        chain.Prepare(48000, 1);
        chain.IsEnabled = false;

        var block = Ones(64);
        chain.Process(block);
        Assert.All(block, s => Assert.Equal(1f, s));

        chain.IsEnabled = true;
        chain.Process(Ones(64));
        block = Ones(64);
        chain.Process(block);
        Assert.All(block, s => Assert.Equal(6f, s));
    }

    [Fact]
    public void Effects_added_after_prepare_are_prepared_and_reset_when_enabled()
    {
        var chain = new EffectChain();
        chain.Prepare(44100, 2);
        var gain = new GainEffect(0.5f);
        chain.Add(gain);
        Assert.Equal(44100, gain.SampleRate);
        Assert.Equal(2, gain.Channels);

        int resetsBefore = gain.ResetCount;
        chain.Process(Ones(32));
        Assert.Equal(resetsBefore + 1, gain.ResetCount);
    }

    [Fact]
    public void Move_and_remove_update_the_order()
    {
        var chain = new EffectChain();
        var a = new GainEffect(1);
        var b = new GainEffect(1);
        var c = new GainEffect(1);
        chain.Add(a);
        chain.Add(b);
        chain.Add(c);
        int changes = 0;
        chain.Changed += (_, _) => changes++;

        chain.Move(0, 2);
        Assert.Equal([b, c, a], chain.Effects);
        Assert.True(chain.Remove(c));
        Assert.Equal([b, a], chain.Effects);
        Assert.False(chain.Remove(c));
        Assert.Equal(2, changes);
        Assert.Throws<InvalidOperationException>(() => chain.Add(a));
    }

    [Fact]
    public void Large_buffers_are_processed_in_blocks()
    {
        var chain = new EffectChain();
        chain.Add(new GainEffect(2));
        chain.Prepare(48000, 2);
        chain.Process(Ones(64));

        var big = Ones(EffectChain.MaxBlockFrames * 2 * 3 + 10);
        chain.Process(big);
        Assert.All(big, s => Assert.Equal(2f, s));
    }
}
