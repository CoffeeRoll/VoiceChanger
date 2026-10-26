using VoiceChanger.Core.Audio;
using VoiceChanger.Core.Effects;
using static VoiceChanger.Core.Tests.SignalHelpers;

namespace VoiceChanger.Core.Tests;

public class AudioProcessorTests
{
    /// <summary>
    /// Simulates a capture device and a render device running on independent clocks with
    /// callback jitter and returns the processor plus everything that was rendered.
    /// </summary>
    private static (AudioProcessor Processor, List<float> Output, int WarmupUnderruns) Simulate(
        int inputRate,
        int outputRate,
        double inputClockError,
        double jitterMilliseconds,
        double seconds,
        int safetyMilliseconds = 5,
        double frequency = 440)
    {
        var chain = new EffectChain();
        chain.Prepare(inputRate, 1);
        var clock = new ManualClock();
        var processor = new AudioProcessor(new AudioFormat(inputRate, 1), new AudioFormat(outputRate, 2), 1, chain, safetyMilliseconds, clock);
        var random = new Random(42);
        var output = new List<float>();

        int captureFrames = inputRate / 100;
        int renderFrames = outputRate / 100;
        double capturePeriod = captureFrames / (inputRate * (1 + inputClockError));
        double renderPeriod = renderFrames / (double)outputRate;
        int captureIndex = 0;
        int renderIndex = 0;
        double nextCapture = 0.003;
        double nextRender = 0;
        double Jitter() => (random.NextDouble() * 2 - 1) * jitterMilliseconds / 1000;
        long phase = 0;
        int warmupUnderruns = -1;
        var block = new float[captureFrames];

        while (Math.Min(nextCapture, nextRender) < seconds)
        {
            if (nextCapture <= nextRender)
            {
                clock.Seconds = nextCapture;
                for (int i = 0; i < captureFrames; i++, phase++)
                {
                    block[i] = (float)(0.5 * Math.Sin(2 * Math.PI * frequency * phase / inputRate));
                }

                processor.ProcessInput(block);
                nextCapture = 0.003 + ++captureIndex * capturePeriod + Jitter();
            }
            else
            {
                clock.Seconds = nextRender;
                var buffer = new float[renderFrames * 2];
                processor.RenderOutput(buffer);
                output.AddRange(Channel(buffer, 2, 0));

                // Callbacks are never perfectly on time.
                nextRender = ++renderIndex * renderPeriod + Jitter();
            }

            if (warmupUnderruns < 0 && Math.Min(nextCapture, nextRender) > 3)
            {
                warmupUnderruns = processor.Underruns;
            }
        }

        return (processor, output, Math.Max(0, warmupUnderruns));
    }

    private static void PushInBlocks(AudioProcessor processor, float[] samples, int blockSamples)
    {
        for (int offset = 0; offset < samples.Length; offset += blockSamples)
        {
            processor.ProcessInput(samples.AsSpan(offset, Math.Min(blockSamples, samples.Length - offset)));
        }
    }

    [Theory]
    [InlineData(48000, 48000, 0.0)]
    [InlineData(48000, 48000, 150e-6)]
    [InlineData(48000, 48000, -150e-6)]
    [InlineData(44100, 48000, 80e-6)]
    [InlineData(48000, 44100, -80e-6)]
    public void Keeps_latency_low_and_stable_with_clock_drift(int inputRate, int outputRate, double drift)
    {
        var (processor, output, warmupUnderruns) = Simulate(inputRate, outputRate, drift, jitterMilliseconds: 1.5, seconds: 60);

        // No dropouts once the controller has settled.
        Assert.Equal(warmupUnderruns, processor.Underruns);
        Assert.Equal(0, processor.Overflows);

        // The queue stays close to one input block + one output block + the safety margin
        // instead of growing or draining.
        Assert.InRange(processor.BufferedMilliseconds, 10, 28);
        Assert.InRange(processor.DriftCorrectionPpm, -2000, 2000);

        // The tone arrives at the right pitch.
        var tail = output.ToArray().AsSpan(output.Count - outputRate);
        Assert.Equal(440, DominantFrequency(tail, outputRate), 0.5);
        Assert.Equal(0.5 / Math.Sqrt(2), Rms(tail), 2);
    }

