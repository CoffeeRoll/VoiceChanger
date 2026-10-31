using VoiceChanger.Core.Effects;
using VoiceChanger.Core.Graph;
using VoiceChanger.Core.Settings;
using static VoiceChanger.Core.Tests.SignalHelpers;

namespace VoiceChanger.Core.Tests;

public class AudioGraphTests
{
    [Fact]
    public void Connect_wires_an_output_to_an_input_and_replaces_the_previous_wire()
    {
        var graph = new AudioGraph();
        var first = graph.Add(new ToneNode());
        var second = graph.Add(new ToneNode());
        var output = graph.Add(new OutputDeviceNode());
        int changes = 0;
        graph.Changed += (_, _) => changes++;

        var wire = graph.Connect(first.Outputs[0], output.Inputs[0]);
        Assert.Same(wire, graph.ConnectionTo(output.Inputs[0]));
        Assert.Equal(1, changes);

        // An input takes one wire: connecting another source replaces it.
        graph.Connect(second.Outputs[0], output.Inputs[0]);
        Assert.Equal(second.Outputs[0], Assert.Single(graph.Connections).From);
        Assert.Equal(2, changes);

        // Connecting the same wire again changes nothing.
        graph.Connect(second.Outputs[0], output.Inputs[0]);
        Assert.Equal(2, changes);

        Assert.True(graph.Disconnect(graph.Connections[0]));
        Assert.Empty(graph.Connections);
        Assert.Equal(3, changes);
    }

    [Fact]
    public void Outputs_can_feed_several_inputs()
    {
        var graph = new AudioGraph();
        var tone = graph.Add(new ToneNode());
        var mixer = graph.Add(new MixerNode());
        var speakers = graph.Add(new OutputDeviceNode());

        graph.Connect(tone.Outputs[0], mixer.Inputs[0]);
        graph.Connect(tone.Outputs[0], mixer.Inputs[1]);
        graph.Connect(tone.Outputs[0], speakers.Inputs[0]);

        Assert.Equal(3, graph.ConnectionsFrom(tone.Outputs[0]).Count());
    }

    [Fact]
    public void Wires_that_would_loop_or_go_the_wrong_way_are_refused()
    {
        var graph = new AudioGraph();
        var pitch = graph.Add(new EffectNode(new PitchShiftEffect()));
        var eq = graph.Add(new EffectNode(new EqualizerEffect()));
        var mixer = graph.Add(new MixerNode());
        graph.Connect(pitch.Outputs[0], eq.Inputs[0]);
        graph.Connect(eq.Outputs[0], mixer.Inputs[0]);

        Assert.False(graph.CanConnect(mixer.Outputs[0], pitch.Inputs[0], out string? reason));
        Assert.Contains("loop", reason);
        Assert.False(graph.CanConnect(eq.Outputs[0], eq.Inputs[0], out reason));
        Assert.Contains("own input", reason);
        Assert.False(graph.CanConnect(eq.Inputs[0], pitch.Outputs[0], out reason));
        Assert.Contains("from an output", reason);
        Assert.False(graph.CanConnect(new ToneNode().Outputs[0], pitch.Inputs[0], out reason));
        Assert.Contains("not part of this graph", reason);
        Assert.Throws<InvalidOperationException>(() => graph.Connect(mixer.Outputs[0], pitch.Inputs[0]));
        Assert.Equal(2, graph.Connections.Count);

        Assert.True(graph.IsUpstream(of: mixer, candidate: pitch));
        Assert.False(graph.IsUpstream(of: pitch, candidate: mixer));
    }

    [Fact]
    public void Removing_a_node_removes_its_wires()
    {
        var graph = new AudioGraph();
        var tone = graph.Add(new ToneNode());
        var eq = graph.Add(new EffectNode(new EqualizerEffect()));
        var output = graph.Add(new OutputDeviceNode());
        graph.Connect(tone.Outputs[0], eq.Inputs[0]);
        graph.Connect(eq.Outputs[0], output.Inputs[0]);

        Assert.True(graph.Remove(eq));
        Assert.Empty(graph.Connections);
        Assert.Equal([tone, output], graph.Nodes);
        Assert.False(graph.Remove(eq));
    }

