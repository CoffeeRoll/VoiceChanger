using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoiceChanger.Core.Audio;
using VoiceChanger.Core.Effects;
using VoiceChanger.Core.Graph;
using VoiceChanger.Core.Settings;

namespace VoiceChanger.App.ViewModels;

public sealed record LatencyOption(LatencyPreset Preset, string Name, string Description)
{
    public override string ToString() => Name;
}

/// <summary>An effect that can be added, with the command that adds it.</summary>
public sealed record AddEffectOption(string Name, string Description, IRelayCommand Command, string? Group = null);

/// <summary>An entry of an "Add" menu: something to add, or a submenu of <see cref="Items"/>.</summary>
public sealed record AddMenuEntry(string Name, string? Description, ICommand? Command, IReadOnlyList<AddMenuEntry>? Items = null)
{
    /// <summary>The submenu plug-ins are listed in, by vendor.</summary>
    public const string PluginsName = "Plug-ins";

    /// <summary>
    /// Menu entries for <paramref name="options"/>: the built-in ones as they are, the grouped ones
    /// (plug-ins) in a submenu per group, those in a "Plug-ins" submenu placed where
    /// <paramref name="pluginsAfter"/> says (after the last built-in entry by default).
    /// </summary>
    public static IReadOnlyList<AddMenuEntry> Build<T>(
        IReadOnlyList<T> options,
        Func<T, string?> group,
        Func<T, AddMenuEntry> entry,
        Func<T, bool>? pluginsAfter = null)
    {
        var groups = options
            .Where(o => group(o) is not null)
            .GroupBy(o => group(o)!, StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(g => new AddMenuEntry(g.Key, null, null, g.Select(entry).OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList()))
            .ToList();
        var builtIn = options.Where(o => group(o) is null).ToList();
        var menu = builtIn.Select(entry).ToList();
        if (groups.Count > 0)
        {
            int index = pluginsAfter is null ? builtIn.Count : builtIn.FindLastIndex(o => pluginsAfter(o)) + 1;
            menu.Insert(index, new AddMenuEntry(PluginsName, "VST3 effects installed on this computer", null, groups));
        }

        return menu;
    }
}

/// <summary>
/// State of the main window: device selection, the effect chain or the node graph, the master
/// switch and the audio engine's live status.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private const double MeterFloorDb = -60;
    private const double MeterFallDbPerTick = 1.2;

    private readonly AudioEngine _engine;
    private readonly AudioGraph _graph = new();
    private readonly GraphEngine _graphEngine;
    private readonly SettingsStore _settingsStore;
    private readonly EffectRegistry _registry;
    private readonly NodeRegistry _nodeRegistry;
    private readonly DispatcherTimer _statusTimer;
    private bool _graphInitialized;
    private bool _initializing = true;
    private bool _disposed;