    [Fact]
    public void Plays_silence_until_enough_input_has_arrived()
    {
        var chain = new EffectChain();
        chain.Prepare(48000, 1);
        var processor = new AudioProcessor(new AudioFormat(48000, 1), new AudioFormat(48000, 2), 1, chain, safetyMilliseconds: 5);

        var silent = new float[960];
        processor.RenderOutput(silent);
        Assert.All(silent, s => Assert.Equal(0, s));

        // One 10 ms block is not enough: the output waits for an input block + its own block + 5 ms.
        processor.ProcessInput(Enumerable.Repeat(0.25f, 480).ToArray());
        processor.RenderOutput(silent);
        Assert.All(silent, s => Assert.Equal(0, s));

        PushInBlocks(processor, Enumerable.Repeat(0.25f, 1920).ToArray(), 480);
        var rendered = new float[960];
        processor.RenderOutput(rendered);
        Assert.Equal(0.25f, rendered[^1], 3);
        Assert.Equal(rendered[^1], rendered[^2]);
        Assert.Equal(0, processor.Underruns);
    }

    [Fact]
    public void Applies_gains_and_effects_and_meters_levels()
    {
        var chain = new EffectChain();
        var eq = new EqualizerEffect();
        eq.OutputGain.Value = -6;
        chain.Add(eq);
        chain.Prepare(48000, 1);
        chain.Process(new float[16]); // let the enable cross-fade finish

        var processor = new AudioProcessor(new AudioFormat(48000, 2), new AudioFormat(48000, 2), 1, chain, safetyMilliseconds: 0)
        {
            InputGain = 2,
            OutputGain = 0.5f,
        };

        PushInBlocks(processor, Enumerable.Repeat(0.2f, 9600).ToArray(), 960);
        var rendered = new float[1000];
        processor.RenderOutput(rendered);

        // 0.2 * 2 (input) * 0.5 (EQ -6 dB) * 0.5 (output)
        Assert.Equal(0.2 * 2 * Math.Pow(10, -6 / 20.0) * 0.5, rendered[^1], 3);
        Assert.Equal(0.4f, processor.InputMeter.ReadAndReset(), 3);
        Assert.Equal(0f, processor.InputMeter.ReadAndReset());
        Assert.True(processor.OutputMeter.ReadAndReset() > 0.09f);
    }

    [Fact]
    public void Output_is_clamped_to_full_scale()
    {
        var chain = new EffectChain();
        chain.Prepare(48000, 1);
        var processor = new AudioProcessor(new AudioFormat(48000, 1), new AudioFormat(48000, 1), 1, chain, 0) { OutputGain = 10 };
        PushInBlocks(processor, Enumerable.Repeat(0.5f, 4800).ToArray(), 480);
        var rendered = new float[1000];
        processor.RenderOutput(rendered);
        Assert.All(rendered, s => Assert.InRange(s, -1f, 1f));
        Assert.Equal(1f, rendered[^1]);
    }
}

internal sealed class ManualClock : TimeProvider
{
    public double Seconds { get; set; }

    public override long TimestampFrequency => 1_000_000_000;

    public override long GetTimestamp() => (long)(Seconds * 1e9);
}

