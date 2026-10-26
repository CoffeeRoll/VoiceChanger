using System.Diagnostics.CodeAnalysis;

namespace VoiceChanger.Core.Graph;

/// <summary>
/// Nodes and the wires between them. Every input takes at most one wire; an output can feed any
/// number of inputs. Loops are not allowed: audio always flows from sources to outputs.
/// <para>Edit the graph from one (UI) thread; the audio engine works on compiled snapshots.</para>
/// </summary>
public sealed class AudioGraph
{
    private readonly List<AudioNode> _nodes = [];
    private readonly List<NodeConnection> _connections = [];
    private bool _effectsEnabled = true;
    private int _updateDepth;
    private bool _changedDuringUpdate;

    public IReadOnlyList<AudioNode> Nodes => _nodes;

    public IReadOnlyList<NodeConnection> Connections => _connections;

    /// <summary>Master switch: when off every effect node passes its input through unchanged.</summary>
    public bool EffectsEnabled
    {
        get => _effectsEnabled;
        set
        {
            _effectsEnabled = value;
            foreach (var node in _nodes.OfType<EffectNode>())
            {
                node.Chain.IsEnabled = value;
            }
        }
    }

    /// <summary>Raised after nodes, connections, ports or devices changed (once per <see cref="BeginUpdate"/> block).</summary>
    public event EventHandler? Changed;

    /// <summary>Collects changes and reports them once when the returned object is disposed.</summary>
    public IDisposable BeginUpdate()
    {
        _updateDepth++;
        return new UpdateScope(this);
    }

    public T Add<T>(T node)
        where T : AudioNode
    {
        ArgumentNullException.ThrowIfNull(node);
        if (_nodes.Contains(node))
        {
            throw new InvalidOperationException("The node is already part of the graph.");
        }

        if (FindNode(node.Id) is not null)
        {
            throw new InvalidOperationException($"Another node already has the id {node.Id}.");
        }

        if (node is EffectNode effectNode)
        {
            effectNode.Chain.IsEnabled = _effectsEnabled;
        }

        _nodes.Add(node);
        node.StructureChanged += OnNodeStructureChanged;
        RaiseChanged();
        return node;
    }

    public bool Remove(AudioNode node)
    {
        if (!_nodes.Remove(node))
        {
            return false;
        }

        node.StructureChanged -= OnNodeStructureChanged;
        _connections.RemoveAll(c => c.From.Node == node || c.To.Node == node);
        RaiseChanged();
        return true;
    }

    public void Clear()
    {
        foreach (var node in _nodes)
        {
            node.StructureChanged -= OnNodeStructureChanged;
        }

        _nodes.Clear();
        _connections.Clear();
        RaiseChanged();
    }

    public AudioNode? FindNode(Guid id) => _nodes.FirstOrDefault(n => n.Id == id);

    /// <summary>The wire arriving at <paramref name="input"/>, if any.</summary>
    public NodeConnection? ConnectionTo(NodePort input) => _connections.FirstOrDefault(c => c.To == input);

    /// <summary>The wires leaving <paramref name="output"/>.</summary>
    public IEnumerable<NodeConnection> ConnectionsFrom(NodePort output) => _connections.Where(c => c.From == output);

    /// <summary>Whether a wire from <paramref name="from"/> to <paramref name="to"/> is allowed, and if not why.</summary>
    public bool CanConnect(NodePort from, NodePort to, [NotNullWhen(false)] out string? reason)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);

        if (from.Direction != PortDirection.Output || to.Direction != PortDirection.Input)
        {
            reason = "Wires go from an output (right side of a node) to an input (left side).";
        }
        else if (!_nodes.Contains(from.Node) || !_nodes.Contains(to.Node) || !from.Node.Outputs.Contains(from) || !to.Node.Inputs.Contains(to))
        {
            reason = "That socket is not part of this graph.";
        }
        else if (from.Node == to.Node)
        {
            reason = "A node cannot feed its own input.";
        }
        else if (IsUpstream(of: from.Node, candidate: to.Node))
        {
            reason = "That would create a loop: audio has to flow from the sources to the outputs.";
        }
        else
        {
            reason = null;
        }

        return reason is null;
    }

    /// <summary>Connects two ports. A wire already arriving at <paramref name="to"/> is replaced.</summary>
    public NodeConnection Connect(NodePort from, NodePort to)
    {
        if (!CanConnect(from, to, out string? reason))
        {
            throw new InvalidOperationException(reason);
        }

        var existing = ConnectionTo(to);
        if (existing is not null && existing.From == from)
        {
            return existing;
        }

        if (existing is not null)
        {
            _connections.Remove(existing);
        }

        var connection = new NodeConnection(from, to);
        _connections.Add(connection);
        RaiseChanged();
        return connection;
    }

    public bool Disconnect(NodeConnection connection)
    {
        if (!_connections.Remove(connection))
        {
            return false;
        }

        RaiseChanged();
        return true;
    }

    /// <summary>Whether <paramref name="candidate"/> feeds <paramref name="of"/>, directly or through other nodes.</summary>
    public bool IsUpstream(AudioNode of, AudioNode candidate)
    {
        var visited = new HashSet<AudioNode>();
        var pending = new Stack<AudioNode>();
        pending.Push(of);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            foreach (var connection in _connections)
            {
                if (connection.To.Node != node)
                {
                    continue;
                }

                var source = connection.From.Node;
                if (source == candidate)
                {
                    return true;
                }

                if (visited.Add(source))
                {
                    pending.Push(source);
                }
            }
        }

        return false;
    }

    private void OnNodeStructureChanged(object? sender, EventArgs e)
    {
        // Ports that disappeared (e.g. a mixer with fewer inputs) take their wires with them.
        _connections.RemoveAll(c => !c.From.Node.Outputs.Contains(c.From) || !c.To.Node.Inputs.Contains(c.To));
        RaiseChanged();
    }

    private void RaiseChanged()
    {
        if (_updateDepth > 0)
        {
            _changedDuringUpdate = true;
            return;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private sealed class UpdateScope(AudioGraph graph) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (--graph._updateDepth == 0 && graph._changedDuringUpdate)
            {
                graph._changedDuringUpdate = false;
                graph.Changed?.Invoke(graph, EventArgs.Empty);
            }
        }
    }
}