    [Fact]
    public void Changing_the_mixer_input_count_adds_and_removes_sockets()
    {
        var graph = new AudioGraph();
        var tone = graph.Add(new ToneNode());
        var mixer = graph.Add(new MixerNode());
        Assert.Equal(["in1", "in2"], mixer.Inputs.Select(p => p.Id));

        mixer.InputCount.Value = 4;
        var fourth = mixer.Inputs[3];
        var first = mixer.Inputs[0];
        graph.Connect(tone.Outputs[0], first);
        graph.Connect(tone.Outputs[0], fourth);
        Assert.Equal("Input 4", fourth.Name);

        int changes = 0;
        graph.Changed += (_, _) => changes++;
        mixer.InputCount.Value = 3;

        // The wire on the removed socket goes away, the others stay attached to the same sockets.
        Assert.Equal(1, changes);
        Assert.Same(first, Assert.Single(graph.Connections).To);
        Assert.Same(first, mixer.Inputs[0]);
        Assert.False(mixer.Gains[3].IsAvailable);
        Assert.True(mixer.Gains[2].IsAvailable);
    }

    [Fact]
    public void Updates_are_reported_once()
    {
        var graph = new AudioGraph();
        int changes = 0;
        graph.Changed += (_, _) => changes++;

        using (graph.BeginUpdate())
        {
            var tone = graph.Add(new ToneNode());
            var output = graph.Add(new OutputDeviceNode { DeviceId = "speakers" });
            graph.Connect(tone.Outputs[0], output.Inputs[0]);
            Assert.Equal(0, changes);
        }

        Assert.Equal(1, changes);

        // Choosing a device changes how the graph is played.
        ((OutputDeviceNode)graph.Nodes[1]).DeviceId = "cable";
        Assert.Equal(2, changes);
    }

    [Fact]
    public void Effects_switch_bypasses_every_effect_node()
    {
        var graph = new AudioGraph();
        var pitch = graph.Add(new EffectNode(new PitchShiftEffect()));
        graph.EffectsEnabled = false;
        var eq = graph.Add(new EffectNode(new EqualizerEffect()));

        Assert.False(pitch.Chain.IsEnabled);
        Assert.False(eq.Chain.IsEnabled);
        graph.EffectsEnabled = true;
        Assert.True(pitch.Chain.IsEnabled && eq.Chain.IsEnabled);
    }

    [Fact]
    public void The_registry_offers_devices_generators_effects_and_the_mixing_nodes()
    {
        var registry = NodeRegistry.Default;
        Assert.Equal(
            ["input", "tone", "effect:pitch-shift", "effect:auto-tune", "effect:equalizer", "effect:vocoder", "invert", "mixer", "sync", "split-stereo", "output"],
            registry.Descriptors.Select(d => d.TypeId));

        var node = Assert.IsType<EffectNode>(registry.Create("effect:auto-tune"));
        Assert.IsType<AutoTuneEffect>(node.Effect);
        Assert.Equal("effect:auto-tune", node.TypeId);
        Assert.Null(registry.Create("unknown"));
    }
}

public class GraphPlanTests
{
    private const int Rate = 48000;

