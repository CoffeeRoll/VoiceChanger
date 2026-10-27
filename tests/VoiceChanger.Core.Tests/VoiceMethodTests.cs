using VoiceChanger.Core.Dsp;
using VoiceChanger.Core.Effects;
using static VoiceChanger.Core.Tests.SignalHelpers;

namespace VoiceChanger.Core.Tests;

public class VoiceMethodTests
{
    private const int SampleRate = 48000;

    private static float[] Shift(VoiceShiftMethod method, float[] signal, double ratio)
    {
        var shifter = VoiceShifter.Create(method, 1, SampleRate);
        shifter.Ratio = ratio;
        ProcessInBlocks(signal, 1, 480, block => shifter.Process(block.Span));
        return signal;
    }

    [Fact]
    public void Each_method_is_best_in_its_own_direction()
    {
        var voice = SyntheticVoice.Generate(3, t => SyntheticVoice.Speech(t, 110), SyntheticVoice.Vowels);

        // An octave down, moving pulses keeps the upper formants of a real lower voice; rebuilding
        // them from the weak, breathy top of the original spectrum loses them.
        var lower = SyntheticVoice.Generate(3, t => SyntheticVoice.Speech(t, 110) * 0.5, SyntheticVoice.Vowels);
        double expected = SyntheticVoice.BandShareDb(lower, 1500, 4000);
        double pulseDown = SyntheticVoice.BandShareDb(Shift(VoiceShiftMethod.Pulse, (float[])voice.Clone(), 0.5), 1500, 4000);
        double spectralDown = SyntheticVoice.BandShareDb(Shift(VoiceShiftMethod.Spectral, (float[])voice.Clone(), 0.5), 1500, 4000);
        Assert.InRange(pulseDown, expected - 3, expected + 3);
        Assert.True(spectralDown < pulseDown - 3, $"pulse {pulseDown:0.0} dB, spectral {spectralDown:0.0} dB");

        // An octave up, harmonics rebuilt under the envelope come out smoother than pulses that
        // overlap each other.
        double pulseUp = SyntheticVoice.Periodicity(Shift(VoiceShiftMethod.Pulse, (float[])voice.Clone(), 2));
        double spectralUp = SyntheticVoice.Periodicity(Shift(VoiceShiftMethod.Spectral, (float[])voice.Clone(), 2));
        Assert.True(spectralUp > pulseUp + 1, $"pulse {pulseUp:0.0} dB, spectral {spectralUp:0.0} dB");
    }

    [Theory]
    [InlineData(VoiceShiftMethod.Pulse, 3)]
    [InlineData(VoiceShiftMethod.Spectral, 3)]
    [InlineData(VoiceShiftMethod.Pulse, 500)]
    [InlineData(VoiceShiftMethod.Spectral, 500)]
    public void Shifts_by_exactly_the_ratio(VoiceShiftMethod method, int cents)
    {
        // Even a few cents come out exactly (frame hops are whole samples, but they carry their
        // rounding over instead of rounding the ratio).
        var output = Shift(method, PitchDetectorTests.Voice(200, SampleRate, 2 * SampleRate), Math.Pow(2, cents / 1200.0));
        double measured = 1200 * Math.Log2(DominantFrequency(output.AsSpan(SampleRate), SampleRate) / 200);
        Assert.InRange(measured, cents - 1, cents + 1);
    }

    [Fact]
    public void Spectral_method_follows_a_correction_frame_by_frame()
    {
        var periods = new List<double>();
        var shifter = new SpectralVoiceShifter(1, SampleRate)
        {
            Correction = (period, _) =>
            {
                if (period > 0)
                {
                    periods.Add(period);
                }

                // Pull the voice to exactly 250 Hz.
                return period > 0 ? period / (SampleRate / 250.0) : 1;
            },
        };
        var signal = PitchDetectorTests.Voice(200, SampleRate, 2 * SampleRate);
        ProcessInBlocks(signal, 1, 480, block => shifter.Process(block.Span));

        // One call per frame, about every 5 ms, with the period of the voice.
        Assert.InRange(periods.Count, 300, 380);
        Assert.All(periods.Skip(5), p => Assert.Equal(240, p, 1));
        Assert.Equal(250, DominantFrequency(signal.AsSpan(SampleRate), SampleRate), 0.5);
    }

    [Fact]
    public void Spectral_method_keeps_time_while_the_ratio_keeps_changing()
    {
        // Auto tune changes the ratio with every frame: the output must neither run dry nor pile up.
        const int rate = 44100;
        var shifter = new SpectralVoiceShifter(2, rate) { Correction = (_, time) => time / 4410 % 2 < 1 ? 1.06 : 0.94 };
        var mono = PitchDetectorTests.Voice(180, rate, 20 * rate);
        var signal = new float[mono.Length * 2];
        for (int i = 0; i < mono.Length; i++)
        {
            signal[2 * i] = signal[2 * i + 1] = mono[i];
        }

        double inputLevel = Rms(mono);
        ProcessInBlocks(signal, 2, 441, block => shifter.Process(block.Span));
        var left = Channel(signal, 2, 0);
        for (int block = rate / 5; block + rate / 100 <= left.Length; block += rate / 100)
        {
            Assert.InRange(Rms(left.AsSpan(block, rate / 100)) / inputLevel, 0.5, 2);
        }

        Assert.All(signal, s => Assert.True(float.IsFinite(s)));
    }

    [Fact]
    public void Shifter_set_switches_methods_and_starts_the_new_one_from_silence()
    {
        var set = new VoiceShifterSet(1, SampleRate);
        Assert.Equal(VoiceShiftMethod.Pulse, set.Method);
        Assert.IsType<PsolaPitchShifter>(set.Current);

        set.Ratio = 2;
        set.FormantRatio = 1.5;
        foreach (var method in Enum.GetValues<VoiceShiftMethod>())
        {
            Assert.Equal(2, set[method].Ratio);
            Assert.Equal(1.5, set[method].FormantRatio);
        }

        Assert.True(set.Select(VoiceShiftMethod.Spectral));
        Assert.False(set.Select(VoiceShiftMethod.Spectral));
        Assert.IsType<SpectralVoiceShifter>(set.Current);
        set.Current.Process(PitchDetectorTests.Voice(200, SampleRate, SampleRate / 2));

        // Switching away and back: nothing of the old sound comes out again.
        set.Select(VoiceShiftMethod.Pulse);
        set.Select(VoiceShiftMethod.Spectral);
        var silence = new float[SampleRate / 2];
        set.Current.Process(silence);
        Assert.All(silence, s => Assert.Equal(0, s));
    }

    [Fact]
    public void Every_voice_effect_offers_the_same_methods()
    {
        var pitch = new PitchShiftEffect().GetParameter(VoiceShifterSet.ParameterId);
        var autoTune = new AutoTuneEffect().GetParameter(VoiceShifterSet.ParameterId);
        Assert.Equal(pitch.Name, autoTune.Name);
        Assert.Equal(pitch.Choices, autoTune.Choices);
        Assert.Equal(pitch.Description, autoTune.Description);
        Assert.Equal(["Pulse", "Spectral"], pitch.Choices);
        Assert.True(pitch.ShowAsButtons);
        Assert.Equal((int)VoiceShiftMethod.Pulse, (int)pitch.DefaultValue);
    }
}