    public MainWindowViewModel(IAudioBackend backend, SettingsStore settingsStore, EffectRegistry? registry = null)
    {
        _settingsStore = settingsStore;
        _registry = registry ?? EffectRegistry.Default;
        _nodeRegistry = registry is null ? NodeRegistry.Default : NodeRegistry.CreateDefault(registry);
        _engine = new AudioEngine(backend);
        _engine.Faulted += OnEngineFaulted;
        _graphEngine = new GraphEngine(backend, _graph);
        _graphEngine.Faulted += OnGraphEngineFaulted;

        LatencyOptions =
        [
            new(LatencyPreset.UltraLow, "Ultra low", "Smallest delay. May crackle on slower machines or Bluetooth devices."),
            new(LatencyPreset.Low, "Low (recommended)", "Low delay that works well on most machines."),
            new(LatencyPreset.Balanced, "Balanced", "A little more delay in exchange for extra safety against dropouts."),
            new(LatencyPreset.Safe, "Safe", "Largest buffers. Use this if you hear crackles or dropouts."),
        ];
        AvailableEffects = _registry.Descriptors;
        BuildAddEffectOptions();
        BackendName = backend.Name;
        IsBackendSupported = backend.IsSupported;

        var settings = settingsStore.Load();
        SelectedLatency = LatencyOptions.FirstOrDefault(o => o.Preset == settings.Latency) ?? LatencyOptions[1];
        MonoProcessing = settings.MonoProcessing;
        InputGainDb = Math.Clamp(settings.InputGainDb, -24, 24);
        OutputGainDb = Math.Clamp(settings.OutputGainDb, -40, 12);
        EffectsEnabled = settings.EffectsEnabled;
        _engine.InputGainDb = (float)InputGainDb;
        _engine.OutputGainDb = (float)OutputGainDb;
        _engine.Effects.IsEnabled = EffectsEnabled;

        LoadEffects(settings.Effects);
        LoadDevices(settings.InputDeviceId, settings.OutputDeviceId);

        NodeEditor = new NodeEditorViewModel(_graph, _nodeRegistry, InputDevices, OutputDevices);
        _graph.EffectsEnabled = EffectsEnabled;
        if (settings.Graph is not null)
        {
            settings.Graph.RestoreInto(_graph, _nodeRegistry);
            _graphInitialized = true;
            ChooseAvailableDevices();
        }

        Mode = settings.Mode;

        StatusText = IsBackendSupported
            ? "Stopped. Pick your devices and press Start."
            : "Audio devices are only supported on Windows at the moment.";

        _statusTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Background, (_, _) => UpdateStatus());
        _initializing = false;
    }

    // ---------------------------------------------------------------- devices (steps 1 and 3)

    public ObservableCollection<AudioDeviceInfo> InputDevices { get; } = [];

    public ObservableCollection<AudioDeviceInfo> OutputDevices { get; } = [];

    [ObservableProperty]
    public partial AudioDeviceInfo? SelectedInput { get; set; }

    [ObservableProperty]
    public partial AudioDeviceInfo? SelectedOutput { get; set; }

    public IReadOnlyList<LatencyOption> LatencyOptions { get; }

    [ObservableProperty]
    public partial LatencyOption SelectedLatency { get; set; }

    [ObservableProperty]
    public partial bool MonoProcessing { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInputGainChanged))]
    public partial double InputGainDb { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOutputGainChanged))]
    public partial double OutputGainDb { get; set; }

    /// <summary>Whether the input gain is away from 0 dB, so its reset button shows.</summary>
    public bool IsInputGainChanged => InputGainDb != 0;

    public bool IsOutputGainChanged => OutputGainDb != 0;

    public string BackendName { get; }

    public bool IsBackendSupported { get; }

    // ---------------------------------------------------------------- chain or nodes

    /// <summary>Whether the window shows the effect chain or the node editor.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChainMode))]
    [NotifyPropertyChangedFor(nameof(IsNodeMode))]
    public partial EditorMode Mode { get; set; }

    public bool IsChainMode
    {
        get => Mode == EditorMode.Chain;
        set
        {
            if (value)
            {
                Mode = EditorMode.Chain;
            }
        }
    }

    public bool IsNodeMode
    {
        get => Mode == EditorMode.Nodes;
        set
        {
            if (value)
            {
                Mode = EditorMode.Nodes;
            }
        }
    }

    public NodeEditorViewModel NodeEditor { get; }

    // ---------------------------------------------------------------- effects (steps 2 and 4)

    public IReadOnlyList<EffectDescriptor> AvailableEffects { get; }

    /// <summary>Every effect that can be added.</summary>
    public IReadOnlyList<AddEffectOption> AddEffectOptions { get; private set; } = [];

    /// <summary>The "Add effect" menu: the built-in effects, then the plug-ins by vendor.</summary>
    public IReadOnlyList<AddMenuEntry> AddEffectMenu { get; private set; } = [];

    public ObservableCollection<EffectViewModel> Effects { get; } = [];

    public bool HasNoEffects => Effects.Count == 0;

    public bool HasEffects => Effects.Count > 0;

    /// <summary>"Collapse all" while any card is open, otherwise "Expand all".</summary>
    public string CollapseAllText => Effects.Any(e => !e.IsCollapsed) ? "Collapse all" : "Expand all";

    /// <summary>Master switch: bypasses every effect when off.</summary>
    [ObservableProperty]
    public partial bool EffectsEnabled { get; set; }

    // ---------------------------------------------------------------- engine status

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartStopText))]
    public partial bool IsRunning { get; set; }

    public string StartStopText => IsRunning ? "Stop" : "Start";

    [ObservableProperty]
    public partial string StatusText { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; set; }

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    [ObservableProperty]
    public partial double InputLevelDb { get; set; } = MeterFloorDb;

    [ObservableProperty]
    public partial double OutputLevelDb { get; set; } = MeterFloorDb;

    [ObservableProperty]
    public partial string LatencyText { get; set; } = "–";

    // ---------------------------------------------------------------- commands

    [RelayCommand]
    private void ToggleRunning()
    {
        if (IsRunning)
        {
            Stop();
        }
        else
        {
            Start();
        }
    }

    [RelayCommand]
    private void RefreshDevices()
    {
        var input = SelectedInput;
        var output = SelectedOutput;
        LoadDevices(input?.Id, output?.Id);
        if (_graphInitialized)
        {
            ChooseAvailableDevices();
        }

        // A device that disappeared was replaced by another one: move the running audio over.
        if (IsRunning && Mode == EditorMode.Chain && (SelectedInput != input || SelectedOutput != output))
        {
            Start();
        }
    }

    [RelayCommand]
    private void AddEffect(EffectDescriptor descriptor)
    {
        var effect = descriptor.Create();
        if (effect is MissingEffect)
        {
            ErrorMessage = $"{descriptor.Name} could not be loaded.";
            return;
        }

        AddEffectViewModel(effect, descriptor);
        _engine.Effects.Add(Effects[^1].Effect);
        UpdatePositions();
    }

    /// <summary>Puts plug-ins found after the window opened into the "Add" menus.</summary>
    public void AddPlugins(IEnumerable<EffectDescriptor> plugins)
    {
        var added = plugins.ToList();
        if (added.Count == 0)
        {
            return;
        }

        foreach (var plugin in added.Where(p => _nodeRegistry.Find(EffectNode.TypePrefix + p.TypeId) is null))
        {
            _nodeRegistry.RegisterEffect(plugin);
        }

        BuildAddEffectOptions();
        NodeEditor.BuildAddNodeOptions();
    }

    [RelayCommand]
    private void DismissError() => ErrorMessage = null;

    /// <summary>Collapses every effect card, or expands them all when they are all collapsed already.</summary>
    [RelayCommand]
    private void ToggleCollapseAll()
    {
        bool collapse = Effects.Any(e => !e.IsCollapsed);
        foreach (var effect in Effects)
        {
            effect.IsCollapsed = collapse;
        }
    }

    public void Start()
    {
        ErrorMessage = null;
        if (Mode == EditorMode.Nodes)
        {
            StartGraph();
            return;
        }

        if (SelectedInput is null || SelectedOutput is null)
        {
            if (IsRunning)
            {
                Stop();
            }

            ErrorMessage = "Select an input and an output device first.";
            return;
        }

        try
        {
            var options = AudioEngineOptions.FromPreset(SelectedLatency.Preset, MonoProcessing);
            _graphEngine.Stop();
            _engine.Start(SelectedInput.Id, SelectedOutput.Id, options);
            OnStarted();
        }
        catch (Exception ex)
        {
            OnStartFailed(ex);
        }
    }

    private void StartGraph()
    {
        try
        {
            var options = AudioEngineOptions.FromPreset(SelectedLatency.Preset, MonoProcessing);
            _engine.Stop();
            _graphEngine.Start(options);
            OnStarted();
        }
        catch (Exception ex)
        {
            OnStartFailed(ex);
        }
    }

    private void OnStarted()
    {
        IsRunning = true;
        _statusTimer.Start();
        UpdateStatus();
    }

    private void OnStartFailed(Exception ex)
    {
        _engine.Stop();
        _graphEngine.Stop();
        IsRunning = false;
        _statusTimer.Stop();
        ClearLiveDisplays();
        ErrorMessage = $"Could not start audio: {ex.Message}";
        StatusText = "Stopped.";
    }

    public void Stop()
    {
        _engine.Stop();
        _graphEngine.Stop();
        IsRunning = false;
        _statusTimer.Stop();
        ClearLiveDisplays();
        StatusText = "Stopped.";
        SaveSettings();
    }

    public void SaveSettings()
    {
        try
        {
            _settingsStore.Save(new AppSettings
            {
                InputDeviceId = SelectedInput?.Id,
                OutputDeviceId = SelectedOutput?.Id,
                Latency = SelectedLatency.Preset,
                MonoProcessing = MonoProcessing,
                InputGainDb = (float)InputGainDb,
                OutputGainDb = (float)OutputGainDb,
                EffectsEnabled = EffectsEnabled,
                Effects = Effects.Select(CaptureEffect).ToList(),
                Mode = Mode,
                Graph = _graphInitialized ? GraphSettings.Capture(_graph) : null,
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not being able to remember settings must never break the app.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _statusTimer.Stop();
        _engine.Faulted -= OnEngineFaulted;
        _engine.Dispose();
        _graphEngine.Faulted -= OnGraphEngineFaulted;
        _graphEngine.Dispose();
        SaveSettings();
    }

    // ---------------------------------------------------------------- change handlers

    partial void OnSelectedInputChanged(AudioDeviceInfo? value) => RestartIfRunning();

    partial void OnSelectedOutputChanged(AudioDeviceInfo? value) => RestartIfRunning();

    partial void OnSelectedLatencyChanged(LatencyOption value) => RestartIfRunning();

    partial void OnMonoProcessingChanged(bool value) => RestartIfRunning();

    [RelayCommand]
    private void ResetInputGain() => InputGainDb = 0;

    [RelayCommand]
    private void ResetOutputGain() => OutputGainDb = 0;

    partial void OnInputGainDbChanged(double value) => _engine.InputGainDb = (float)value;

    partial void OnOutputGainDbChanged(double value) => _engine.OutputGainDb = (float)value;

    partial void OnEffectsEnabledChanged(bool value)
    {
        _engine.Effects.IsEnabled = value;
        _graph.EffectsEnabled = value;
    }

    partial void OnModeChanged(EditorMode value)
    {
        if (value == EditorMode.Nodes)
        {
            EnsureGraph();
        }

        // Keep playing, now through the other editor's signal path.
        RestartIfRunning();
    }

    private void RestartIfRunning()
    {
        if (!_initializing && IsRunning)
        {
            Start();
        }
    }

    private void OnGraphEngineFaulted(object? sender, AudioStreamStoppedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            IsRunning = false;
            _statusTimer.Stop();
            ClearLiveDisplays();
            StatusText = "Stopped.";
            ErrorMessage = e.Error is InvalidOperationException
                ? $"Audio stopped: {e.Error.Message}"
                : $"An audio device stopped working: {e.Error?.Message} Check your devices and press Start again.";
            RefreshDevices();
        });
    }

    private void OnEngineFaulted(object? sender, AudioStreamStoppedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            IsRunning = false;
            _statusTimer.Stop();
            ClearLiveDisplays();
            StatusText = "Stopped.";
            ErrorMessage = $"The audio device stopped working: {e.Error?.Message} Check the device and press Start again.";
            RefreshDevices();
        });
    }

    /// <summary>Refreshes meters, status text and live effect displays (called by a 30 Hz timer while running).</summary>
    internal void UpdateStatus()
    {
        if (Mode == EditorMode.Nodes)
        {
            UpdateGraphStatus();
            return;
        }

        var status = _engine.GetStatus();
        if (!status.IsRunning)
        {
            return;
        }

        InputLevelDb = Meter(InputLevelDb, status.InputPeak);
        OutputLevelDb = Meter(OutputLevelDb, status.OutputPeak);
        LatencyText = $"≈ {status.EstimatedLatencyMilliseconds:0} ms";

        string channels = status.ProcessingChannels == 1 ? "mono" : "stereo";
        string text = string.Create(
            CultureInfo.CurrentCulture,
            $"Running · {status.InputFormat.SampleRate / 1000.0:0.#} kHz in → {status.OutputFormat.SampleRate / 1000.0:0.#} kHz out · processing in {channels} · delay ≈ {status.EstimatedLatencyMilliseconds:0} ms");
        if (status.Underruns > 0)
        {
            text += $" · {status.Underruns} dropout{(status.Underruns == 1 ? "" : "s")} (try a safer latency setting)";
        }

        StatusText = text;
        foreach (var effect in Effects)
        {
            effect.Tick(isRunning: true);
        }
    }

    private void UpdateGraphStatus()
    {
        var status = _graphEngine.GetStatus();
        if (!status.IsRunning)
        {
            return;
        }

        LatencyText = $"≈ {status.EstimatedLatencyMilliseconds:0} ms";
        string channels = status.Channels == 1 ? "mono" : "stereo";
        string text = string.Create(
            CultureInfo.CurrentCulture,
            $"Running · {Plural(status.InputDevices, "input")} → {Plural(status.OutputDevices, "output")} · {status.SampleRate / 1000.0:0.#} kHz · processing in {channels} · delay ≈ {status.EstimatedLatencyMilliseconds:0} ms");
        if (status.Underruns > 0)
        {
            text += $" · {Plural(status.Underruns, "dropout")} (try a safer latency setting)";
        }

        StatusText = text;
        NodeEditor.Tick(isRunning: true);
    }

    private static string Plural(int count, string noun) => $"{count} {noun}{(count == 1 ? "" : "s")}";

    private void ClearLiveDisplays()
    {
        InputLevelDb = MeterFloorDb;
        OutputLevelDb = MeterFloorDb;
        LatencyText = "–";
        foreach (var effect in Effects)
        {
            effect.Tick(isRunning: false);
        }

        NodeEditor.Tick(isRunning: false);
    }

    // ---------------------------------------------------------------- node graph

    /// <summary>The first time the node editor opens, the effect chain becomes its starting graph.</summary>
    private void EnsureGraph()
    {
        if (_graphInitialized)
        {
            return;
        }

        _graphInitialized = true;
        const double spacing = 280;
        using (_graph.BeginUpdate())
        {
            _graph.Clear();
            var input = _graph.Add(new InputDeviceNode { DeviceId = SelectedInput?.Id });
            input.Gain.Value = (float)InputGainDb;
            NodePort previous = input.Outputs[0];
            double x = spacing;
            foreach (var effect in Effects)
            {
                var copy = EffectSettings.Capture(effect.Effect).Restore(_registry);
                if (copy is null)
                {
                    continue;
                }

                var node = _graph.Add(new EffectNode(copy) { X = x });
                _graph.Connect(previous, node.Inputs[0]);
                previous = node.Outputs[0];
                x += spacing;
            }

            // A mixer before the output leaves room for more sources.
            var mixer = _graph.Add(new MixerNode { X = x });
            _graph.Connect(previous, mixer.Inputs[0]);
            var output = _graph.Add(new OutputDeviceNode { DeviceId = SelectedOutput?.Id, X = x + spacing });
            output.Volume.Value = (float)OutputGainDb;
            _graph.Connect(mixer.Outputs[0], output.Inputs[0]);
        }

        ChooseAvailableDevices();
    }

    /// <summary>Device nodes whose device is missing switch to the default one, like the chain's device boxes.</summary>
    private void ChooseAvailableDevices()
    {
        foreach (var node in NodeEditor.Nodes.OfType<DeviceNodeViewModel>())
        {
            node.ChooseAvailableDevice();
        }
    }

    private static double Meter(double previousDb, float peak)
    {
        double db = peak > 0 ? Math.Max(MeterFloorDb, 20 * Math.Log10(peak)) : MeterFloorDb;
        return Math.Max(db, previousDb - MeterFallDbPerTick);
    }

    // ---------------------------------------------------------------- loading

    private void LoadDevices(string? preferredInputId, string? preferredOutputId)
    {
        bool wasInitializing = _initializing;
        _initializing = true;
        try
        {
            Replace(InputDevices, SafeGetDevices(AudioDeviceKind.Input));
            Replace(OutputDevices, SafeGetDevices(AudioDeviceKind.Output));

            SelectedInput = Pick(InputDevices, preferredInputId);
            SelectedOutput = Pick(OutputDevices, preferredOutputId);
        }
        finally
        {
            _initializing = wasInitializing;
        }
    }

    private IReadOnlyList<AudioDeviceInfo> SafeGetDevices(AudioDeviceKind kind)
    {
        try
        {
            return _engine.Backend.GetDevices(kind);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Could not list audio devices: {ex.Message}";
            return [];
        }
    }

    private static AudioDeviceInfo? Pick(IReadOnlyList<AudioDeviceInfo> devices, string? preferredId) =>
        devices.FirstOrDefault(d => d.Id == preferredId)
        ?? devices.FirstOrDefault(d => d.IsDefault)
        ?? devices.FirstOrDefault();

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }

    private void LoadEffects(List<EffectSettings>? saved)
    {
        if (saved is null)
        {
            // First run: a pitch shifter followed by an equalizer, both ready to use.
            foreach (var descriptor in new[] { PitchShiftEffect.Type, EqualizerEffect.Type }.Select(_registry.Find).OfType<EffectDescriptor>())
            {
                var effect = descriptor.Create();
                if (effect is PitchShiftEffect pitch)
                {
                    pitch.ApplyPreset(pitch.Presets.First(p => p.Name == "Higher voice"));
                }
                else if (effect is EqualizerEffect equalizer)
                {
                    equalizer.ApplyPreset(equalizer.Presets.First(p => p.Name == "Voice clarity"));
                }

                AddEffectViewModel(effect, descriptor);
                _engine.Effects.Add(effect);
            }
        }
        else
        {
            foreach (var entry in saved)
            {
                var effect = entry.Restore(_registry);
                if (effect is null)
                {
                    continue;
                }

                // A plug-in that isn't installed comes back as a stand-in that keeps its settings.
                var descriptor = effect is MissingEffect
                    ? new EffectDescriptor(effect.TypeId, effect.Name, MissingEffect.Explanation, () => effect)
                    : _registry.Find(entry.Type);
                if (descriptor is not null)
                {
                    AddEffectViewModel(effect, descriptor).IsCollapsed = entry.Collapsed;
                    _engine.Effects.Add(effect);
                }
            }
        }

        UpdatePositions();
    }

    private void BuildAddEffectOptions()
    {
        AddEffectOptions = AvailableEffects
            .Select(d => new AddEffectOption(d.Name, d.Description, new RelayCommand(() => AddEffect(d)), d.Group))
            .ToList();
        AddEffectMenu = AddMenuEntry.Build(AddEffectOptions, o => o.Group, o => new AddMenuEntry(o.Name, o.Description, o.Command));
        OnPropertyChanged(nameof(AddEffectOptions));
        OnPropertyChanged(nameof(AddEffectMenu));
    }

    private static EffectSettings CaptureEffect(EffectViewModel viewModel)
    {
        var settings = EffectSettings.Capture(viewModel.Effect);
        settings.Collapsed = viewModel.IsCollapsed;
        return settings;
    }

    private EffectViewModel AddEffectViewModel(AudioEffect effect, EffectDescriptor descriptor)
    {
        var viewModel = EffectViewModel.Create(effect, descriptor.Description);
        viewModel.RemoveCommand = new RelayCommand(() => RemoveEffect(viewModel));
        viewModel.MoveUpCommand = new RelayCommand(() => MoveEffect(viewModel, -1));
        viewModel.MoveDownCommand = new RelayCommand(() => MoveEffect(viewModel, +1));
        viewModel.PropertyChanged += OnEffectPropertyChanged;
        Effects.Add(viewModel);
        OnEffectsChanged();
        return viewModel;
    }

    private void RemoveEffect(EffectViewModel viewModel)
    {
        _engine.Effects.Remove(viewModel.Effect);
        viewModel.PropertyChanged -= OnEffectPropertyChanged;
        Effects.Remove(viewModel);
        OnEffectsChanged();
        UpdatePositions();
    }

    private void OnEffectPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EffectViewModel.IsCollapsed))
        {
            OnPropertyChanged(nameof(CollapseAllText));
        }
    }

    private void OnEffectsChanged()
    {
        OnPropertyChanged(nameof(HasNoEffects));
        OnPropertyChanged(nameof(HasEffects));
        OnPropertyChanged(nameof(CollapseAllText));
    }

    private void MoveEffect(EffectViewModel viewModel, int delta)
    {
        int from = Effects.IndexOf(viewModel);
        int to = from + delta;
        if (from < 0 || to < 0 || to >= Effects.Count)
        {
            return;
        }

        _engine.Effects.Move(from, to);
        Effects.Move(from, to);
        UpdatePositions();
    }

    private void UpdatePositions()
    {
        for (int i = 0; i < Effects.Count; i++)
        {
            Effects[i].Position = i + 1;
            Effects[i].CanMoveUp = i > 0;
            Effects[i].CanMoveDown = i < Effects.Count - 1;
        }
    }
}