    /// <summary>Compiles <paramref name="graph"/> with a mix buffer per output node and runs it for <paramref name="frames"/> frames.</summary>
    private static Dictionary<OutputDeviceNode, List<float>> Run(AudioGraph graph, int frames, int channels = 1, Dictionary<InputDeviceNode, float[]>? inputs = null)
    {
        foreach (var node in graph.Nodes)
        {
            node.Prepare(Rate, channels);
        }

        var mixes = graph.Nodes.OfType<OutputDeviceNode>().ToDictionary(n => n, _ => new float[GraphPlan.BlockFrames * channels]);
        var captures = graph.Nodes.OfType<InputDeviceNode>().ToDictionary(n => n, _ => new float[GraphPlan.BlockFrames * channels]);
        var plan = GraphPlan.Compile(graph, channels, n => n switch
        {
            OutputDeviceNode output => mixes[output],
            InputDeviceNode input when inputs?.ContainsKey(input) == true => captures[input],
            _ => null,
        });

        var result = mixes.Keys.ToDictionary(n => n, _ => new List<float>());
        for (int offset = 0; offset < frames; offset += 480)
        {
            int count = Math.Min(480, frames - offset);
            foreach (var (node, signal) in inputs ?? [])
            {
                signal.AsSpan(offset * channels, count * channels).CopyTo(captures[node]);
            }

            foreach (var mix in mixes.Values)
            {
                Array.Clear(mix);
            }

            plan.Process(count);
            foreach (var (node, mix) in mixes)
            {
                result[node].AddRange(mix.AsSpan(0, count * channels));
            }
        }

        return result;
    }

    private static ToneNode Tone(AudioGraph graph, float frequency, float levelDb)
    {
        var tone = graph.Add(new ToneNode());
        tone.Frequency.Value = frequency;
        tone.Level.Value = levelDb;
        return tone;
    }

    [Fact]
    public void Nodes_run_after_their_sources_and_unheard_nodes_are_skipped()
    {
        var graph = new AudioGraph();
        var output = graph.Add(new OutputDeviceNode());
        var eq = graph.Add(new EffectNode(new EqualizerEffect()));
        var mixer = graph.Add(new MixerNode());
        var mic = graph.Add(new InputDeviceNode());
        var tone = graph.Add(new ToneNode());
        var dangling = graph.Add(new EffectNode(new PitchShiftEffect()));
        var unused = graph.Add(new ToneNode());
        graph.Connect(mixer.Outputs[0], eq.Inputs[0]);
        graph.Connect(eq.Outputs[0], output.Inputs[0]);
        graph.Connect(mic.Outputs[0], mixer.Inputs[0]);
        graph.Connect(tone.Outputs[0], mixer.Inputs[1]);
        graph.Connect(tone.Outputs[0], dangling.Inputs[0]);

        var order = GraphPlan.Compile(graph, 1, _ => null).Order;

        // The pitch shifter and the second tone lead nowhere, so they would only waste CPU.
        Assert.DoesNotContain(dangling, order);
        Assert.DoesNotContain(unused, order);
        Assert.Equal(5, order.Count);
        Assert.True(order.ToList().IndexOf(mixer) > order.ToList().IndexOf(mic));
        Assert.True(order.ToList().IndexOf(mixer) > order.ToList().IndexOf(tone));
        Assert.True(order.ToList().IndexOf(eq) > order.ToList().IndexOf(mixer));
        Assert.True(order.ToList().IndexOf(output) > order.ToList().IndexOf(eq));
    }

    [Fact]
    public void Tone_generator_plays_the_requested_pitch_and_level()
    {
        var graph = new AudioGraph();
        var tone = Tone(graph, 440, -6);
        var output = graph.Add(new OutputDeviceNode());
        graph.Connect(tone.Outputs[0], output.Inputs[0]);

        var signal = Run(graph, Rate / 2)[output].ToArray();

        Assert.Equal(440, DominantFrequency(signal, Rate), 0.5);
        Assert.Equal(0.501, ToneAmplitude(signal, 440, Rate), 0.01);
        Assert.Equal(0.501f, tone.Meter.ReadAndReset(), 0.01f);
    }

    [Theory]
    [InlineData(ToneWaveform.Triangle)]
    [InlineData(ToneWaveform.Sawtooth)]
    [InlineData(ToneWaveform.Square)]
    [InlineData(ToneWaveform.Noise)]
    public void Every_waveform_stays_within_range(ToneWaveform waveform)
    {
        var graph = new AudioGraph();
        var tone = Tone(graph, 1000, 0);
        tone.Waveform.Value = (int)waveform;
        var output = graph.Add(new OutputDeviceNode());
        graph.Connect(tone.Outputs[0], output.Inputs[0]);

        var signal = Run(graph, Rate / 4)[output].ToArray();

        Assert.All(signal, s => Assert.InRange(s, -1.1f, 1.1f));
        Assert.True(Rms(signal) > 0.2, $"rms {Rms(signal)}");
        if (waveform != ToneWaveform.Noise)
        {
            Assert.Equal(1000, DominantFrequency(signal, Rate), 1.0);
        }
    }

