using System.Collections.ObjectModel;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoiceChanger.Core.Audio;
using VoiceChanger.Core.Effects;
using VoiceChanger.Core.Graph;
using VoiceChanger.Core.Settings;

namespace VoiceChanger.App.ViewModels;

/// <summary>An entry of the "Add node" menu.</summary>
public sealed record AddNodeOption(NodeDescriptor Descriptor, IRelayCommand Command)
{
    public string Name => Descriptor.Name;

    public string Description => Descriptor.Description;
}

/// <summary>
/// The node editor: nodes and wires of an <see cref="AudioGraph"/>, the selection and the view
/// (zoom and scroll position). The graph is the source of truth; this view model follows its changes.
/// </summary>
public sealed partial class NodeEditorViewModel : ObservableObject
{
    public const double MinZoom = 0.3;
    public const double MaxZoom = 2;

    private readonly NodeRegistry _registry;
    private readonly ObservableCollection<AudioDeviceInfo> _inputDevices;
    private readonly ObservableCollection<AudioDeviceInfo> _outputDevices;
    private int _topZOrder;
    private int _cascade;

    public NodeEditorViewModel(
        AudioGraph graph,
        NodeRegistry registry,
        ObservableCollection<AudioDeviceInfo> inputDevices,
        ObservableCollection<AudioDeviceInfo> outputDevices)
    {
        Graph = graph;
        _registry = registry;
        _inputDevices = inputDevices;
        _outputDevices = outputDevices;
        BuildAddNodeOptions();
        graph.Changed += (_, _) => Sync();
        Sync();
    }

    public AudioGraph Graph { get; }

    public ObservableCollection<NodeViewModel> Nodes { get; } = [];

    public ObservableCollection<WireViewModel> Wires { get; } = [];

    /// <summary>Every node that can be added.</summary>
    public IReadOnlyList<AddNodeOption> AddNodeOptions { get; private set; } = [];

    /// <summary>The "Add node" menu: the built-in nodes, with the plug-ins by vendor after the effects.</summary>
    public IReadOnlyList<AddMenuEntry> AddNodeMenu { get; private set; } = [];

