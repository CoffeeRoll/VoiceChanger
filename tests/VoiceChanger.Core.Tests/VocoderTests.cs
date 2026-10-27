using VoiceChanger.Core.Dsp;
using VoiceChanger.Core.Effects;
using VoiceChanger.Core.Graph;
using VoiceChanger.Core.Settings;
using static VoiceChanger.Core.Tests.SignalHelpers;

namespace VoiceChanger.Core.Tests;

public class VocoderTests
{
    private const int Rate = 48000;

    private static float[] Vowel(int vowel, double f0 = 130, double seconds = 2) =>
        SyntheticVoice.Generate(seconds, _ => f0, _ => vowel, breathDb: -40, jitter: 0.003);

    private static float[] Run(float[] voice, Action<VocoderEffect>? setup = null)
    {
        var effect = new VocoderEffect();
        setup?.Invoke(effect);
        effect.Prepare(Rate, 1);
        var output = (float[])voice.Clone();
        ProcessInBlocks(output, 1, 480, block => effect.Process(block.Span));
        return output;
    }

    /// <summary>Level (dB) of every half octave from 250 Hz to 4 kHz, after the first half second.</summary>
    private static double[] Profile(float[] signal)
    {
        const int size = 8192;
        var power = new double[size / 2];
        var fft = new Fft(size);
        var re = new float[size];
        var im = new float[size];
        for (int start = Rate / 2; start + size <= signal.Length; start += size / 2)
        {
            for (int i = 0; i < size; i++)
            {
                re[i] = (float)(signal[start + i] * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / size)));
                im[i] = 0;
            }

