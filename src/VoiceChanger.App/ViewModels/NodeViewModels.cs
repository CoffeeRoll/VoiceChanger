using System.Collections.ObjectModel;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoiceChanger.Core.Audio;
using VoiceChanger.Core.Effects;
using VoiceChanger.Core.Graph;

namespace VoiceChanger.App.ViewModels;

/// <summary>A socket on a node in the editor.</summary>
public sealed partial class PortViewModel(NodeViewModel owner, NodePort port) : ObservableObject
{
    public NodeViewModel Owner { get; } = owner;

    public NodePort Port { get; } = port;

    public string Name => Port.Name;

    public bool IsInput => Port.Direction == PortDirection.Input;

    [ObservableProperty]
    public partial bool IsConnected { get; set; }

    /// <summary>Extra text next to the name, e.g. a mixer input's volume.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetail))]
    public partial string? Detail { get; set; }

    public bool HasDetail => !string.IsNullOrEmpty(Detail);

    /// <summary>Centre of the socket in canvas coordinates; kept up to date by the editor view.</summary>
    public Point Anchor { get; set; }

    public override string ToString() => Port.ToString();
}

/// <summary>A wire between two sockets.</summary>
public sealed class WireViewModel(NodeConnection connection, PortViewModel from, PortViewModel to)
{
    public NodeConnection Connection { get; } = connection;

    public PortViewModel From { get; } = from;

    public PortViewModel To { get; } = to;

    /// <summary>Wires of the selected node stand out.</summary>
    public bool IsHighlighted => From.Owner.IsSelected || To.Owner.IsSelected;
}

/// <summary>A node on the canvas and in the inspector.</summary>
public abstract partial class NodeViewModel : ObservableObject
{
    private const double MeterFloorDb = -60;
    private const double MeterFallDbPerTick = 1.2;

    protected NodeViewModel(AudioNode node, string description)
    {
        Node = node;
        Description = description;
        node.LabelChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(Label));
        };
    }

    public AudioNode Node { get; }

    public string Description { get; }

    /// <summary>The user's name for the node, or the node type's name.</summary>
    public string Title => Node.Title;

    public string TypeName => Node.Name;

    /// <summary>Optional name given by the user ("Guitar"); empty shows the type name.</summary>
    public string? Label
    {
        get => Node.Label;
        set => Node.Label = value;
    }

    public NodeCategory Category => Node.Category;

    public string CategoryName => Category switch
    {
        NodeCategory.Source => "Source",
        NodeCategory.Effect => "Effect",
        NodeCategory.Mixing => "Mixer",
        _ => "Output",
    };

    public bool IsSource => Category == NodeCategory.Source;

    public bool IsEffect => Category == NodeCategory.Effect;

    public bool IsMixing => Category == NodeCategory.Mixing;

    public bool IsOutput => Category == NodeCategory.Output;

    /// <summary>Position of the node's top-left corner on the canvas.</summary>
    public double X
    {
        get => Node.X;
        set
        {
            if (Node.X != value)
            {
                Node.X = value;
                OnPropertyChanged();
            }
        }
    }

    public double Y
    {
        get => Node.Y;
        set
        {
            if (Node.Y != value)
            {
                Node.Y = value;
                OnPropertyChanged();
            }
        }
    }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>Stacking order on the canvas: the most recently selected node is on top.</summary>
    [ObservableProperty]
    public partial int ZOrder { get; set; }

    public ObservableCollection<PortViewModel> Inputs { get; } = [];

    public ObservableCollection<PortViewModel> Outputs { get; } = [];

    /// <summary>Level of what the node produces, in dBFS.</summary>
    [ObservableProperty]
    public partial double LevelDb { get; set; } = MeterFloorDb;

    /// <summary>One line describing the node's settings, shown on the canvas.</summary>
    public virtual string Summary => "";

    public bool HasSummary => !string.IsNullOrEmpty(Summary);

    /// <summary>Whether the node has an on/off switch (effects).</summary>
    public virtual bool CanBypass => false;

    /// <summary>The on/off switch of nodes that <see cref="CanBypass"/>; other nodes are always on.</summary>
    public virtual bool IsEnabled
    {
        get => true;
        set
        {
        }
    }

    // Set by the editor when the node is added.
    public IRelayCommand? DeleteCommand { get; set; }

    public IRelayCommand? DuplicateCommand { get; set; }

    public static NodeViewModel Create(
        AudioNode node,
        string description,
        ObservableCollection<AudioDeviceInfo> inputDevices,
        ObservableCollection<AudioDeviceInfo> outputDevices)
    {
        NodeViewModel viewModel = node switch
        {
            InputDeviceNode input => new DeviceNodeViewModel(input, description, inputDevices, input.Gain),
            OutputDeviceNode output => new DeviceNodeViewModel(output, description, outputDevices, output.Volume),
            EffectNode effect => new EffectNodeViewModel(effect, description),
            MixerNode mixer => new MixerNodeViewModel(mixer, description),
            _ => new GenericNodeViewModel(node, description),
        };
        viewModel.SyncPorts();
        return viewModel;
    }

    public PortViewModel? FindPort(NodePort port) =>
        (port.Direction == PortDirection.Input ? Inputs : Outputs).FirstOrDefault(p => p.Port == port);

    /// <summary>Matches the socket lists to the node's ports, keeping the view models of ports that remain.</summary>
    internal void SyncPorts()
    {
        Sync(Inputs, Node.Inputs);
        Sync(Outputs, Node.Outputs);
        OnPortsSynced();
    }

    /// <summary>Called after the sockets or the wires attached to them changed.</summary>
    protected internal virtual void OnPortsSynced()
    {
    }


    /// <summary>Refreshes meters and live displays (about 30 times per second on the UI thread).</summary>
    public virtual void Tick(bool isRunning)
    {
        LevelDb = isRunning ? Meter(LevelDb, Node.Meter.ReadAndReset()) : MeterFloorDb;
    }

    /// <summary>Called when the node left the graph: stop listening to shared objects.</summary>
    protected internal virtual void Detach()
    {
    }

    internal static double Meter(double previousDb, float peak)
    {
        double db = peak > 0 ? Math.Max(MeterFloorDb, 20 * Math.Log10(peak)) : MeterFloorDb;
        return Math.Max(db, previousDb - MeterFallDbPerTick);
    }

    protected void RefreshSummary()
    {
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(HasSummary));
    }

    private void Sync(ObservableCollection<PortViewModel> target, IReadOnlyList<NodePort> ports)
    {
        for (int i = target.Count - 1; i >= 0; i--)
        {
            if (!ports.Contains(target[i].Port))
            {
                target.RemoveAt(i);
            }
        }

        for (int i = 0; i < ports.Count; i++)
        {
            if (i >= target.Count || target[i].Port != ports[i])
            {
                target.Insert(i, new PortViewModel(this, ports[i]));
            }
        }
    }
}

