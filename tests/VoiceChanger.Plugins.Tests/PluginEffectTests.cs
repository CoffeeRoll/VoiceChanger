using VoiceChanger.Core.Effects;
using VoiceChanger.Core.Graph;
using VoiceChanger.Core.Settings;

namespace VoiceChanger.Plugins.Tests;

public sealed class PluginEffectTests
{
    private const int Rate = 48000;

    [Fact]
    public void Scanning_finds_the_effect_with_and_without_moduleinfo()
    {
        var info = TestPlugin.Info;
        var loaded = Assert.Single(PluginScanner.ScanModule(info.Path), p => p.Name == TestPlugin.ClassName);

        Assert.Equal(info.ClassId, loaded.ClassId);
        Assert.Equal("VoiceChanger", loaded.Vendor);
        Assert.Equal("Fx|Tools", loaded.Subcategories);
        Assert.Equal("Fx|Tools", info.Subcategories);
        Assert.Equal(32, info.ClassId.Length);
        Assert.Equal(PluginEffect.TypePrefix + info.ClassId, info.TypeId);
    }

    [Fact]
    public void Parameters_map_to_effect_parameters_with_the_plugins_text()
    {
        using var effect = TestPlugin.Open();

        Assert.Equal(TestPlugin.ClassName, effect.Name);
        Assert.Equal(TestPlugin.Info.TypeId, effect.TypeId);
        var sidechain = Assert.IsAssignableFrom<ISidechainEffect>(effect);
        Assert.Equal("Voice", sidechain.InputName);
        Assert.Equal("Key", sidechain.SidechainName);

        // The bypass (the effect's switch does that) and hidden parameters aren't shown.
        Assert.Equal(["Gain", "Latency", "Sidechain Mix", "Hold"], effect.Parameters.Select(p => p.Name));

        var gain = effect.GetParameter("0");
        Assert.Equal(ParameterKind.Continuous, gain.Kind);
        Assert.Equal(TestPlugin.GainValue(0), gain.DefaultValue, 5);
        Assert.Equal("0.0000 dB", gain.FormattedValue);
        Assert.Equal("12.0000 dB", gain.Format(1));

        var latency = effect.GetParameter("1");
        Assert.Equal(ParameterKind.Choice, latency.Kind);
        Assert.Equal(["None", "64 samples", "480 samples"], latency.Choices);

        Assert.Equal(ParameterKind.Toggle, effect.GetParameter("2").Kind);

        var hold = effect.GetParameter("4");
        Assert.Equal(ParameterKind.Integer, hold.Kind);
        Assert.Equal(7, hold.Maximum);
        Assert.Equal("1 samples", hold.FormattedValue);
        Assert.Equal("4 samples", hold.Format(3));
    }

    [Fact]
    public void Processes_stereo_and_follows_parameter_changes()
    {
        using var effect = TestPlugin.Open();
        effect.Prepare(Rate, 2);
        var input = Sine(1024, 2);

        var output = Run(effect, input);
        AssertClose(input, output, 1e-6f);

        effect.GetParameter("0").Value = (float)TestPlugin.GainValue(6);
        output = Run(effect, input);
        float gain = MathF.Pow(10, 6 / 20f);
        AssertClose(input.Select(x => x * gain).ToArray(), output, 1e-4f);
    }

    [Fact]
    public void Processes_mono()
    {
        using var effect = TestPlugin.Open();
        effect.Prepare(Rate, 1);
        effect.GetParameter("0").Value = (float)TestPlugin.GainValue(-6);
        var input = Sine(3000, 1);

        // Longer than the plug-in's block: split into several.
        var output = Run(effect, input);

        float gain = MathF.Pow(10, -6 / 20f);
        AssertClose(input.Select(x => x * gain).ToArray(), output, 1e-4f);
    }

    [Fact]
    public void The_sidechain_is_the_connected_signal_or_else_the_input()
    {
        using var effect = TestPlugin.Open();
        var sidechain = Assert.IsAssignableFrom<ISidechainEffect>(effect);
        effect.Prepare(Rate, 2);
        effect.GetParameter("2").Value = 1;
        var input = Sine(512, 2);

        sidechain.SetSidechain(Enumerable.Repeat(0.25f, input.Length).ToArray());
        var output = Run(effect, input);
        for (int i = 0; i < input.Length; i++)
        {
            Assert.Equal(input[i] + 0.25f, output[i], 5);
        }

        // Nothing connected: the plug-in hears its own input (keyed from itself).
        sidechain.SetSidechain(null);
        output = Run(effect, input);
        for (int i = 0; i < input.Length; i++)
        {
            Assert.Equal(2 * input[i], output[i], 5);
        }
    }