            fft.Forward(re, im);
            for (int k = 0; k < size / 2; k++)
            {
                power[k] += re[k] * re[k] + im[k] * im[k];
            }
        }

        var bands = new List<double>();
        for (double low = 250; low < 3990; low *= Math.Sqrt(2))
        {
            double high = low * Math.Sqrt(2), sum = 1e-12;
            for (int k = (int)(low * size / Rate); k < (int)(high * size / Rate); k++)
            {
                sum += power[k];
            }

            bands.Add(10 * Math.Log10(sum));
        }

        return bands.ToArray();
    }

    private static double Correlation(double[] a, double[] b)
    {
        double ma = a.Average(), mb = b.Average(), ab = 0, aa = 0, bb = 0;
        for (int i = 0; i < a.Length; i++)
        {
            ab += (a[i] - ma) * (b[i] - mb);
            aa += (a[i] - ma) * (a[i] - ma);
            bb += (b[i] - mb) * (b[i] - mb);
        }

        return ab / Math.Sqrt(aa * bb);
    }

    private static double Pitch(float[] signal)
    {
        var detector = new PitchDetector(Rate);
        detector.Process(signal.AsSpan(signal.Length - Rate / 2));
        Assert.True(detector.IsVoiced);
        return detector.Frequency;
    }

    [Fact]
    public void The_sound_takes_the_shape_of_the_voice()
    {
        // "a" (resonances at 730 and 1090 Hz) and "i" (270 and 2290 Hz) shaping noise (which has
        // no harmonics of its own to get in the way of measuring): each output has the spectral
        // shape of its own vowel, peaks and valleys almost as deep, not the other one's.
        var a = Vowel(0);
        var i = Vowel(1);
        var outA = Profile(Run(a, e => e.Synth.Value = (int)VocoderSynth.Whisper));
        var outI = Profile(Run(i, e => e.Synth.Value = (int)VocoderSynth.Whisper));
        var shapeA = Profile(a);
        var shapeI = Profile(i);

        Assert.True(Correlation(outA, shapeA) > 0.85, $"a: {Correlation(outA, shapeA):0.00}");
        Assert.True(Correlation(outI, shapeI) > 0.85, $"i: {Correlation(outI, shapeI):0.00}");
        Assert.True(Correlation(outA, shapeI) < 0.5);
        Assert.True(Correlation(outI, shapeA) < 0.5);
        Assert.True(outA.Max() - outA.Min() > 0.75 * (shapeA.Max() - shapeA.Min()));
    }

    [Fact]
    public void The_synth_sets_the_pitch()
    {
        var voice = Vowel(0, f0: 180);
        Assert.InRange(Pitch(Run(voice, e => e.Synth.Value = (int)VocoderSynth.Note)), 109, 111);
        Assert.InRange(Pitch(Run(voice)), 178, 182);
        Assert.InRange(Pitch(Run(voice, e => e.Transpose.Value = -12)), 89, 91);
    }

    [Fact]
    public void Silence_stays_silent_and_the_level_follows_the_voice()
    {
        Assert.All(Run(new float[Rate]), s => Assert.Equal(0, s));

        var voice = SyntheticVoice.Generate(3, t => SyntheticVoice.Speech(t, 140), SyntheticVoice.Vowels);
        double level = Rms(voice.AsSpan(Rate / 2));
        foreach (var preset in new VocoderEffect().Presets)
        {
            var output = Run(voice, e => e.ApplyPreset(preset));
            Assert.InRange(Rms(output.AsSpan(Rate / 2)) / level, 0.6, 1.6);
            Assert.All(output, s => Assert.InRange(s, -1.5f, 1.5f));
        }
    }

    [Fact]
    public void Whisper_has_no_pitch_and_robot_does()
    {
        var voice = Vowel(3, seconds: 3);
        double robot = SyntheticVoice.Periodicity(Run(voice));
        double whisper = SyntheticVoice.Periodicity(Run(voice, e => e.Synth.Value = (int)VocoderSynth.Whisper));
        Assert.True(robot > 10, $"robot {robot:0.0} dB");
        Assert.True(whisper < robot - 12, $"whisper {whisper:0.0} dB, robot {robot:0.0} dB");
    }

    [Fact]
    public void Formant_shift_moves_the_resonances()
    {
        // How much louder the top of the spectrum (above 1.4 kHz) is than the bottom (below 700 Hz).
        static double Tilt(double[] profile) => profile[^3..].Average() - profile[..3].Average();

        var voice = Vowel(0);
        double down = Tilt(Profile(Run(voice, e => e.FormantShift.Value = -6)));
        double none = Tilt(Profile(Run(voice)));
        double up = Tilt(Profile(Run(voice, e => e.FormantShift.Value = 6)));
        Assert.True(down < none - 5 && none < up - 5, $"{down:0.0} {none:0.0} {up:0.0} dB");
    }

    [Fact]
    public void A_plugged_in_carrier_replaces_the_synth()
    {
        // Voice into the node's Voice socket, a 220 Hz sawtooth into its Carrier socket.
        var graph = new AudioGraph();
        var mic = graph.Add(new InputDeviceNode());
        var tone = graph.Add(new ToneNode());
        tone.Waveform.Value = (int)ToneWaveform.Sawtooth;
        tone.Frequency.Value = 220;
        var node = graph.Add(new EffectNode(new VocoderEffect()));
        var output = graph.Add(new OutputDeviceNode());
        Assert.Equal(["Voice", "Carrier"], node.Inputs.Select(p => p.Name));
        Assert.Equal(["in", "sidechain"], node.Inputs.Select(p => p.Id));
        graph.Connect(mic.Outputs[0], node.Inputs[0]);
        graph.Connect(tone.Outputs[0], node.Inputs[1]);
        graph.Connect(node.Outputs[0], output.Inputs[0]);

        float[] Play(float[] voice)
        {
            foreach (var n in graph.Nodes)
            {
                n.Prepare(Rate, 1);
            }

            var capture = new float[GraphPlan.BlockFrames];
            var mix = new float[GraphPlan.BlockFrames];
            var plan = GraphPlan.Compile(graph, 1, n => n == mic ? capture : n == output ? mix : null);
            var result = new List<float>();
            for (int offset = 0; offset + 480 <= voice.Length; offset += 480)
            {
                voice.AsSpan(offset, 480).CopyTo(capture);
                Array.Clear(mix);
                plan.Process(480);
                result.AddRange(mix.AsSpan(0, 480));
            }

            return result.ToArray();
        }

        var vocoder = (VocoderEffect)node.Effect;
        Assert.InRange(Pitch(Play(Vowel(0, f0: 150))), 218, 222);
        Assert.True(vocoder.UsesCarrierInput);

        // Unplugged, the synth takes over again (on the voice's own pitch).
        graph.Disconnect(graph.ConnectionTo(node.Inputs[1])!);
        Assert.InRange(Pitch(Play(Vowel(0, f0: 150))), 148, 152);
        Assert.False(vocoder.UsesCarrierInput);

        // The wire to the carrier is saved like any other.
        graph.Connect(tone.Outputs[0], node.Inputs[1]);
        var restored = new AudioGraph();
        GraphSettings.Capture(graph).RestoreInto(restored, NodeRegistry.Default);
        Assert.Single(restored.Connections, c => c.To.Id == "sidechain");
    }

    [Fact]
    public void Works_in_the_effect_chain_in_stereo()
    {
        var chain = new EffectChain();
        chain.Add(new VocoderEffect());
        chain.Prepare(Rate, 2);
        var mono = Vowel(0, f0: 160);
        var stereo = new float[mono.Length * 2];
        for (int n = 0; n < mono.Length; n++)
        {
            stereo[2 * n] = stereo[2 * n + 1] = mono[n];
        }

        ProcessInBlocks(stereo, 2, 512, block => chain.Process(block.Span));
        var left = Channel(stereo, 2, 0);
        Assert.Equal(left, Channel(stereo, 2, 1));
        Assert.InRange(Pitch(left), 158, 162);
    }

    [Fact]
    public void Survives_noise_and_parameter_changes_while_running()
    {
        var effect = new VocoderEffect();
        effect.Prepare(44100, 2);
        var random = new Random(3);
        var block = new float[441 * 2];
        for (int i = 0; i < 300; i++)
        {
            for (int s = 0; s < block.Length; s++)
            {
                block[s] = (float)(random.NextDouble() * 2 - 1);
            }

            if (i % 20 == 0)
            {
                foreach (var parameter in effect.Parameters)
                {
                    parameter.Value = (float)(parameter.Minimum + random.NextDouble() * (parameter.Maximum - parameter.Minimum));
                }
            }

            effect.Process(block);
            Assert.All(block, s => Assert.True(float.IsFinite(s) && Math.Abs(s) < 16, $"{s}"));
        }
    }
}
