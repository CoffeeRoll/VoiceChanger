using VoiceChanger.Core.Audio;
using VoiceChanger.Core.Dsp;

namespace VoiceChanger.Core.Graph;

/// <summary>Snapshot of the graph engine's state for display.</summary>
public readonly record struct GraphEngineStatus(
    bool IsRunning,
    int SampleRate,
    int Channels,
    int InputDevices,
    int OutputDevices,
    double EstimatedLatencyMilliseconds,
    int Underruns);

/// <summary>
/// Plays an <see cref="AudioGraph"/> on any number of input and output devices.
/// <para>
/// The first output node's device is the master clock: its render callback pulls audio from every
/// input device, runs the graph and pushes the result to the other output devices. Every other
/// device runs on its own clock, so each one is connected through a <see cref="ClockBridge"/> that
/// converts the sample rate and compensates the drift.
/// </para>
/// <para>
/// Editing the graph while it plays swaps in a new compiled plan without interrupting the sound;
/// only adding, removing or changing devices reopens them. Public members are meant to be called
/// from one (UI) thread, the same one that edits the graph.
/// </para>
/// </summary>
public sealed class GraphEngine : IDisposable
{
    private readonly object _gate = new();
    private readonly TimeProvider? _timeProvider;
    private Session? _session;
    private AudioEngineOptions? _options;
    private bool _disposed;

    public GraphEngine(IAudioBackend backend, AudioGraph graph, TimeProvider? timeProvider = null)
    {
        Backend = backend ?? throw new ArgumentNullException(nameof(backend));
        Graph = graph ?? throw new ArgumentNullException(nameof(graph));
        _timeProvider = timeProvider;
        Graph.Changed += OnGraphChanged;
    }

    public IAudioBackend Backend { get; }