public class AudioEngineTests
{
    [Fact]
    public void Start_wires_the_devices_through_the_effect_chain()
    {
        var backend = new FakeAudioBackend { InputFormat = new(44100, 2), OutputFormat = new(48000, 2) };
        using var engine = new AudioEngine(backend);
        engine.Effects.Add(new PitchShiftEffect());

        engine.Start("mic", "speakers", AudioEngineOptions.FromPreset(LatencyPreset.Low));

        Assert.True(engine.IsRunning);
        var input = Assert.Single(backend.Inputs);
        var output = Assert.Single(backend.Outputs);
        Assert.True(input.IsStarted);
        Assert.True(output.IsStarted);
        Assert.Equal("mic", input.DeviceId);
        Assert.Equal("speakers", output.DeviceId);
        Assert.Equal(44100, engine.Effects.SampleRate);
        Assert.Equal(1, engine.Effects.Channels);

        input.Push(Sine(440, 44100, 4410, channels: 2));
        output.Pull(480);

        var status = engine.GetStatus();
        Assert.True(status.IsRunning);
        Assert.Equal(new AudioFormat(44100, 2), status.InputFormat);
        Assert.Equal(new AudioFormat(48000, 2), status.OutputFormat);
        Assert.True(status.InputPeak > 0.4f);
        Assert.InRange(status.EstimatedLatencyMilliseconds, 30, 200);

        engine.Stop();
        Assert.False(engine.IsRunning);
        Assert.True(input.IsDisposed);
        Assert.True(output.IsDisposed);
        Assert.False(engine.GetStatus().IsRunning);
    }

    [Fact]
    public void Stereo_processing_keeps_two_channels()
    {
        var backend = new FakeAudioBackend { InputFormat = new(48000, 2) };
        using var engine = new AudioEngine(backend);
        engine.Start("mic", "speakers", AudioEngineOptions.FromPreset(LatencyPreset.Low, monoProcessing: false));
        Assert.Equal(2, engine.Effects.Channels);
    }

    [Fact]
    public void Restarting_closes_the_previous_devices()
    {
        var backend = new FakeAudioBackend();
        using var engine = new AudioEngine(backend);
        engine.Start("mic", "speakers", new AudioEngineOptions());
        engine.Start("line", "cable", new AudioEngineOptions());

        Assert.True(backend.Inputs[0].IsDisposed);
        Assert.True(backend.Outputs[0].IsDisposed);
        Assert.False(backend.Inputs[1].IsDisposed);
        Assert.Equal("cable", backend.Outputs[1].DeviceId);
    }

    [Fact]
    public void Failure_to_open_a_device_cleans_up_and_rethrows()
    {
        var backend = new FakeAudioBackend { OpenOutputError = new InvalidOperationException("busy") };
        using var engine = new AudioEngine(backend);

        var error = Assert.Throws<InvalidOperationException>(() => engine.Start("mic", "speakers", new AudioEngineOptions()));
        Assert.Equal("busy", error.Message);
        Assert.False(engine.IsRunning);
        Assert.True(Assert.Single(backend.Inputs).IsDisposed);
    }

    [Fact]
    public async Task Device_failure_stops_the_engine_and_reports_it()
    {
        var backend = new FakeAudioBackend();
        using var engine = new AudioEngine(backend);
        var faulted = new TaskCompletionSource<Exception?>();
        engine.Faulted += (_, e) => faulted.TrySetResult(e.Error);
        engine.Start("mic", "speakers", new AudioEngineOptions());

        backend.Inputs[0].Fail(new IOException("unplugged"));

        var error = await faulted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.IsType<IOException>(error);
        Assert.False(engine.IsRunning);
        Assert.True(backend.Outputs[0].IsDisposed);
    }

    [Fact]
    public void Gains_can_be_changed_while_running()
    {
        var backend = new FakeAudioBackend();
        using var engine = new AudioEngine(backend);
        engine.Start("mic", "speakers", new AudioEngineOptions { SafetyBufferMilliseconds = 0 });
        engine.OutputGainDb = -6;

        for (int i = 0; i < 10; i++)
        {
            backend.Inputs[0].Push(Enumerable.Repeat(0.5f, 480).ToArray());
        }

        var rendered = backend.Outputs[0].Pull(480);
        Assert.Equal(0.5 * Math.Pow(10, -6 / 20.0), rendered[^1], 3);
    }
}