    [Fact]
    public void A_latency_change_restarts_the_plugin_and_is_reported()
    {
        using var effect = TestPlugin.Open();
        effect.Prepare(Rate, 1);
        Assert.Equal(0, effect.LatencyFrames);

        effect.GetParameter("1").Value = 1;
        Assert.Equal(64, effect.LatencyFrames);

        var impulse = new float[256];
        impulse[0] = 1;
        var output = Run(effect, impulse);
        Assert.Equal(64, Array.IndexOf(output, 1f));

        effect.GetParameter("1").Value = 2;
        Assert.Equal(480, effect.LatencyFrames);
    }

    [Fact]
    public void Reset_clears_the_plugins_signal_history()
    {
        using var effect = TestPlugin.Open();
        effect.Prepare(Rate, 1);
        effect.GetParameter("1").Value = 2;
        Run(effect, Sine(256, 1));

        effect.Reset();
        var output = Run(effect, new float[256]);
        Assert.All(output, sample => Assert.Equal(0, sample));
    }

    [Fact]
    public void Stepped_parameters_reach_the_processor()
    {
        using var effect = TestPlugin.Open();
        effect.Prepare(Rate, 1);
        effect.GetParameter("4").Value = 3; // hold every sample for 4
        var ramp = Enumerable.Range(0, 16).Select(i => (float)i).ToArray();

        var output = Run(effect, ramp);

        Assert.Equal([0, 0, 0, 0, 4, 4, 4, 4, 8, 8, 8, 8, 12, 12, 12, 12], output);
    }

    [Fact]
    public void State_round_trips_through_a_new_instance()
    {
        byte[] state;
        using (var effect = TestPlugin.Open())
        {
            effect.GetParameter("0").Value = (float)TestPlugin.GainValue(-12);
            effect.GetParameter("1").Value = 2;
            effect.GetParameter("4").Value = 5;
            state = Assert.IsType<byte[]>(effect.SaveState());
        }

        using var restored = TestPlugin.Open();
        Assert.True(restored.LoadState(state));
        Assert.Equal(TestPlugin.GainValue(-12), restored.GetParameter("0").Value, 5);
        Assert.Equal(2, restored.GetParameter("1").Value);
        Assert.Equal(5, restored.GetParameter("4").Value);

        restored.Prepare(Rate, 2);
        Assert.Equal(480, restored.LatencyFrames);
        Assert.False(restored.LoadState([1, 2, 3]));
    }

    [Fact]
    public void Settings_save_the_state_and_restore_it()
    {
        var registry = new EffectRegistry();
        registry.Register(PluginCatalog.Describe(TestPlugin.Info));
        var effect = Assert.IsAssignableFrom<PluginEffect>(registry.Create(TestPlugin.Info.TypeId));
        effect.GetParameter("0").Value = (float)TestPlugin.GainValue(3);
        effect.GetParameter("2").Value = 1;
        effect.IsEnabled = false;

        var saved = EffectSettings.Capture(effect);
        Assert.Equal(TestPlugin.ClassName, saved.Name);
        Assert.False(string.IsNullOrEmpty(saved.State));

        // The state wins over the parameter values.
        saved.Parameters["0"] = 0;
        using var restored = Assert.IsAssignableFrom<PluginEffect>(saved.Restore(registry));
        Assert.Equal(TestPlugin.GainValue(3), restored.GetParameter("0").Value, 5);
        Assert.Equal(1, restored.GetParameter("2").Value);
        Assert.False(restored.IsEnabled);
        effect.Dispose();
    }

    [Fact]
    public void A_plugin_that_is_not_installed_keeps_its_settings()
    {
        var saved = new EffectSettings
        {
            Type = "vst3:00112233445566778899AABBCCDDEEFF",
            Name = "Gone Reverb",
            State = Convert.ToBase64String([1, 2, 3, 4]),
            Parameters = new() { ["7"] = 0.25f },
            Enabled = false,
        };

        var missing = Assert.IsAssignableFrom<MissingEffect>(saved.Restore(EffectRegistry.Default));
        Assert.Equal("Gone Reverb (missing)", missing.Name);
        Assert.Empty(missing.Parameters);

        var input = Sine(256, 2);
        missing.Prepare(Rate, 2);
        AssertClose(input, Run(missing, input), 0);

        var again = EffectSettings.Capture(missing);
        Assert.Equal(saved.Type, again.Type);
        Assert.Equal(saved.Name, again.Name);
        Assert.Equal(saved.State, again.State);
        Assert.Equal(0.25f, again.Parameters["7"]);
        Assert.False(again.Enabled);

        // Built-in effects don't get a stand-in.
        Assert.Null(new EffectSettings { Type = "flanger" }.Restore(EffectRegistry.Default));
    }