    public AudioGraph Graph { get; }

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _session is not null;
            }
        }
    }

    /// <summary>
    /// Raised when the engine stopped by itself: a device failed (for example it was unplugged), or
    /// an edit needed devices that could not be opened. May be raised on any thread.
    /// </summary>
    public event EventHandler<AudioStreamStoppedEventArgs>? Faulted;

    /// <summary>The output node whose device sets the pace: the first one with a device.</summary>
    public static OutputDeviceNode? FindMasterOutput(AudioGraph graph) =>
        graph.Nodes.OfType<OutputDeviceNode>().FirstOrDefault(n => n.DeviceId is not null);

    /// <summary>Opens every device used by the graph and starts playing. A running session is stopped first.</summary>
    public void Start(AudioEngineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            StopCore();
            _session = Session.Open(this, options);
            _options = options;
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            StopCore();
        }
    }

    public GraphEngineStatus GetStatus()
    {
        lock (_gate)
        {
            return _session?.GetStatus() ?? default;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Graph.Changed -= OnGraphChanged;
        Stop();
        _disposed = true;
    }

    private void StopCore()
    {
        var session = _session;
        _session = null;
        _options = null;
        session?.Close();
    }

    private void OnGraphChanged(object? sender, EventArgs e)
    {
        Exception? failure = null;
        lock (_gate)
        {
            if (_session is null || _options is null)
            {
                return;
            }

            if (Session.DeviceSignature(Graph) == _session.Signature)
            {
                _session.Update();
                return;
            }

            // Devices were added, removed or swapped: reopen them all.
            var options = _options;
            StopCore();
            try
            {
                _session = Session.Open(this, options);
                _options = options;
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        }

        if (failure is not null)
        {
            Faulted?.Invoke(this, new AudioStreamStoppedEventArgs(failure));
        }
    }

    private void OnStreamStopped(object? sender, AudioStreamStoppedEventArgs e)
    {
        if (e.Error is null)
        {
            return;
        }

        // The event can be raised on the device thread itself, which must not be joined from here.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            lock (_gate)
            {
                if (_session is null || !_session.Owns(sender))
                {
                    return; // A stream from a previous session.
                }

                StopCore();
            }

            Faulted?.Invoke(this, e);
        });
    }

    private enum Fade
    {
        None,
        Out,
        In,
    }

    private sealed class InputDevice(string id, IAudioInputStream stream, ClockBridge bridge, int channels)
    {
        public string Id { get; } = id;

        public IAudioInputStream Stream { get; } = stream;

        public ClockBridge Bridge { get; } = bridge;

        /// <summary>The part of the captured audio the graph is processing now.</summary>
        public float[] Block { get; } = new float[GraphPlan.BlockFrames * channels];

        /// <summary>Capture thread: the device's audio converted to the graph's channel count.</summary>
        public float[] Mapped { get; set; } = [];

        /// <summary>Render thread: the audio pulled from the bridge for the current callback.</summary>
        public float[] Pulled { get; set; } = [];
    }

    private sealed class OutputDevice(string id, IAudioOutputStream stream, ClockBridge? bridge, int channels)
    {
        public string Id { get; } = id;

        public IAudioOutputStream Stream { get; } = stream;

        /// <summary>Null for the master output, which needs no bridge.</summary>
        public ClockBridge? Bridge { get; } = bridge;

        /// <summary>What the output nodes playing on this device add up to, for the block being processed.</summary>
        public float[] Block { get; } = new float[GraphPlan.BlockFrames * channels];

        /// <summary>Render thread (master): the device's mix for the whole callback.</summary>
        public float[] Mix { get; set; } = [];

        /// <summary>Render thread (this device): audio pulled from the bridge.</summary>
        public float[] Rendered { get; set; } = [];
    }

    private sealed class Session
    {
        private readonly GraphEngine _engine;
        private readonly AudioEngineOptions _options;
        private readonly InputDevice[] _inputs;
        private readonly OutputDevice[] _outputs;
        private GraphPlan _plan = null!;

        // Render thread state.
        private GraphPlan? _playing;
        private bool _fadedOut;

        private Session(GraphEngine engine, AudioEngineOptions options, string signature, int sampleRate, int channels, InputDevice[] inputs, OutputDevice[] outputs)
        {
            _engine = engine;
            _options = options;
            Signature = signature;
            SampleRate = sampleRate;
            Channels = channels;
            _inputs = inputs;
            _outputs = outputs;
        }

        public string Signature { get; }

        public int SampleRate { get; }

        public int Channels { get; }

        private OutputDevice Master => _outputs[0];

        /// <summary>The devices the graph uses; any change means they have to be reopened.</summary>
        public static string DeviceSignature(AudioGraph graph)
        {
            string master = FindMasterOutput(graph)?.DeviceId ?? "";
            var outputs = graph.Nodes.OfType<OutputDeviceNode>().Select(n => n.DeviceId).OfType<string>().Where(id => id != master).Distinct().Order(StringComparer.Ordinal);
            var inputs = graph.Nodes.OfType<InputDeviceNode>().Select(n => n.DeviceId).OfType<string>().Distinct().Order(StringComparer.Ordinal);
            return $"master:{master}|out:{string.Join(',', outputs)}|in:{string.Join(',', inputs)}";
        }

        public static Session Open(GraphEngine engine, AudioEngineOptions options)
        {
            var graph = engine.Graph;
            var backend = engine.Backend;
            string masterId = FindMasterOutput(graph)?.DeviceId
                ?? throw new InvalidOperationException("Add an Audio Output node and choose a device for it.");
            var outputIds = graph.Nodes.OfType<OutputDeviceNode>().Select(n => n.DeviceId).OfType<string>().Where(id => id != masterId).Distinct().Prepend(masterId).ToList();
            var inputIds = graph.Nodes.OfType<InputDeviceNode>().Select(n => n.DeviceId).OfType<string>().Distinct().ToList();
            int channels = options.MonoProcessing ? 1 : 2;

            var inputs = new List<InputDevice>();
            var outputs = new List<OutputDevice>();
            var opened = new List<IDisposable>();
            try
            {
                // The master output decides the graph's sample rate; every other device is converted to it.
                var masterStream = backend.OpenOutput(masterId, options.OutputLatencyMilliseconds);
                opened.Add(masterStream);
                AudioProcessor.ValidateFormat(masterStream.Format, nameof(options));
                int sampleRate = masterStream.Format.SampleRate;
                outputs.Add(new OutputDevice(masterId, masterStream, null, channels));

                foreach (string id in outputIds.Skip(1))
                {
                    var stream = backend.OpenOutput(id, options.OutputLatencyMilliseconds);
                    opened.Add(stream);
                    AudioProcessor.ValidateFormat(stream.Format, nameof(options));
                    var bridge = new ClockBridge(sampleRate, stream.Format.SampleRate, channels, options.SafetyBufferMilliseconds, engine._timeProvider);
                    outputs.Add(new OutputDevice(id, stream, bridge, channels));
                }

                foreach (string id in inputIds)
                {
                    var stream = backend.OpenInput(id, options.CaptureBufferMilliseconds);
                    opened.Add(stream);
                    AudioProcessor.ValidateFormat(stream.Format, nameof(options));
                    var bridge = new ClockBridge(stream.Format.SampleRate, sampleRate, channels, options.SafetyBufferMilliseconds, engine._timeProvider);
                    inputs.Add(new InputDevice(id, stream, bridge, channels));
                }

                var session = new Session(engine, options, DeviceSignature(graph), sampleRate, channels, inputs.ToArray(), outputs.ToArray());
                session.Update();
                session.StartStreams();
                return session;
            }
            catch
            {
                foreach (var input in inputs)
                {
                    input.Stream.Stopped -= engine.OnStreamStopped;
                }

                foreach (var output in outputs)
                {
                    output.Stream.Stopped -= engine.OnStreamStopped;
                }

                foreach (var stream in opened)
                {
                    stream.Dispose();
                }

                throw;
            }
        }

        public bool Owns(object? stream) =>
            _inputs.Any(i => ReferenceEquals(i.Stream, stream)) || _outputs.Any(o => ReferenceEquals(o.Stream, stream));

        /// <summary>Prepares new nodes and publishes a freshly compiled plan to the render thread.</summary>
        public void Update()
        {
            foreach (var node in _engine.Graph.Nodes)
            {
                // Nodes already playing keep their state; only new ones (or a new format) are prepared.
                if (node.SampleRate != SampleRate || node.Channels != Channels)
                {
                    node.Prepare(SampleRate, Channels);
                }
            }

            var plan = GraphPlan.Compile(_engine.Graph, Channels, DeviceBlock);
            Volatile.Write(ref _plan, plan);
        }

        public GraphEngineStatus GetStatus()
        {
            var plan = Volatile.Read(ref _plan);
            double inputDelay = 0;
            if (_inputs.Length > 0)
            {
                inputDelay = _options.CaptureBufferMilliseconds + _inputs.Max(i => i.Bridge.BufferedMilliseconds + i.Bridge.ResamplerLatencyMilliseconds);
            }

            double latency = inputDelay + Master.Stream.LatencyMilliseconds + plan.LatencyFrames() * 1000.0 / SampleRate;
            int underruns = _inputs.Sum(i => i.Bridge.Underruns) + _outputs.Sum(o => o.Bridge?.Underruns ?? 0);
            return new GraphEngineStatus(true, SampleRate, Channels, _inputs.Length, _outputs.Length, latency, underruns);
        }

        public void Close()
        {
            foreach (var input in _inputs)
            {
                input.Stream.Stopped -= _engine.OnStreamStopped;
                input.Stream.Stop();
                input.Stream.Dispose();
            }

            foreach (var output in _outputs)
            {
                output.Stream.Stopped -= _engine.OnStreamStopped;
                output.Stream.Stop();
                output.Stream.Dispose();
            }
        }

        private float[]? DeviceBlock(DeviceNode node) => node switch
        {
            InputDeviceNode => _inputs.FirstOrDefault(i => i.Id == node.DeviceId)?.Block,
            OutputDeviceNode => _outputs.FirstOrDefault(o => o.Id == node.DeviceId)?.Block,
            _ => null,
        };

        private void StartStreams()
        {
            foreach (var input in _inputs)
            {
                input.Stream.Stopped += _engine.OnStreamStopped;
            }

            foreach (var output in _outputs)
            {
                output.Stream.Stopped += _engine.OnStreamStopped;
            }

            // Outputs first (they play silence until audio arrives), the master last: it drives everything.
            foreach (var output in _outputs.Skip(1))
            {
                output.Stream.Start(buffer => RenderSecondary(output, buffer));
            }

            Master.Stream.Start(RenderMaster);
            foreach (var input in _inputs)
            {
                input.Stream.Start(data => Capture(input, data));
            }
        }

        /// <summary>Capture thread of one input device.</summary>
        private void Capture(InputDevice device, ReadOnlySpan<float> interleaved)
        {
            int deviceChannels = device.Stream.Format.Channels;
            int frames = interleaved.Length / deviceChannels;
            if (frames == 0)
            {
                return;
            }

            if (device.Mapped.Length < frames * Channels)
            {
                device.Mapped = new float[frames * Channels];
            }

            var mapped = device.Mapped.AsSpan(0, frames * Channels);
            ChannelMapper.Map(interleaved, deviceChannels, mapped, Channels, frames);
            device.Bridge.Write(mapped);
        }

        /// <summary>Render thread of the master output: runs the whole graph.</summary>
        private void RenderMaster(Span<float> interleaved)
        {
            int deviceChannels = Master.Stream.Format.Channels;
            int frames = interleaved.Length / deviceChannels;
            if (frames == 0)
            {
                return;
            }

            int samples = frames * Channels;

            // One read per callback so each bridge sees the real block size.
            foreach (var input in _inputs)
            {
                if (input.Pulled.Length < samples)
                {
                    input.Pulled = new float[samples];
                }

                input.Bridge.Read(input.Pulled.AsSpan(0, samples));
            }

            foreach (var output in _outputs)
            {
                if (output.Mix.Length < samples)
                {
                    output.Mix = new float[samples];
                }
            }

            var (plan, fade) = NextPlan();
            for (int offset = 0; offset < frames; offset += GraphPlan.BlockFrames)
            {
                int count = Math.Min(GraphPlan.BlockFrames, frames - offset);
                int start = offset * Channels;
                int length = count * Channels;
                foreach (var input in _inputs)
                {
                    input.Pulled.AsSpan(start, length).CopyTo(input.Block);
                }

                foreach (var output in _outputs)
                {
                    output.Block.AsSpan(0, length).Clear();
                }

                plan.Process(count);
                foreach (var output in _outputs)
                {
                    output.Block.AsSpan(0, length).CopyTo(output.Mix.AsSpan(start));
                }
            }

            foreach (var output in _outputs)
            {
                ApplyFade(output.Mix.AsSpan(0, samples), fade);
            }

            ChannelMapper.Map(Master.Mix, Channels, interleaved, deviceChannels, frames);
            AudioProcessor.ApplyGainAndClamp(interleaved, 1);
            foreach (var output in _outputs.AsSpan(1))
            {
                output.Bridge!.Write(output.Mix.AsSpan(0, samples));
            }
        }

        /// <summary>Render thread of another output device: plays what the master produced for it.</summary>
        private void RenderSecondary(OutputDevice device, Span<float> interleaved)
        {
            int deviceChannels = device.Stream.Format.Channels;
            int frames = interleaved.Length / deviceChannels;
            if (frames == 0)
            {
                return;
            }

            if (device.Rendered.Length < frames * Channels)
            {
                device.Rendered = new float[frames * Channels];
            }

            var block = device.Rendered.AsSpan(0, frames * Channels);
            device.Bridge!.Read(block);
            ChannelMapper.Map(block, Channels, interleaved, deviceChannels, frames);
            AudioProcessor.ApplyGainAndClamp(interleaved, 1);
        }

        /// <summary>
        /// Picks the plan for this callback. When the wiring changed, the old plan fades out over one
        /// callback and the new one fades in over the next, so re-patching never clicks.
        /// </summary>
        private (GraphPlan Plan, Fade Fade) NextPlan()
        {
            var target = Volatile.Read(ref _plan);
            if (_playing is null || ReferenceEquals(target, _playing) || target.Routing == _playing.Routing)
            {
                _playing = target;
                _fadedOut = false;
                return (target, Fade.None);
            }

            if (!_fadedOut)
            {
                _fadedOut = true;
                return (_playing, Fade.Out);
            }

            _playing = target;
            _fadedOut = false;
            return (target, Fade.In);
        }

        private void ApplyFade(Span<float> block, Fade fade)
        {
            if (fade == Fade.None)
            {
                return;
            }

            int frames = block.Length / Channels;
            for (int frame = 0, i = 0; frame < frames; frame++)
            {
                float ramp = (frame + 1f) / frames;
                float gain = fade == Fade.In ? ramp : 1 - ramp;
                for (int ch = 0; ch < Channels; ch++, i++)
                {
                    block[i] *= gain;
                }
            }
        }
    }
}