/// <summary>An audio input or output: which device, and its level.</summary>
public sealed partial class DeviceNodeViewModel : NodeViewModel
{
    private readonly DeviceNode _device;

    public DeviceNodeViewModel(DeviceNode node, string description, ObservableCollection<AudioDeviceInfo> devices, EffectParameter level)
        : base(node, description)
    {
        _device = node;
        Devices = devices;
        Level = new ParameterViewModel(level);
        Level.Changed += (_, _) => RefreshSummary();
        devices.CollectionChanged += OnDevicesChanged;
    }

    public ObservableCollection<AudioDeviceInfo> Devices { get; }

    public bool IsInputDevice => _device.DeviceKind == AudioDeviceKind.Input;

    public string DeviceLabel => IsInputDevice ? "Microphone or source" : "Speakers, headphones or virtual cable";

    public string PlaceholderText => Devices.Count == 0 ? "No devices found" : "Choose a device";

    /// <summary>Gain of an input, volume of an output.</summary>
    public ParameterViewModel Level { get; }

    public AudioDeviceInfo? SelectedDevice
    {
        get => Devices.FirstOrDefault(d => d.Id == _device.DeviceId);
        set
        {
            // The combo box reports null while its list is refreshed: keep the device.
            if (value is null || value.Id == _device.DeviceId)
            {
                return;
            }

            _device.DeviceId = value.Id;
            OnPropertyChanged();
            RefreshSummary();
        }
    }

    public override string Summary
    {
        get
        {
            string device = SelectedDevice?.Name ?? (_device.DeviceId is null ? "No device chosen" : "Device not found");
            return Level.Parameter.IsDefault ? device : $"{device} · {Level.DisplayValue}";
        }
    }

    protected internal override void Detach() => Devices.CollectionChanged -= OnDevicesChanged;

    private void OnDevicesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(SelectedDevice));
        OnPropertyChanged(nameof(PlaceholderText));
        RefreshSummary();
    }

    /// <summary>Picks the default device when none is chosen or the chosen one is gone.</summary>
    public void ChooseAvailableDevice()
    {
        if (SelectedDevice is null && Devices.Count > 0)
        {
            SelectedDevice = Devices.FirstOrDefault(d => d.IsDefault) ?? Devices[0];
        }
    }
}

/// <summary>An effect: its card's editor is shown in the inspector.</summary>
public sealed partial class EffectNodeViewModel : NodeViewModel
{
    public EffectNodeViewModel(EffectNode node, string description)
        : base(node, description)
    {
        Effect = EffectViewModel.Create(node.Effect, description);
        Effect.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EffectViewModel.Summary))
            {
                RefreshSummary();
            }
            else if (e.PropertyName == nameof(EffectViewModel.IsEnabled))
            {
                OnPropertyChanged(nameof(IsEnabled));
            }
        };
    }

    public EffectViewModel Effect { get; }

    public override bool CanBypass => true;

    public override bool IsEnabled
    {
        get => Effect.IsEnabled;
        set => Effect.IsEnabled = value;
    }

    public override string Summary => Effect.Summary;

    public override void Tick(bool isRunning)
    {
        base.Tick(isRunning);
        Effect.Tick(isRunning);
    }
}