    [Fact]
    public void A_missing_plugin_node_keeps_its_wires()
    {
        var registry = new EffectRegistry();
        registry.Register(PluginCatalog.Describe(TestPlugin.Info));
        var nodes = NodeRegistry.CreateDefault(registry);
        var graph = new AudioGraph();
        var voice = graph.Add(new InputDeviceNode());
        var key = graph.Add(new ToneNode());
        var plugin = graph.Add(nodes.Create(EffectNode.TypePrefix + TestPlugin.Info.TypeId)!);
        var output = graph.Add(new OutputDeviceNode());
        graph.Connect(voice.Outputs[0], plugin.Inputs[0]);
        graph.Connect(key.Outputs[0], plugin.Inputs[1]);
        graph.Connect(plugin.Outputs[0], output.Inputs[0]);
        var saved = GraphSettings.Capture(graph);

        // Restored where the plug-in isn't installed, and saved again.
        var elsewhere = new AudioGraph();
        saved.RestoreInto(elsewhere, NodeRegistry.Default);
        var standIn = Assert.IsType<EffectNode>(elsewhere.FindNode(plugin.Id));
        Assert.IsAssignableFrom<MissingEffect>(standIn.Effect);
        Assert.Equal(3, elsewhere.Connections.Count);
        var resaved = GraphSettings.Capture(elsewhere);

        // Back where it is installed: the plug-in, its state and its wires return.
        var back = new AudioGraph();
        resaved.RestoreInto(back, nodes);
        var node = Assert.IsType<EffectNode>(back.FindNode(plugin.Id));
        using var effect = Assert.IsAssignableFrom<PluginEffect>(node.Effect);
        Assert.Equal(3, back.Connections.Count);
        Assert.Equal(saved.Nodes.Single(n => n.Id == plugin.Id).State, resaved.Nodes.Single(n => n.Id == plugin.Id).State);
        ((PluginEffect)((EffectNode)plugin).Effect).Dispose();
    }

    [Fact]
    public void In_a_graph_the_latency_counts_and_the_second_socket_feeds_the_sidechain()
    {
        var graph = new AudioGraph();
        var voice = graph.Add(new InputDeviceNode());
        var key = graph.Add(new InputDeviceNode());
        using var effect = TestPlugin.Open();
        var plugin = graph.Add(new EffectNode(effect));
        var output = graph.Add(new OutputDeviceNode());
        graph.Connect(voice.Outputs[0], plugin.Inputs[0]);
        graph.Connect(key.Outputs[0], plugin.Inputs[1]);
        graph.Connect(plugin.Outputs[0], output.Inputs[0]);
        Assert.Equal(["Voice", "Key"], plugin.Inputs.Select(p => p.Name));

        foreach (var node in graph.Nodes)
        {
            node.Prepare(Rate, 1);
        }

        effect.GetParameter("1").Value = 1;
        effect.GetParameter("2").Value = 1;
        var voiceBuffer = new float[GraphPlan.BlockFrames];
        var keyBuffer = new float[GraphPlan.BlockFrames];
        var mix = new float[GraphPlan.BlockFrames];
        var plan = GraphPlan.Compile(graph, 1, n => n == voice ? voiceBuffer : n == key ? keyBuffer : n == output ? mix : null);
        Assert.Equal(64, plan.LatencyFrames());

        Array.Fill(voiceBuffer, 0.1f);
        Array.Fill(keyBuffer, 0.2f);
        for (int block = 0; block < 4; block++)
        {
            Array.Clear(mix);
            plan.Process(480);
        }

        Assert.All(mix.Take(480), sample => Assert.Equal(0.3f, sample, 5));
    }

    [Fact]
    public void A_crashing_plugin_passes_audio_through()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Crashes are only caught on Windows.");
        using var effect = TestPlugin.Open();
        effect.Prepare(Rate, 2);
        effect.GetParameter("0").Value = (float)TestPlugin.GainValue(6);
        var input = Sine(512, 2);

        effect.SetNormalized(TestPlugin.Crash, 1);
        var output = Run(effect, input);

        Assert.True(effect.HasCrashed);
        Assert.NotNull(effect.Problem);
        AssertClose(input, output, 0);
        AssertClose(input, Run(effect, input), 0);
    }

    private static float[] Sine(int frames, int channels)
    {
        var samples = new float[frames * channels];
        for (int frame = 0; frame < frames; frame++)
        {
            for (int channel = 0; channel < channels; channel++)
            {
                samples[(frame * channels) + channel] = 0.5f * MathF.Sin((frame * 0.05f) + channel);
            }
        }

        return samples;
    }

    private static float[] Run(AudioEffect effect, float[] input)
    {
        var buffer = (float[])input.Clone();
        effect.Process(buffer);
        return buffer;
    }

    private static void AssertClose(float[] expected, float[] actual, float tolerance)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.True(Math.Abs(expected[i] - actual[i]) <= tolerance, $"Sample {i}: expected {expected[i]}, got {actual[i]}.");
        }
    }
}
