using VoiceChanger.Core.Audio;
using VoiceChanger.Core.Effects;
using VoiceChanger.Core.Graph;
using static VoiceChanger.Core.Tests.SignalHelpers;

namespace VoiceChanger.Core.Tests;

public class GraphEngineTests
{

    private static readonly AudioEngineOptions Options = AudioEngineOptions.FromPreset(LatencyPreset.Low);

    /// <summary>Mic and line in → mixer → speakers, the default devices of the fake backend.</summary>
    private static (AudioGraph Graph, InputDeviceNode Mic, InputDeviceNode Line, MixerNode Mixer, OutputDeviceNode Speakers) TwoInputs()
    {
        var graph = new AudioGraph();
        var mic = graph.Add(new InputDeviceNode { DeviceId = "mic" });
        var line = graph.Add(new InputDeviceNode { DeviceId = "line" });
        var mixer = graph.Add(new MixerNode());
        var speakers = graph.Add(new OutputDeviceNode { DeviceId = "speakers" });
        graph.Connect(mic.Outputs[0], mixer.Inputs[0]);
        graph.Connect(line.Outputs[0], mixer.Inputs[1]);
        graph.Connect(mixer.Outputs[0], speakers.Inputs[0]);
        return (graph, mic, line, mixer, speakers);
    }

    private static FakeInputStream Input(FakeAudioBackend backend, string id) => backend.Inputs.Last(i => i.DeviceId == id);

    private static FakeOutputStream Output(FakeAudioBackend backend, string id) => backend.Outputs.Last(o => o.DeviceId == id);

    /// <summary>Feeds every input 10 ms blocks of a sine and renders the outputs in step; returns what each output played (left channel).</summary>
    private static Dictionary<string, List<float>> Play(FakeAudioBackend backend, Dictionary<string, double> inputFrequencies, double seconds)
    {
        var outputs = backend.Outputs.Where(o => o.IsStarted).ToList();
        var result = outputs.ToDictionary(o => o.DeviceId, _ => new List<float>());
        var phases = inputFrequencies.ToDictionary(p => p.Key, _ => 0L);
        for (int block = 0; block < seconds * 100; block++)
        {
            foreach (var (id, frequency) in inputFrequencies)
            {
                var input = Input(backend, id);
                int frames = input.Format.SampleRate / 100;
                var samples = new float[frames * input.Format.Channels];
                for (int i = 0; i < frames; i++, phases[id]++)
                {
                    for (int ch = 0; ch < input.Format.Channels; ch++)
                    {
                        samples[i * input.Format.Channels + ch] = (float)(0.25 * Math.Sin(2 * Math.PI * frequency * phases[id] / input.Format.SampleRate));
                    }
                }

                input.Push(samples);
            }

            foreach (var output in outputs)
            {
                result[output.DeviceId].AddRange(Channel(output.Pull(output.Format.SampleRate / 100), output.Format.Channels, 0));
            }
        }

        return result;
    }

    [Fact]
    public void Two_inputs_are_mixed_into_one_output()
    {
        var backend = new FakeAudioBackend { OutputFormat = new(48000, 2) };
        backend.DeviceFormats["mic"] = new(44100, 1);
        backend.DeviceFormats["line"] = new(48000, 2);
        var (graph, _, _, mixer, _) = TwoInputs();
        using var engine = new GraphEngine(backend, graph);

        engine.Start(Options);

        Assert.True(engine.IsRunning);
        Assert.Equal(["mic", "line"], backend.Inputs.Select(i => i.DeviceId));
        Assert.All(backend.Inputs, i => Assert.True(i.IsStarted));
        Assert.True(Output(backend, "speakers").IsStarted);
        Assert.Equal(48000, mixer.SampleRate);
        Assert.Equal(1, mixer.Channels);

        var played = Play(backend, new() { ["mic"] = 440, ["line"] = 1250 }, seconds: 1)["speakers"].ToArray();
        var tail = played.AsSpan(played.Length / 2);
        Assert.Equal(0.25, ToneAmplitude(tail, 440, 48000), 0.02);
        Assert.Equal(0.25, ToneAmplitude(tail, 1250, 48000), 0.02);

        var status = engine.GetStatus();
        Assert.True(status.IsRunning);
        Assert.Equal((48000, 1, 2, 1), (status.SampleRate, status.Channels, status.InputDevices, status.OutputDevices));
        Assert.InRange(status.EstimatedLatencyMilliseconds, 25, 80);
        Assert.True(mixer.Meter.ReadAndReset() > 0.3f);
    }

