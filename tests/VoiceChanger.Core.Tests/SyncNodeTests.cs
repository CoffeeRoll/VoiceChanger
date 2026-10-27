using VoiceChanger.Core.Effects;
using VoiceChanger.Core.Graph;
using VoiceChanger.Core.Settings;

namespace VoiceChanger.Core.Tests;

/// <summary>An effect that only delays, by a fixed number of frames, and says so.</summary>
internal sealed class FixedDelayEffect(int frames) : AudioEffect("test-delay", "Delay")
{
    private float[] _line = [];
    private int _write;

    public override int LatencyFrames => frames;

    protected override void OnPrepare() => _line = new float[(frames + 1) * Channels];

    protected override void OnParametersChanged()
    {
    }

    protected override void OnReset() => Array.Clear(_line);

    protected override void ProcessCore(Span<float> interleaved)
    {
        for (int i = 0; i < interleaved.Length; i++)
        {
            (_line[_write], interleaved[i]) = (interleaved[i], _line[(_write + Channels) % _line.Length]);
            _write = (_write + 1) % _line.Length;
        }
    }
}

public class SyncNodeTests
{
    private const int Rate = 48000;

    /// <summary>Claps: short noise bursts with a sharp start, at uneven times.</summary>
    private static float[] Claps(double seconds, int seed = 1, double level = 0.5)
    {
        var signal = new float[(int)(seconds * Rate)];
        var random = new Random(seed);
        foreach (double at in new[] { 0.31, 0.92, 1.37, 2.18, 2.71, 3.55, 4.4, 5.02 })
        {
            int start = (int)(at * Rate);
            for (int n = 0; n < Rate / 50 && start + n < signal.Length; n++)
            {
                signal[start + n] += (float)(level * Math.Exp(-n / (0.004 * Rate)) * (random.NextDouble() * 2 - 1));
            }
        }

        return signal;
    }

    private static float[] Delayed(float[] signal, int frames)
    {
        var result = new float[signal.Length];
        signal.AsSpan(0, signal.Length - frames).CopyTo(result.AsSpan(frames));
        return result;
    }

    /// <summary>
    /// Two microphones (or whatever the test feeds them) through optional effects into a
    /// synchronizer, each of its outputs to its own output node.
    /// </summary>
    private sealed class Rig
    {
        private readonly float[] _first;
        private readonly float[] _second;
        private readonly float[] _firstCapture = new float[GraphPlan.BlockFrames];
        private readonly float[] _secondCapture = new float[GraphPlan.BlockFrames];
        private readonly float[] _firstMix = new float[GraphPlan.BlockFrames];
        private readonly float[] _secondMix = new float[GraphPlan.BlockFrames];
        private int _offset;
        private GraphPlan _plan = null!;

        public Rig(float[] first, float[] second, int firstEffectDelay = 0)
        {
            _first = first;
            _second = second;
            var a = Graph.Add(new InputDeviceNode());
            var b = Graph.Add(new InputDeviceNode());
            Sync = Graph.Add(new SyncNode());
            var outA = Graph.Add(new OutputDeviceNode());
            var outB = Graph.Add(new OutputDeviceNode());
            if (firstEffectDelay > 0)
            {
                var effect = Graph.Add(new EffectNode(new FixedDelayEffect(firstEffectDelay)));
                Graph.Connect(a.Outputs[0], effect.Inputs[0]);
                Graph.Connect(effect.Outputs[0], Sync.Inputs[0]);
            }
            else
            {
                Graph.Connect(a.Outputs[0], Sync.Inputs[0]);
            }

            Graph.Connect(b.Outputs[0], Sync.Inputs[1]);
            Graph.Connect(Sync.Outputs[0], outA.Inputs[0]);
            Graph.Connect(Sync.Outputs[1], outB.Inputs[0]);
            foreach (var node in Graph.Nodes)
            {
                node.Prepare(Rate, 1);
            }

            _plan = GraphPlan.Compile(Graph, 1, n => n switch
            {
                InputDeviceNode when n == a => _firstCapture,
                InputDeviceNode => _secondCapture,
                OutputDeviceNode when n == outA => _firstMix,
                OutputDeviceNode => _secondMix,
                _ => null,
            });
        }