    [Fact]
    public void Mixer_combines_its_inputs_with_their_own_volumes()
    {
        var graph = new AudioGraph();
        var low = Tone(graph, 300, -6);
        var high = Tone(graph, 1100, -6);
        var third = Tone(graph, 2500, -6);
        var mixer = graph.Add(new MixerNode());
        mixer.InputCount.Value = 3;
        var output = graph.Add(new OutputDeviceNode());
        graph.Connect(low.Outputs[0], mixer.Inputs[0]);
        graph.Connect(high.Outputs[0], mixer.Inputs[1]);
        graph.Connect(third.Outputs[0], mixer.Inputs[2]);
        graph.Connect(mixer.Outputs[0], output.Inputs[0]);
        mixer.Gains[1].Value = -6;
        mixer.Mutes[2].Value = 1;
        mixer.Master.Value = -3;

        var signal = Run(graph, Rate / 2)[output].ToArray();

        double master = Math.Pow(10, -3 / 20.0);
        Assert.Equal(0.501 * master, ToneAmplitude(signal, 300, Rate), 0.01);
        Assert.Equal(0.501 * 0.501 * master, ToneAmplitude(signal, 1100, Rate), 0.01);
        Assert.True(ToneAmplitude(signal, 2500, Rate) < 0.001);

        // Input meters show what arrives, even on a muted input.
        Assert.Equal(0.501f, mixer.InputMeters[2].ReadAndReset(), 0.01f);
    }

    [Fact]
    public void Mixer_fades_a_newly_connected_input_in()
    {
        var graph = new AudioGraph();
        var tone = Tone(graph, 1000, 0);
        tone.Waveform.Value = (int)ToneWaveform.Square;
        var mixer = graph.Add(new MixerNode());
        var output = graph.Add(new OutputDeviceNode());
        graph.Connect(tone.Outputs[0], mixer.Inputs[0]);
        graph.Connect(mixer.Outputs[0], output.Inputs[0]);

        var signal = Run(graph, 480)[output].ToArray();

        Assert.True(Math.Abs(signal[0]) < 0.01, $"first sample {signal[0]}");
        Assert.True(Math.Abs(signal[^2]) > 0.9);
    }

    [Fact]
    public void Several_outputs_can_share_a_device_and_mix_on_it()
    {
        var graph = new AudioGraph();
        var low = Tone(graph, 300, -6);
        var high = Tone(graph, 1100, -6);
        var first = graph.Add(new OutputDeviceNode());
        var second = graph.Add(new OutputDeviceNode());
        graph.Connect(low.Outputs[0], first.Inputs[0]);
        graph.Connect(high.Outputs[0], second.Inputs[0]);
        second.Volume.Value = -6;
        foreach (var node in graph.Nodes)
        {
            node.Prepare(Rate, 2);
        }

        var shared = new float[GraphPlan.BlockFrames * 2];
        var plan = GraphPlan.Compile(graph, 2, _ => shared);
        var signal = new List<float>();
        for (int block = 0; block < 50; block++)
        {
            Array.Clear(shared);
            plan.Process(480);
            signal.AddRange(shared.AsSpan(0, 960));
        }

        var left = Channel(signal.ToArray(), 2, 0);
        Assert.Equal(0.501, ToneAmplitude(left, 300, Rate), 0.01);
        Assert.Equal(0.251, ToneAmplitude(left, 1100, Rate), 0.01);
        Assert.Equal(left, Channel(signal.ToArray(), 2, 1));
    }