    [Fact]
    public void Other_output_devices_play_their_own_mix()
    {
        var backend = new FakeAudioBackend();
        backend.DeviceFormats["cable"] = new(44100, 2);
        var graph = new AudioGraph();
        var mic = graph.Add(new InputDeviceNode { DeviceId = "mic" });
        var tone = graph.Add(new ToneNode());
        tone.Frequency.Value = 1000;
        var speakers = graph.Add(new OutputDeviceNode { DeviceId = "speakers" });
        var cable = graph.Add(new OutputDeviceNode { DeviceId = "cable" });
        graph.Connect(mic.Outputs[0], speakers.Inputs[0]);
        graph.Connect(tone.Outputs[0], cable.Inputs[0]);
        using var engine = new GraphEngine(backend, graph);

        engine.Start(Options);
        Assert.Equal(2, engine.GetStatus().OutputDevices);

        var played = Play(backend, new() { ["mic"] = 440 }, seconds: 1);

        // The speakers hear the mic, the cable (on its own clock and sample rate) the tone.
        var toSpeakers = played["speakers"].ToArray().AsSpan(24000);
        var toCable = played["cable"].ToArray().AsSpan(22050);
        Assert.Equal(0.25, ToneAmplitude(toSpeakers, 440, 48000), 0.02);
        Assert.True(ToneAmplitude(toSpeakers, 1000, 48000) < 0.005);
        Assert.Equal(0.126, ToneAmplitude(toCable, 1000, 44100), 0.01);
        Assert.True(ToneAmplitude(toCable, 440, 44100) < 0.005);
    }

    [Fact]
    public void Editing_the_graph_while_playing_keeps_the_devices_open()
    {
        var backend = new FakeAudioBackend();
        var (graph, mic, line, mixer, speakers) = TwoInputs();
        using var engine = new GraphEngine(backend, graph);
        engine.Start(Options);
        Play(backend, new() { ["mic"] = 440, ["line"] = 1250 }, seconds: 0.5);

        var eq = graph.Add(new EffectNode(new EqualizerEffect()));
        graph.Connect(mixer.Outputs[0], eq.Inputs[0]);
        graph.Connect(eq.Outputs[0], speakers.Inputs[0]);
        mixer.Mutes[0].Value = 1;
        graph.Disconnect(graph.ConnectionTo(mixer.Inputs[0])!);
        graph.Connect(mic.Outputs[0], mixer.Inputs[1]);

        Assert.Equal(2, backend.Inputs.Count);
        Assert.Single(backend.Outputs);
        Assert.True(eq.IsPrepared);

        var played = Play(backend, new() { ["mic"] = 440, ["line"] = 1250 }, seconds: 0.5)["speakers"].ToArray();
        var tail = played.AsSpan(played.Length / 2);
        Assert.Equal(0.25, ToneAmplitude(tail, 440, 48000), 0.03);
        Assert.True(ToneAmplitude(tail, 1250, 48000) < 0.005);
        Assert.True(eq.Meter.ReadAndReset() > 0.1f);

        // An input that is not wired anywhere still shows its level.
        Assert.True(line.Meter.ReadAndReset() > 0.1f);
    }

    [Fact]
    public void Changing_devices_while_playing_reopens_them()
    {
        var backend = new FakeAudioBackend();
        var (graph, mic, line, _, speakers) = TwoInputs();
        using var engine = new GraphEngine(backend, graph);
        engine.Start(Options);
        var firstSpeakers = Output(backend, "speakers");

        speakers.DeviceId = "cable";

        Assert.True(engine.IsRunning);
        Assert.True(firstSpeakers.IsDisposed);
        Assert.True(Output(backend, "cable").IsStarted);
        Assert.Equal(4, backend.Inputs.Count);
        Assert.True(Input(backend, "mic").IsStarted);

        // Both input nodes on the same device share one stream.
        line.DeviceId = "mic";
        Assert.Equal(1, engine.GetStatus().InputDevices);
        var played = Play(backend, new() { ["mic"] = 440 }, seconds: 0.5)["cable"].ToArray();
        Assert.Equal(0.5, ToneAmplitude(played.AsSpan(12000), 440, 48000), 0.03);
        _ = mic;
    }

    [Fact]
    public void An_output_device_is_needed_to_start()
    {
        var backend = new FakeAudioBackend();
        var graph = new AudioGraph();
        graph.Add(new InputDeviceNode { DeviceId = "mic" });
        var output = graph.Add(new OutputDeviceNode());
        using var engine = new GraphEngine(backend, graph);

        var error = Assert.Throws<InvalidOperationException>(() => engine.Start(Options));
        Assert.Contains("Audio Output", error.Message);
        Assert.False(engine.IsRunning);
        Assert.Empty(backend.Inputs);

        output.DeviceId = "speakers";
        engine.Start(Options);
        Assert.True(engine.IsRunning);
    }