        public AudioGraph Graph { get; } = new();

        public SyncNode Sync { get; }

        public List<float> First { get; } = [];

        public List<float> Second { get; } = [];

        public int LatencyFrames => _plan.LatencyFrames();

        public void Play(double seconds)
        {
            int end = Math.Min(_first.Length, _offset + (int)(seconds * Rate));
            for (; _offset < end; _offset += 480)
            {
                int count = Math.Min(480, end - _offset);
                _first.AsSpan(_offset, count).CopyTo(_firstCapture);
                _second.AsSpan(_offset, count).CopyTo(_secondCapture);
                Array.Clear(_firstMix);
                Array.Clear(_secondMix);
                _plan.Process(count);
                First.AddRange(_firstMix.AsSpan(0, count));
                Second.AddRange(_secondMix.AsSpan(0, count));
            }
        }
    }

    /// <summary>How many frames later <paramref name="signal"/> is than <paramref name="reference"/> (best match within ±maxLag).</summary>
    private static int Lag(IReadOnlyList<float> reference, IReadOnlyList<float> signal, int from, int maxLag)
    {
        int best = 0;
        double bestValue = double.NegativeInfinity;
        for (int lag = -maxLag; lag <= maxLag; lag++)
        {
            double sum = 0;
            for (int n = from; n < reference.Count - maxLag; n++)
            {
                sum += reference[n] * signal[n + lag];
            }

            if (sum > bestValue)
            {
                bestValue = sum;
                best = lag;
            }
        }

        return best;
    }

    [Fact]
    public void Matches_the_delay_of_effects_on_the_way()
    {
        // The first source goes through a 25 ms effect, the second straight in: matched, both come
        // out together, 25 ms after they went in.
        var claps = Claps(3);
        var rig = new Rig(claps, claps, firstEffectDelay: 1200);
        rig.Play(3);
        Assert.Equal(0, Lag(rig.First, rig.Second, Rate / 2, 2000));
        Assert.Equal(1200, Lag(claps, rig.Second, Rate / 2, 2000));
        Assert.Equal(1200, rig.Sync.DelayMilliseconds(1) * Rate / 1000, 0.5);
        Assert.Equal(0, rig.Sync.DelayMilliseconds(0));
        Assert.Equal(1200, rig.LatencyFrames);

        // Without matching, the second one is ahead by the effect's delay.
        var unmatched = new Rig(claps, claps, firstEffectDelay: 1200);
        unmatched.Sync.MatchEffectDelays.Value = 0;
        unmatched.Play(3);
        Assert.Equal(-1200, Lag(unmatched.First, unmatched.Second, Rate / 2, 2000));
    }

    [Fact]
    public void Offsets_move_an_input_later_or_earlier()
    {
        var claps = Claps(3);
        var later = new Rig(claps, claps);
        later.Sync.Offsets[1].Value = 10;
        later.Play(3);
        Assert.Equal(480, Lag(later.First, later.Second, Rate / 2, 2000));
        Assert.Equal(0, Lag(claps, later.First, Rate / 2, 2000));

        // Earlier means the other one waits; the extra delay shows in the latency.
        var earlier = new Rig(claps, claps);
        earlier.Sync.Offsets[1].Value = -10;
        earlier.Play(3);
        Assert.Equal(-480, Lag(earlier.First, earlier.Second, Rate / 2, 2000));
        Assert.Equal(480, Lag(claps, earlier.First, Rate / 2, 2000));
        Assert.Equal(480, earlier.Sync.LatencyFrames);
        Assert.Equal("10.0 ms earlier", SyncNode.FormatOffset(-10));
        Assert.Equal("10.0 ms later", SyncNode.FormatOffset(10));

        // Offsets of inputs that aren't there change nothing.
        var spare = new Rig(claps, claps);
        spare.Sync.InputCount.Value = 3;
        spare.Sync.Offsets[2].Value = -200;
        spare.Play(3);
        Assert.Equal(0, Lag(spare.First, spare.Second, Rate / 2, 2000));
        Assert.Equal(0, spare.Sync.LatencyFrames);
    }