    [Fact]
    public void Split_stereo_sends_each_channel_to_an_output_of_its_own()
    {
        var graph = new AudioGraph();
        var device = graph.Add(new InputDeviceNode { DeviceId = "interface" });
        var split = graph.Add(new StereoSplitNode());
        var leftOut = graph.Add(new OutputDeviceNode());
        var rightOut = graph.Add(new OutputDeviceNode());
        graph.Connect(device.Outputs[0], split.Inputs[0]);
        graph.Connect(split.Outputs[0], leftOut.Inputs[0]);
        graph.Connect(split.Outputs[1], rightOut.Inputs[0]);

        // A different microphone on each input of the interface: 300 Hz on the left, 1100 Hz on the right.
        var left = Sine(300, Rate, Rate / 2, amplitude: 0.4);
        var right = Sine(1100, Rate, Rate / 2, amplitude: 0.2);
        var stereo = new float[Rate];
        for (int n = 0; n < Rate / 2; n++)
        {
            stereo[2 * n] = left[n];
            stereo[2 * n + 1] = right[n];
        }

        var mixes = Run(graph, Rate / 2, channels: 2, inputs: new() { [device] = stereo });

        // Each track plays on both channels, in the middle.
        foreach (int channel in new[] { 0, 1 })
        {
            var fromLeft = Channel(mixes[leftOut].ToArray(), 2, channel);
            var fromRight = Channel(mixes[rightOut].ToArray(), 2, channel);
            Assert.Equal(left, fromLeft);
            Assert.Equal(right, fromRight);
        }

        Assert.Equal(0.4f, split.Meter.ReadAndReset(), 0.01f);
    }

    [Fact]
    public void In_mono_both_split_outputs_carry_the_signal()
    {
        var graph = new AudioGraph();
        var mic = graph.Add(new InputDeviceNode { DeviceId = "mic" });
        var split = graph.Add(new StereoSplitNode());
        var first = graph.Add(new OutputDeviceNode());
        var second = graph.Add(new OutputDeviceNode());
        graph.Connect(mic.Outputs[0], split.Inputs[0]);
        graph.Connect(split.Outputs[0], first.Inputs[0]);
        graph.Connect(split.Outputs[1], second.Inputs[0]);
        var voice = Sine(500, Rate, Rate / 4, amplitude: 0.3);

        var mixes = Run(graph, Rate / 4, inputs: new() { [mic] = voice });

        Assert.Equal(voice, mixes[first]);
        Assert.Equal(voice, mixes[second]);
    }

    [Fact]
    public void Invert_turns_the_signal_upside_down_and_cancels_it_in_a_mix()
    {
        var graph = new AudioGraph();
        var tone = Tone(graph, 440, -6);
        var invert = graph.Add(new InvertNode());
        var mixer = graph.Add(new MixerNode());
        var plain = graph.Add(new OutputDeviceNode());
        var inverted = graph.Add(new OutputDeviceNode());
        var cancelled = graph.Add(new OutputDeviceNode());
        graph.Connect(tone.Outputs[0], plain.Inputs[0]);
        graph.Connect(tone.Outputs[0], invert.Inputs[0]);
        graph.Connect(invert.Outputs[0], inverted.Inputs[0]);
        graph.Connect(tone.Outputs[0], mixer.Inputs[0]);
        graph.Connect(invert.Outputs[0], mixer.Inputs[1]);
        graph.Connect(mixer.Outputs[0], cancelled.Inputs[0]);

        var mixes = Run(graph, Rate / 4, channels: 2);

        Assert.True(Rms(mixes[plain].ToArray()) > 0.3);
        Assert.Equal(mixes[plain].Select(s => -s), mixes[inverted]);
        Assert.All(mixes[cancelled], s => Assert.Equal(0f, s));
    }

