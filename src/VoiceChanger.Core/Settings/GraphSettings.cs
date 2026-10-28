using System.Text.Json.Serialization;
using VoiceChanger.Core.Effects;
using VoiceChanger.Core.Graph;

namespace VoiceChanger.Core.Settings;

/// <summary>How the window shows the signal path.</summary>
public enum EditorMode
{
    /// <summary>One input, a list of effects, one output.</summary>
    Chain,

    /// <summary>A node editor with any number of sources, effects, mixers and outputs.</summary>
    Nodes,
}

/// <summary>Saved state of a node graph.</summary>
public sealed class GraphSettings
{
    public List<NodeSettings> Nodes { get; set; } = [];

    public List<ConnectionSettings> Connections { get; set; } = [];

    public static GraphSettings Capture(AudioGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        return new GraphSettings
        {
            Nodes = graph.Nodes.Select(NodeSettings.Capture).ToList(),
            Connections = graph.Connections.Select(c => new ConnectionSettings
            {
                From = c.From.Node.Id,
                FromPort = c.From.Id,
                To = c.To.Node.Id,
                ToPort = c.To.Id,
            }).ToList(),
        };
    }

    /// <summary>
    /// Replaces the contents of <paramref name="graph"/> with the saved nodes and wires. Unknown node
    /// types and wires that no longer fit are skipped, so an old or damaged file still loads.
    /// </summary>
    public void RestoreInto(AudioGraph graph, NodeRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(registry);

        using var update = graph.BeginUpdate();
        graph.Clear();

        // A missing plug-in keeps its side-chain input when something was plugged into it.
        var sidechained = (Connections ?? [])
            .Where(c => c.ToPort == EffectNode.SidechainPort)
            .Select(c => c.To)
            .ToHashSet();
        foreach (var saved in Nodes ?? [])
        {
            var node = saved.Restore(registry, sidechained.Contains(saved.Id));
            if (node is null)
            {
                continue;
            }

            if (graph.FindNode(node.Id) is not null)
            {
                node.Id = Guid.NewGuid();
            }

            graph.Add(node);
        }

        foreach (var saved in Connections ?? [])
        {
            var from = graph.FindNode(saved.From)?.FindOutput(saved.FromPort ?? "");
            var to = graph.FindNode(saved.To)?.FindInput(saved.ToPort ?? "");
            if (from is not null && to is not null && graph.CanConnect(from, to, out _))
            {
                graph.Connect(from, to);
            }
        }
    }
}

/// <summary>Saved state of one node.</summary>
public sealed class NodeSettings
{
    public Guid Id { get; set; }

    public string Type { get; set; } = "";

    public string? Label { get; set; }

    public string? DeviceId { get; set; }

    public double X { get; set; }

    public double Y { get; set; }

    /// <summary>Whether an effect node is switched on.</summary>
    public bool Enabled { get; set; } = true;

    public Dictionary<string, float> Parameters { get; set; } = [];

    /// <summary>An effect node's plug-in name, so it can be named when it is missing.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }

    /// <summary>An effect node's plug-in state (base64), see <see cref="IStatefulEffect"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? State { get; set; }

    public static NodeSettings Capture(AudioNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var effect = (node as EffectNode)?.Effect;
        return new NodeSettings
        {
            Id = node.Id,
            Type = node.TypeId,
            Label = node.Label,
            DeviceId = (node as DeviceNode)?.DeviceId,
            X = Math.Round(node.X, 1),
            Y = Math.Round(node.Y, 1),
            Enabled = effect?.IsEnabled ?? true,
            Parameters = effect is not null ? SavedEffect.ParametersOf(effect) : node.Parameters.ToDictionary(p => p.Id, p => p.Value),
            Name = effect is not null ? SavedEffect.NameOf(effect) : null,
            State = effect is not null ? SavedEffect.StateOf(effect) : null,
        };
    }

    /// <summary>
    /// Creates the node, or <c>null</c> if its type is unknown. A plug-in that isn't installed or
    /// doesn't load gets an effect node with a <see cref="MissingEffect"/>, with a side-chain input
    /// when <paramref name="hasSidechain"/>.
    /// </summary>
    public AudioNode? Restore(NodeRegistry registry, bool hasSidechain = false)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var node = registry.Create(Type);
        if (node is EffectNode { Effect: MissingEffect } || (node is null && Type.StartsWith(EffectNode.TypePrefix, StringComparison.OrdinalIgnoreCase)))
        {
            var missing = SavedEffect.StandIn(Type[EffectNode.TypePrefix.Length..], Name, Parameters, State, hasSidechain);
            node = missing is null ? null : new EffectNode(missing);
        }

        if (node is null)
        {
            return null;
        }

        if (Id != Guid.Empty)
        {
            node.Id = Id;
        }

        node.Label = Label;
        node.X = double.IsFinite(X) ? X : 0;
        node.Y = double.IsFinite(Y) ? Y : 0;
        if (node is DeviceNode device)
        {
            device.DeviceId = DeviceId;
        }

        if (node is EffectNode effectNode)
        {
            if (effectNode.Effect is not MissingEffect)
            {
                SavedEffect.Apply(effectNode.Effect, Parameters, State);
            }

            effectNode.Effect.IsEnabled = Enabled;
            return node;
        }

        // In the node's own order, so settings that add ports (the mixer's input count) come first.
        var values = new Dictionary<string, float>(Parameters ?? [], StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in node.Parameters)
        {
            if (values.TryGetValue(parameter.Id, out float value))
            {
                parameter.Value = value;
            }
        }

        return node;
    }
}

/// <summary>Saved wire between two nodes.</summary>
public sealed class ConnectionSettings
{
    public Guid From { get; set; }

    public string FromPort { get; set; } = "out";

    public Guid To { get; set; }

    public string ToPort { get; set; } = "in";
}