    public bool IsEmpty => Nodes.Count == 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(DuplicateSelectedCommand))]
    public partial NodeViewModel? SelectedNode { get; set; }

    public bool HasSelection => SelectedNode is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ZoomText))]
    public partial double Zoom { get; set; } = 1;

    public string ZoomText => $"{Zoom:P0}";

    /// <summary>Screen offset of the canvas origin.</summary>
    [ObservableProperty]
    public partial double PanX { get; set; }

    [ObservableProperty]
    public partial double PanY { get; set; }

    /// <summary>Why the last action did not work (e.g. a wire that would make a loop); cleared by the next one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHint))]
    public partial string? Hint { get; set; }

    public bool HasHint => !string.IsNullOrEmpty(Hint);

    /// <summary>Centre of the visible part of the canvas; new nodes from the toolbar go there. Set by the view.</summary>
    public Point ViewCenter { get; set; } = new(400, 250);

    /// <summary>Raised when the wires need to be redrawn (e.g. the selection changed which ones stand out).</summary>
    public event EventHandler? WiresInvalidated;

    /// <summary>Asks the view to zoom and scroll so every node is visible.</summary>
    public event EventHandler? FitRequested;

    [RelayCommand]
    private void FitView() => FitRequested?.Invoke(this, EventArgs.Empty);

    public NodeViewModel? Find(AudioNode node) => Nodes.FirstOrDefault(n => n.Node == node);

    /// <summary>Adds a node of the given type, centred on <paramref name="at"/> (or the middle of the view), and selects it.</summary>
    public NodeViewModel AddNode(NodeDescriptor descriptor, Point? at = null)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var node = descriptor.Create();
        Point position;
        if (at is { } point)
        {
            position = point;
            _cascade = 0;
        }
        else
        {
            // Several nodes added in a row from the toolbar fan out instead of piling up.
            position = ViewCenter + new Point(24 * _cascade, 24 * _cascade);
            _cascade = (_cascade + 1) % 8;
        }

        node.X = Math.Round(position.X - 110);
        node.Y = Math.Round(position.Y - 40);
        Graph.Add(node);
        var viewModel = Find(node)!;
        if (viewModel is DeviceNodeViewModel device)
        {
            device.ChooseAvailableDevice();
        }

        SelectedNode = viewModel;
        Hint = node is EffectNode { Effect: MissingEffect } ? $"{descriptor.Name} could not be loaded." : null;
        return viewModel;
    }

    /// <summary>(Re)builds <see cref="AddNodeOptions"/> from the registry, e.g. once more plug-ins were found.</summary>
    public void BuildAddNodeOptions()
    {
        AddNodeOptions = _registry.Descriptors
            .Select(d => new AddNodeOption(d, new RelayCommand(() => AddNode(d))))
            .ToList();
        AddNodeMenu = AddMenuEntry.Build(
            AddNodeOptions,
            o => o.Descriptor.Group,
            o => new AddMenuEntry(o.Name, o.Description, o.Command),
            pluginsAfter: o => o.Descriptor.Category <= NodeCategory.Effect);
        OnPropertyChanged(nameof(AddNodeOptions));
        OnPropertyChanged(nameof(AddNodeMenu));
    }

    public void RemoveNode(NodeViewModel node)
    {
        Graph.Remove(node.Node);
        Hint = null;
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void DeleteSelected()
    {
        if (SelectedNode is { } node)
        {
            RemoveNode(node);
        }
    }

    /// <summary>Adds a copy of the selected node (with its settings, without its wires) next to it.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void DuplicateSelected()
    {
        if (SelectedNode is not { } original)
        {
            return;
        }

        var settings = NodeSettings.Capture(original.Node);
        settings.Id = Guid.Empty;
        var copy = settings.Restore(_registry);
        if (copy is null)
        {
            return;
        }

        copy.X = original.X + 30;
        copy.Y = original.Y + 30;
        Graph.Add(copy);
        SelectedNode = Find(copy);
    }

    /// <summary>Connects two sockets (in either order). Returns false and sets <see cref="Hint"/> when that is not possible.</summary>
    public bool TryConnect(PortViewModel first, PortViewModel second)
    {
        var (from, to) = first.IsInput ? (second, first) : (first, second);
        if (!Graph.CanConnect(from.Port, to.Port, out string? reason))
        {
            Hint = reason;
            return false;
        }

        Graph.Connect(from.Port, to.Port);
        Hint = null;
        return true;
    }

    /// <summary>Removes the wire arriving at <paramref name="input"/>.</summary>
    public void Disconnect(PortViewModel input)
    {
        if (Graph.ConnectionTo(input.Port) is { } connection)
        {
            Graph.Disconnect(connection);
        }

        Hint = null;
    }

    /// <summary>The socket feeding <paramref name="input"/>, if it is connected.</summary>
    public PortViewModel? SourceOf(PortViewModel input) =>
        Graph.ConnectionTo(input.Port) is { } connection ? Find(connection.From.Node)?.FindPort(connection.From) : null;

    /// <summary>Keeps the zoom in range and the point under <paramref name="pivot"/> (screen coordinates) in place.</summary>
    public void ZoomAt(Point pivot, double zoom)
    {
        zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        double worldX = (pivot.X - PanX) / Zoom;
        double worldY = (pivot.Y - PanY) / Zoom;
        Zoom = zoom;
        PanX = pivot.X - worldX * zoom;
        PanY = pivot.Y - worldY * zoom;
    }

    /// <summary>Refreshes meters and live displays (about 30 times per second on the UI thread).</summary>
    public void Tick(bool isRunning)
    {
        foreach (var node in Nodes)
        {
            node.Tick(isRunning);
        }
    }

    partial void OnSelectedNodeChanged(NodeViewModel? oldValue, NodeViewModel? newValue)
    {
        oldValue?.IsSelected = false;
        if (newValue is not null)
        {
            newValue.IsSelected = true;
            newValue.ZOrder = ++_topZOrder;
        }

        WiresInvalidated?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Brings the nodes, sockets and wires in line with the graph.</summary>
    private void Sync()
    {
        for (int i = Nodes.Count - 1; i >= 0; i--)
        {
            if (!Graph.Nodes.Contains(Nodes[i].Node))
            {
                if (SelectedNode == Nodes[i])
                {
                    SelectedNode = null;
                }

                Nodes[i].Detach();

                // A removed plug-in lets go of its window and its host process.
                if (Nodes[i].Node is EffectNode { Effect: IDisposable effect })
                {
                    effect.Dispose();
                }

                Nodes.RemoveAt(i);
            }
        }

        foreach (var node in Graph.Nodes)
        {
            var viewModel = Find(node);
            if (viewModel is null)
            {
                string description = node is EffectNode { Effect: MissingEffect }
                    ? MissingEffect.Explanation
                    : _registry.Find(node.TypeId)?.Description ?? "";
                viewModel = NodeViewModel.Create(node, description, _inputDevices, _outputDevices);
                var added = viewModel;
                viewModel.DeleteCommand = new RelayCommand(() => RemoveNode(added));
                viewModel.DuplicateCommand = new RelayCommand(() =>
                {
                    SelectedNode = added;
                    DuplicateSelected();
                });
                Nodes.Add(viewModel);
            }
        }

        var connected = Graph.Connections.SelectMany(c => new[] { c.From, c.To }).ToHashSet();
        foreach (var node in Nodes)
        {
            node.SyncPorts();
            foreach (var port in node.Inputs.Concat(node.Outputs))
            {
                port.IsConnected = connected.Contains(port.Port);
            }

            node.OnPortsSynced();
        }

        Wires.Clear();
        foreach (var connection in Graph.Connections)
        {
            var from = Find(connection.From.Node)?.FindPort(connection.From);
            var to = Find(connection.To.Node)?.FindPort(connection.To);
            if (from is not null && to is not null)
            {
                Wires.Add(new WireViewModel(connection, from, to));
            }
        }

        OnPropertyChanged(nameof(IsEmpty));
        WiresInvalidated?.Invoke(this, EventArgs.Empty);
    }
}