    [Fact]
    public void Input_nodes_read_their_device_and_apply_their_gain()
    {
        var graph = new AudioGraph();
        var mic = graph.Add(new InputDeviceNode { DeviceId = "mic" });
        var silent = graph.Add(new InputDeviceNode { DeviceId = "missing" });
        var mixer = graph.Add(new MixerNode());
        var output = graph.Add(new OutputDeviceNode());
        graph.Connect(mic.Outputs[0], mixer.Inputs[0]);
        graph.Connect(silent.Outputs[0], mixer.Inputs[1]);
        graph.Connect(mixer.Outputs[0], output.Inputs[0]);
        mic.Gain.Value = 6;

        var signal = Run(graph, Rate / 2, inputs: new() { [mic] = Sine(500, Rate, Rate / 2, amplitude: 0.25) })[output].ToArray();

        Assert.Equal(0.25 * 1.995, ToneAmplitude(signal, 500, Rate), 0.01);
        Assert.Equal(0f, silent.Meter.ReadAndReset());
    }

    [Fact]
    public void Effect_nodes_process_and_can_be_bypassed()
    {
        var graph = new AudioGraph();
        var tone = Tone(graph, 440, -6);
        var pitch = new PitchShiftEffect();
        pitch.Semitones.Value = 12;
        var effect = graph.Add(new EffectNode(pitch));
        var output = graph.Add(new OutputDeviceNode());
        graph.Connect(tone.Outputs[0], effect.Inputs[0]);
        graph.Connect(effect.Outputs[0], output.Inputs[0]);

        var shifted = Run(graph, Rate)[output].ToArray();
        Assert.Equal(880, DominantFrequency(shifted.AsSpan(Rate / 2), Rate), 3.0);

        effect.Effect.IsEnabled = false;
        var bypassed = Run(graph, Rate / 2)[output].ToArray();
        Assert.Equal(440, DominantFrequency(bypassed, Rate), 1.0);
    }

    [Fact]
    public void Latency_is_the_slowest_path_to_an_output()
    {
        var graph = new AudioGraph();
        var tone = graph.Add(new ToneNode());
        var vocoder = new PitchShiftEffect();
        vocoder.Mode.Value = PitchShiftEffect.PhaseVocoderMode;
        vocoder.Semitones.Value = 3;
        var slow = graph.Add(new EffectNode(vocoder));
        var fast = graph.Add(new EffectNode(new EqualizerEffect()));
        var mixer = graph.Add(new MixerNode());
        var output = graph.Add(new OutputDeviceNode());
        graph.Connect(tone.Outputs[0], slow.Inputs[0]);
        graph.Connect(tone.Outputs[0], fast.Inputs[0]);
        graph.Connect(slow.Outputs[0], mixer.Inputs[0]);
        graph.Connect(fast.Outputs[0], mixer.Inputs[1]);
        graph.Connect(mixer.Outputs[0], output.Inputs[0]);
        foreach (var node in graph.Nodes)
        {
            node.Prepare(Rate, 1);
        }

        var plan = GraphPlan.Compile(graph, 1, _ => null);

        Assert.True(slow.LatencyFrames > 0);
        Assert.Equal(slow.LatencyFrames, plan.LatencyFrames());
    }

    [Fact]
    public void Routing_describes_the_wiring_only()
    {
        var graph = new AudioGraph();
        var tone = graph.Add(new ToneNode());
        var output = graph.Add(new OutputDeviceNode());
        graph.Connect(tone.Outputs[0], output.Inputs[0]);
        string before = GraphPlan.Compile(graph, 1, _ => null).Routing;

        // A node that is not connected to anything does not change what is heard.
        graph.Add(new EffectNode(new EqualizerEffect()));
        Assert.Equal(before, GraphPlan.Compile(graph, 1, _ => null).Routing);

        graph.Disconnect(graph.Connections[0]);
        Assert.NotEqual(before, GraphPlan.Compile(graph, 1, _ => null).Routing);
    }
}