/// <summary>One input of a mixer in the inspector.</summary>
public sealed partial class MixerChannelViewModel(int index, ParameterViewModel gain, ParameterViewModel mute) : ObservableObject
{
    public int Index { get; } = index;

    public string Name => $"Input {Index + 1}";

    public ParameterViewModel Gain { get; } = gain;

    public ParameterViewModel Mute { get; } = mute;

    [ObservableProperty]
    public partial double LevelDb { get; set; } = -60;

    /// <summary>Whether something is plugged into this input.</summary>
    [ObservableProperty]
    public partial bool IsConnected { get; set; }
}

/// <summary>The mixer: a channel strip per input plus the master volume.</summary>
public sealed partial class MixerNodeViewModel : NodeViewModel
{
    private readonly MixerNode _mixer;
    private readonly MixerChannelViewModel[] _allChannels;

    public MixerNodeViewModel(MixerNode node, string description)
        : base(node, description)
    {
        _mixer = node;
        Master = new ParameterViewModel(node.Master);
        Master.Changed += (_, _) => RefreshSummary();
        var count = new ParameterViewModel(node.InputCount);
        count.Changed += (_, _) => UpdateChannels();
        _allChannels = Enumerable.Range(0, MixerNode.MaxInputs)
            .Select(i => new MixerChannelViewModel(i, new ParameterViewModel(node.Gains[i]), new ParameterViewModel(node.Mutes[i])))
            .ToArray();
        foreach (var channel in _allChannels)
        {
            channel.Gain.Changed += (_, _) => UpdateDetails();
            channel.Mute.Changed += (_, _) => UpdateDetails();
        }

        UpdateChannels();
    }

    public ParameterViewModel Master { get; }

    public ObservableCollection<MixerChannelViewModel> Channels { get; } = [];

    public int InputCount => _mixer.InputCount.IntValue;

    public override string Summary => $"{InputCount} inputs · master {Master.DisplayValue}";

    [RelayCommand(CanExecute = nameof(CanAddInput))]
    private void AddInput() => _mixer.InputCount.Value = InputCount + 1;

    private bool CanAddInput() => InputCount < MixerNode.MaxInputs;

    [RelayCommand(CanExecute = nameof(CanRemoveInput))]
    private void RemoveInput() => _mixer.InputCount.Value = InputCount - 1;

    private bool CanRemoveInput() => InputCount > MixerNode.MinInputs;

    public override void Tick(bool isRunning)
    {
        base.Tick(isRunning);
        foreach (var channel in Channels)
        {
            channel.LevelDb = isRunning ? Meter(channel.LevelDb, _mixer.InputMeters[channel.Index].ReadAndReset()) : -60;
        }
    }

    protected internal override void OnPortsSynced()
    {
        UpdateDetails();
        UpdateConnections();
    }

    private void UpdateConnections()
    {
        foreach (var channel in Channels)
        {
            channel.IsConnected = channel.Index < Inputs.Count && Inputs[channel.Index].IsConnected;
        }
    }

    private void UpdateChannels()
    {
        while (Channels.Count > InputCount)
        {
            Channels.RemoveAt(Channels.Count - 1);
        }

        while (Channels.Count < InputCount)
        {
            Channels.Add(_allChannels[Channels.Count]);
        }

        OnPropertyChanged(nameof(InputCount));
        AddInputCommand.NotifyCanExecuteChanged();
        RemoveInputCommand.NotifyCanExecuteChanged();
        RefreshSummary();
        UpdateDetails();
        UpdateConnections();
    }

    /// <summary>Shows each input's volume next to its socket.</summary>
    private void UpdateDetails()
    {
        foreach (var port in Inputs)
        {
            var channel = _allChannels[port.Port.Index];
            port.Detail = channel.Mute.IsOn ? "Muted" : channel.Gain.DisplayValue;
        }
    }
}

/// <summary>Nodes without a dedicated editor (the tone generator): every parameter as a row.</summary>
public sealed partial class GenericNodeViewModel : NodeViewModel
{
    public GenericNodeViewModel(AudioNode node, string description)
        : base(node, description)
    {
        Parameters = node.Parameters.Select(p => new ParameterViewModel(p)).ToList();
        foreach (var parameter in Parameters)
        {
            parameter.Changed += (_, _) =>
            {
                foreach (var other in Parameters)
                {
                    other.RefreshAvailability();
                }

                RefreshSummary();
            };
        }
    }

    public IReadOnlyList<ParameterViewModel> Parameters { get; }

    public override string Summary =>
        string.Join(" · ", Parameters.Where(p => p.IsAvailable).Select(p => p.DisplayValue));
}