    [Fact]
    public void Removing_the_last_output_while_playing_stops_with_an_explanation()
    {
        var backend = new FakeAudioBackend();
        var (graph, _, _, _, speakers) = TwoInputs();
        using var engine = new GraphEngine(backend, graph);
        engine.Start(Options);
        Exception? error = null;
        engine.Faulted += (_, e) => error = e.Error;

        graph.Remove(speakers);

        Assert.False(engine.IsRunning);
        Assert.IsType<InvalidOperationException>(error);
        Assert.All(backend.Inputs, i => Assert.True(i.IsDisposed));
    }

    [Fact]
    public async Task A_failing_device_stops_everything()
    {
        var backend = new FakeAudioBackend();
        var (graph, _, _, _, _) = TwoInputs();
        graph.Add(new OutputDeviceNode { DeviceId = "cable" });
        using var engine = new GraphEngine(backend, graph);
        engine.Start(Options);
        var faulted = new TaskCompletionSource<Exception?>();
        engine.Faulted += (_, e) => faulted.TrySetResult(e.Error);

        Output(backend, "cable").Fail(new IOException("unplugged"));

        var error = await faulted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal("unplugged", error?.Message);
        Assert.False(engine.IsRunning);
        Assert.All(backend.Outputs, o => Assert.True(o.IsDisposed));
        Assert.All(backend.Inputs, i => Assert.True(i.IsDisposed));
    }

    [Fact]
    public void A_device_that_fails_to_open_closes_the_others()
    {
        var backend = new FakeAudioBackend();
        var (graph, _, _, _, _) = TwoInputs();
        graph.Add(new OutputDeviceNode { DeviceId = "cable" });
        backend.DeviceFormats["line"] = new(48000, 0);
        using var engine = new GraphEngine(backend, graph);

        Assert.Throws<ArgumentException>(() => engine.Start(Options));

        Assert.False(engine.IsRunning);
        Assert.All(backend.Outputs, o => Assert.True(o.IsDisposed));
        Assert.All(backend.Inputs, i => Assert.True(i.IsDisposed));
    }

    [Fact]
    public void Devices_on_drifting_clocks_play_without_dropouts()
    {
        // Mic 0.05 % fast at 44.1 kHz, speakers at 48 kHz (master), cable 0.03 % slow at 44.1 kHz.
        var backend = new FakeAudioBackend();
        backend.DeviceFormats["mic"] = new(44100, 1);
        backend.DeviceFormats["speakers"] = new(48000, 2);
        backend.DeviceFormats["cable"] = new(44100, 2);
        var graph = new AudioGraph();
        var mic = graph.Add(new InputDeviceNode { DeviceId = "mic" });
        var speakers = graph.Add(new OutputDeviceNode { DeviceId = "speakers" });
        var cable = graph.Add(new OutputDeviceNode { DeviceId = "cable" });
        graph.Connect(mic.Outputs[0], speakers.Inputs[0]);
        graph.Connect(mic.Outputs[0], cable.Inputs[0]);
        var clock = new ManualClock();
        using var engine = new GraphEngine(backend, graph, clock);
        engine.Start(Options);

        var micStream = Input(backend, "mic");
        var speakerStream = Output(backend, "speakers");
        var cableStream = Output(backend, "cable");
        double[] periods = [0.01 / 1.0005, 0.01, 0.01 / 0.9997];
        double[] starts = [0.003, 0, 0.005];
        double[] next = [.. starts];
        int[] counts = new int[3];
        var random = new Random(7);
        var cableOutput = new List<float>();
        long phase = 0;
        int warmupUnderruns = -1;
        var block = new float[441];
        while (next.Min() < 20)
        {
            int device = Array.IndexOf(next, next.Min());
            clock.Seconds = next[device];
            if (device == 0)
            {
                for (int i = 0; i < block.Length; i++, phase++)
                {
                    block[i] = (float)(0.5 * Math.Sin(2 * Math.PI * 440 * phase / 44100));
                }

                micStream.Push(block);
            }
            else if (device == 1)
            {
                speakerStream.Pull(480);
            }
            else
            {
                cableOutput.AddRange(Channel(cableStream.Pull(441), 2, 0));
            }

            // Steady clocks whose callbacks are never perfectly on time.
            next[device] = starts[device] + ++counts[device] * periods[device] + (random.NextDouble() - 0.5) * 0.002;
            if (warmupUnderruns < 0 && next.Min() > 3)
            {
                warmupUnderruns = engine.GetStatus().Underruns;
            }
        }

        Assert.Equal(warmupUnderruns, engine.GetStatus().Underruns);
        var tail = cableOutput.ToArray().AsSpan(cableOutput.Count - 44100);
        Assert.Equal(440, DominantFrequency(tail, 44100), 1.0);

        // Drift compensation bends the pitch by a fraction of a hertz, so measure the level by its RMS.
        Assert.Equal(0.5, Rms(tail) * Math.Sqrt(2), 0.02);
        Assert.InRange(engine.GetStatus().EstimatedLatencyMilliseconds, 20, 90);
    }
}