public class GraphSettingsTests
{
    [Fact]
    public void A_graph_survives_saving_and_loading()
    {
        var graph = new AudioGraph();
        var mic = graph.Add(new InputDeviceNode { DeviceId = "mic", Label = "Vocals", X = 40, Y = 120.5 });
        mic.Gain.Value = 3;
        var line = graph.Add(new InputDeviceNode { DeviceId = "line", X = 40, Y = 300 });
        var pitch = graph.Add(new EffectNode(new PitchShiftEffect()));
        ((PitchShiftEffect)pitch.Effect).Semitones.Value = -5;
        pitch.Effect.IsEnabled = false;
        var mixer = graph.Add(new MixerNode());
        mixer.InputCount.Value = 4;
        mixer.Gains[3].Value = -12;
        var output = graph.Add(new OutputDeviceNode { DeviceId = "speakers" });
        graph.Connect(mic.Outputs[0], pitch.Inputs[0]);
        graph.Connect(pitch.Outputs[0], mixer.Inputs[0]);
        graph.Connect(line.Outputs[0], mixer.Inputs[3]);
        graph.Connect(mixer.Outputs[0], output.Inputs[0]);

        string path = Path.Combine(Path.GetTempPath(), "VoiceChangerGraphTests", Guid.NewGuid().ToString("N"), "settings.json");
        try
        {
            var store = new SettingsStore(path);
            store.Save(new AppSettings { Mode = EditorMode.Nodes, Graph = GraphSettings.Capture(graph) });
            var loaded = store.Load();
            Assert.Equal(EditorMode.Nodes, loaded.Mode);

            var restored = new AudioGraph();
            loaded.Graph!.RestoreInto(restored, NodeRegistry.Default);

            Assert.Equal(graph.Nodes.Select(n => (n.Id, n.TypeId, n.Title, n.X, n.Y)), restored.Nodes.Select(n => (n.Id, n.TypeId, n.Title, n.X, n.Y)));
            Assert.Equal(
                graph.Connections.Select(c => $"{c.From.Node.Id}.{c.From.Id}>{c.To.Node.Id}.{c.To.Id}"),
                restored.Connections.Select(c => $"{c.From.Node.Id}.{c.From.Id}>{c.To.Node.Id}.{c.To.Id}"));
            var restoredMixer = restored.Nodes.OfType<MixerNode>().Single();
            Assert.Equal(4, restoredMixer.Inputs.Count);
            Assert.Equal(-12, restoredMixer.Gains[3].Value);
            var restoredPitch = restored.Nodes.OfType<EffectNode>().Single();
            Assert.False(restoredPitch.Effect.IsEnabled);
            Assert.Equal(-5, restoredPitch.Effect.GetParameter("semitones").Value);
            Assert.Equal(["mic", "line", "speakers"], restored.Nodes.OfType<DeviceNode>().Select(n => n.DeviceId));
            Assert.Equal(3, restored.Nodes.OfType<InputDeviceNode>().First().Gain.Value);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void Unknown_nodes_and_broken_wires_are_skipped()
    {
        var tone = Guid.NewGuid();
        var output = Guid.NewGuid();
        var settings = new GraphSettings
        {
            Nodes =
            [
                new NodeSettings { Id = tone, Type = "tone" },
                new NodeSettings { Id = Guid.NewGuid(), Type = "time-machine" },
                new NodeSettings { Id = output, Type = "output", X = double.NaN },
                new NodeSettings { Id = output, Type = "mixer" },
            ],
            Connections =
            [
                new ConnectionSettings { From = tone, To = output },
                new ConnectionSettings { From = tone, FromPort = "nope", To = output },
                new ConnectionSettings { From = output, To = tone },
                new ConnectionSettings { From = Guid.NewGuid(), To = output },
            ],
        };

        var graph = new AudioGraph();
        graph.Add(new ToneNode());
        settings.RestoreInto(graph, NodeRegistry.Default);

        Assert.Equal(["tone", "output", "mixer"], graph.Nodes.Select(n => n.TypeId));
        Assert.Equal(3, graph.Nodes.Select(n => n.Id).Distinct().Count());
        Assert.Equal(0, graph.Nodes[1].X);
        var wire = Assert.Single(graph.Connections);
        Assert.Equal(tone, wire.From.Node.Id);
    }
}