    [Fact]
    public void Changing_a_delay_glides_without_a_click()
    {
        var tone = SignalHelpers.Sine(440, Rate, 2 * Rate, amplitude: 0.5);
        var rig = new Rig(tone, tone);
        rig.Play(0.5);
        rig.Sync.Offsets[1].Value = 7.3f;
        rig.Play(0.5);
        rig.Sync.Offsets[1].Value = -3;
        rig.Play(1);

        // A 440 Hz sine at 0.5 never moves more than 0.029 between samples; a jump would.
        for (int n = 1; n < rig.Second.Count; n++)
        {
            Assert.InRange(Math.Abs(rig.Second[n] - rig.Second[n - 1]), 0, 0.04);
        }
    }

    [Fact]
    public void Listening_to_a_clap_lines_up_a_device_that_records_late()
    {
        // The second device delivers the same claps 37.5 ms late, quieter, with its own noise.
        var claps = Claps(6);
        var random = new Random(9);
        var late = Delayed(claps, 1800).Select(v => (float)(0.4 * v + 0.01 * (random.NextDouble() - 0.5))).ToArray();
        var rig = new Rig(claps, late);
        rig.Play(0.2);

        rig.Sync.StartListening();
        Assert.True(rig.Sync.IsListening);
        Assert.Null(rig.Sync.TakeMeasurement());
        rig.Play(SyncNode.ListenSeconds + 0.1);
        Assert.False(rig.Sync.IsListening);

        var measurement = rig.Sync.TakeMeasurement();
        Assert.NotNull(measurement);
        Assert.Equal(0, measurement.LagMilliseconds[0]);
        Assert.Equal(37.5, measurement.LagMilliseconds[1]!.Value, 0.5);
        Assert.Null(rig.Sync.TakeMeasurement());

        Assert.Equal(1, rig.Sync.ApplyMeasurement(measurement));
        Assert.Equal(-37.5, rig.Sync.Offsets[1].Value, 0.5);
        int before = rig.First.Count;
        rig.Play(1.5);
        Assert.InRange(Lag(rig.First.Skip(before).ToList(), rig.Second.Skip(before).ToList(), Rate / 10, 2400), -24, 24);
    }

    [Fact]
    public void Listening_reports_inputs_with_nothing_in_common()
    {
        var rig = new Rig(Claps(6), SignalHelpers.Sine(220, Rate, 6 * Rate, amplitude: 0.3));
        rig.Sync.StartListening();
        rig.Play(SyncNode.ListenSeconds + 0.1);
        var measurement = rig.Sync.TakeMeasurement()!;
        Assert.Equal(0, measurement.LagMilliseconds[0]);
        Assert.Null(measurement.LagMilliseconds[1]);
        Assert.Equal(0, rig.Sync.ApplyMeasurement(measurement));
    }

    [Fact]
    public void Inputs_and_offsets_are_saved_with_the_graph()
    {
        var graph = new AudioGraph();
        var sync = graph.Add(new SyncNode());
        sync.InputCount.Value = 4;
        sync.Offsets[3].Value = -42.5f;
        sync.MatchEffectDelays.Value = 0;
        var tone = graph.Add(new ToneNode());
        graph.Connect(tone.Outputs[0], sync.Inputs[3]);

        var restored = new AudioGraph();
        GraphSettings.Capture(graph).RestoreInto(restored, NodeRegistry.Default);
        var copy = restored.Nodes.OfType<SyncNode>().Single();
        Assert.Equal(4, copy.Inputs.Count);
        Assert.Equal(4, copy.Outputs.Count);
        Assert.Equal(-42.5f, copy.Offsets[3].Value);
        Assert.False(copy.MatchEffectDelays.BoolValue);
        Assert.Single(restored.Connections, c => c.To == copy.Inputs[3]);
        Assert.Equal(NodeCategory.Mixing, NodeRegistry.Default.Find(SyncNode.Type)!.Category);
    }
}
