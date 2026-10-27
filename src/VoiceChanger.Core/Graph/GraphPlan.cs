namespace VoiceChanger.Core.Graph;

/// <summary>
/// An immutable, ready-to-run snapshot of an <see cref="AudioGraph"/>: the nodes that matter in an
/// order where every node runs after the nodes feeding it, with a buffer for every output.
/// <para>
/// Only nodes that lead to an output (and input devices, so their meters keep working) are processed;
/// anything else would cost CPU without being heard.
/// </para>
/// </summary>
internal sealed class GraphPlan
{
    /// <summary>Largest number of frames processed in one go; longer buffers are split.</summary>
    public const int BlockFrames = 1024;

    private readonly Step[] _steps;
    private readonly float[] _silence;
    private readonly int[] _pathLatency;

    private GraphPlan(Step[] steps, int channels, string routing)
    {
        _steps = steps;
        Channels = channels;
        Routing = routing;
        _silence = new float[BlockFrames * channels];
        _pathLatency = new int[steps.Length];
    }

    public int Channels { get; }

    /// <summary>The processed nodes, in processing order.</summary>
    public IReadOnlyList<AudioNode> Order => _steps.Select(s => s.Node).ToArray();

    /// <summary>Describes which node feeds which: equal for plans that sound the same.</summary>
    public string Routing { get; }

    /// <summary>
    /// Builds a plan. <paramref name="deviceBuffer"/> returns, for a device node, the buffer holding
    /// the device's captured audio (inputs) or its mix (outputs), or <c>null</c> when it has no open device.
    /// </summary>
    public static GraphPlan Compile(AudioGraph graph, int channels, Func<DeviceNode, float[]?> deviceBuffer)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);

        var live = FindLiveNodes(graph);
        var order = SortTopologically(graph, live);
        var steps = new Step[order.Count];
        var outputBuffers = new Dictionary<NodePort, float[]>();
        var stepIndex = new Dictionary<AudioNode, int>();
        var routing = new System.Text.StringBuilder();

        for (int s = 0; s < order.Count; s++)
        {
            var node = order[s];
            var inputs = new float[]?[node.Inputs.Count];
            var sources = new int[inputs.Length];
            var upstream = new List<int>();
            routing.Append(node.Id.ToString("N")).Append('(');
            for (int i = 0; i < inputs.Length; i++)
            {
                var connection = graph.ConnectionTo(node.Inputs[i]);
                sources[i] = -1;
                if (connection is not null && outputBuffers.TryGetValue(connection.From, out var buffer))
                {
                    inputs[i] = buffer;
                    sources[i] = stepIndex[connection.From.Node];
                    upstream.Add(sources[i]);
                    routing.Append(connection.From.Node.Id.ToString("N")).Append('.').Append(connection.From.Id);
                }

                routing.Append(',');
            }

            routing.Append(')');
            var outputs = new float[node.Outputs.Count][];
            for (int o = 0; o < outputs.Length; o++)
            {
                outputs[o] = new float[BlockFrames * channels];
                outputBuffers[node.Outputs[o]] = outputs[o];
            }

            float[]? device = node is DeviceNode deviceNode ? deviceBuffer(deviceNode) : null;
            if (device is not null && device.Length < BlockFrames * channels)
            {
                throw new ArgumentException("Device buffers must hold a whole block.", nameof(deviceBuffer));
            }

            routing.Append(device is null ? "" : "*").Append(';');
            steps[s] = new Step(node, inputs, outputs, device, upstream.ToArray(), sources, new int[inputs.Length]);
            stepIndex[node] = s;
        }

        return new GraphPlan(steps, channels, routing.ToString());
    }

    /// <summary>Runs every node once. Audio thread only.</summary>
    public void Process(int frames)
    {
        if (frames < 0 || frames > BlockFrames)
        {
            throw new ArgumentOutOfRangeException(nameof(frames));
        }

        for (int s = 0; s < _steps.Length; s++)
        {
            var step = _steps[s];

            // How late each input is, from the delays of the nodes processed so far (they can
            // change while playing, e.g. when an effect is switched off).
            for (int i = 0; i < step.Sources.Length; i++)
            {
                step.InputLatencies[i] = step.Sources[i] < 0 ? 0 : _pathLatency[step.Sources[i]];
            }

            var device = step.Device is null ? Span<float>.Empty : step.Device.AsSpan(0, frames * Channels);
            step.Node.Process(new NodeBlock(step.Inputs, step.Outputs, _silence, device, frames, Channels, step.InputLatencies));
            _pathLatency[s] = PathLatency(step, _pathLatency);
        }
    }

    /// <summary>Delay of the slowest path from a source to an output, in frames.</summary>
    public int LatencyFrames()
    {
        var path = new int[_steps.Length];
        int longest = 0;
        for (int s = 0; s < _steps.Length; s++)
        {
            path[s] = PathLatency(_steps[s], path);
            if (_steps[s].Node is OutputDeviceNode)
            {
                longest = Math.Max(longest, path[s]);
            }
        }

        return longest;
    }

    /// <summary>Delay of the slowest path through a step, given those of the steps before it.</summary>
    private static int PathLatency(Step step, int[] path)
    {
        int upstream = 0;
        foreach (int u in step.Upstream)
        {
            upstream = Math.Max(upstream, path[u]);
        }

        return upstream + step.Node.LatencyFrames;
    }

    private static HashSet<AudioNode> FindLiveNodes(AudioGraph graph)
    {
        var live = new HashSet<AudioNode>();
        var pending = new Stack<AudioNode>();
        foreach (var node in graph.Nodes)
        {
            if (node is OutputDeviceNode or InputDeviceNode && live.Add(node))
            {
                pending.Push(node);
            }
        }

        // Everything that feeds an output (directly or not) can be heard.
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            foreach (var input in node.Inputs)
            {
                var source = graph.ConnectionTo(input)?.From.Node;
                if (source is not null && live.Add(source))
                {
                    pending.Push(source);
                }
            }
        }

        return live;
    }

    private static List<AudioNode> SortTopologically(AudioGraph graph, HashSet<AudioNode> live)
    {
        // Kahn's algorithm, starting from the nodes in the order they were added so plans are deterministic.
        var waitingFor = new Dictionary<AudioNode, int>();
        foreach (var node in graph.Nodes.Where(live.Contains))
        {
            waitingFor[node] = node.Inputs.Count(i => graph.ConnectionTo(i) is { } c && live.Contains(c.From.Node));
        }

        var order = new List<AudioNode>(waitingFor.Count);
        var ready = new Queue<AudioNode>(graph.Nodes.Where(n => waitingFor.TryGetValue(n, out int count) && count == 0));
        while (ready.Count > 0)
        {
            var node = ready.Dequeue();
            order.Add(node);
            foreach (var output in node.Outputs)
            {
                foreach (var connection in graph.ConnectionsFrom(output))
                {
                    var target = connection.To.Node;
                    if (waitingFor.ContainsKey(target) && --waitingFor[target] == 0)
                    {
                        ready.Enqueue(target);
                    }
                }
            }
        }

        if (order.Count != waitingFor.Count)
        {
            throw new InvalidOperationException("The graph contains a loop.");
        }

        return order;
    }

    /// <param name="Sources">For every input, the step feeding it, or -1.</param>
    /// <param name="InputLatencies">Filled in before every block: how late each input is.</param>
    private sealed record Step(AudioNode Node, float[]?[] Inputs, float[][] Outputs, float[]? Device, int[] Upstream, int[] Sources, int[] InputLatencies);
}
