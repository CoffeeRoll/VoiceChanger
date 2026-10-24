using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoiceChanger.Core.Audio;
using VoiceChanger.Core.Effects;
using VoiceChanger.Core.Settings;

namespace VoiceChanger.App.ViewModels;

public sealed record LatencyOption(LatencyPreset Preset, string Name, string Description)
{
    public override string ToString() => Name;
}

/// <summary>An entry of the "Add effect" menu.</summary>
public sealed record AddEffectOption(string Name, string Description, IRelayCommand Command);

/// <summary>
/// State of the main window: device selection, the effect chain, the master switch and the
/// audio engine's live status.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private const double MeterFloorDb = -60;
    private const double MeterFallDbPerTick = 1.2;

    private readonly AudioEngine _engine;
    private readonly SettingsStore _settingsStore;
    private readonly EffectRegistry _registry;
    private readonly DispatcherTimer _statusTimer;
    private bool _initializing = true;
    private bool _disposed;

    public MainWindowViewModel(IAudioBackend backend, SettingsStore settingsStore, EffectRegistry? registry = null)
    {
        _settingsStore = settingsStore;
        _registry = registry ?? EffectRegistry.Default;
        _engine = new AudioEngine(backend);
        _engine.Faulted += OnEngineFaulted;

        LatencyOptions =
        [
            new(LatencyPreset.UltraLow, "Ultra low", "Smallest delay. May crackle on slower machines or Bluetooth devices."),
            new(LatencyPreset.Low, "Low (recommended)", "Low delay that works well on most machines."),
            new(LatencyPreset.Balanced, "Balanced", "A little more delay in exchange for extra safety against dropouts."),
            new(LatencyPreset.Safe, "Safe", "Largest buffers. Use this if you hear crackles or dropouts."),
        ];
        AvailableEffects = _registry.Descriptors;
        AddEffectOptions = AvailableEffects
            .Select(d => new AddEffectOption(d.Name, d.Description, new RelayCommand(() => AddEffect(d))))
            .ToList();
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
    public partial double InputGainDb { get; set; }

    [ObservableProperty]
    public partial double OutputGainDb { get; set; }

    public string BackendName { get; }

    public bool IsBackendSupported { get; }

    // ---------------------------------------------------------------- effects (steps 2 and 4)

    public IReadOnlyList<EffectDescriptor> AvailableEffects { get; }

    public IReadOnlyList<AddEffectOption> AddEffectOptions { get; }

    public ObservableCollection<EffectViewModel> Effects { get; } = [];

    public bool HasNoEffects => Effects.Count == 0;

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

        // A device that disappeared was replaced by another one: move the running audio over.
        if (IsRunning && (SelectedInput != input || SelectedOutput != output))
        {
            Start();
        }
    }

    [RelayCommand]
    private void AddEffect(EffectDescriptor descriptor)
    {
        AddEffectViewModel(descriptor.Create(), descriptor);
        _engine.Effects.Add(Effects[^1].Effect);
        UpdatePositions();
    }

    [RelayCommand]
    private void DismissError() => ErrorMessage = null;

    public void Start()
    {
        ErrorMessage = null;
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
            _engine.Start(SelectedInput.Id, SelectedOutput.Id, options);
            IsRunning = true;
            _statusTimer.Start();
            UpdateStatus();
        }
        catch (Exception ex)
        {
            IsRunning = false;
            _statusTimer.Stop();
            InputLevelDb = MeterFloorDb;
            OutputLevelDb = MeterFloorDb;
            LatencyText = "–";
            ErrorMessage = $"Could not start audio: {ex.Message}";
            StatusText = "Stopped.";
        }
    }

    public void Stop()
    {
        _engine.Stop();
        IsRunning = false;
        _statusTimer.Stop();
        InputLevelDb = MeterFloorDb;
        OutputLevelDb = MeterFloorDb;
        LatencyText = "–";
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
                Effects = Effects.Select(e => EffectSettings.Capture(e.Effect)).ToList(),
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
        SaveSettings();
    }

    // ---------------------------------------------------------------- change handlers

    partial void OnSelectedInputChanged(AudioDeviceInfo? value) => RestartIfRunning();

    partial void OnSelectedOutputChanged(AudioDeviceInfo? value) => RestartIfRunning();

    partial void OnSelectedLatencyChanged(LatencyOption value) => RestartIfRunning();

    partial void OnMonoProcessingChanged(bool value) => RestartIfRunning();

    partial void OnInputGainDbChanged(double value) => _engine.InputGainDb = (float)value;

    partial void OnOutputGainDbChanged(double value) => _engine.OutputGainDb = (float)value;

    partial void OnEffectsEnabledChanged(bool value) => _engine.Effects.IsEnabled = value;

    private void RestartIfRunning()
    {
        if (!_initializing && IsRunning)
        {
            Start();
        }
    }

    private void OnEngineFaulted(object? sender, AudioStreamStoppedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            IsRunning = false;
            _statusTimer.Stop();
            InputLevelDb = MeterFloorDb;
            OutputLevelDb = MeterFloorDb;
            LatencyText = "–";
            StatusText = "Stopped.";
            ErrorMessage = $"The audio device stopped working: {e.Error?.Message} Check the device and press Start again.";
            RefreshDevices();
        });
    }

    private void UpdateStatus()
    {
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
                var descriptor = _registry.Find(entry.Type);
                if (effect is not null && descriptor is not null)
                {
                    AddEffectViewModel(effect, descriptor);
                    _engine.Effects.Add(effect);
                }
            }
        }

        UpdatePositions();
    }

    private void AddEffectViewModel(AudioEffect effect, EffectDescriptor descriptor)
    {
        var viewModel = EffectViewModel.Create(effect, descriptor.Description);
        viewModel.RemoveCommand = new RelayCommand(() => RemoveEffect(viewModel));
        viewModel.MoveUpCommand = new RelayCommand(() => MoveEffect(viewModel, -1));
        viewModel.MoveDownCommand = new RelayCommand(() => MoveEffect(viewModel, +1));
        Effects.Add(viewModel);
        OnPropertyChanged(nameof(HasNoEffects));
    }

    private void RemoveEffect(EffectViewModel viewModel)
    {
        _engine.Effects.Remove(viewModel.Effect);
        Effects.Remove(viewModel);
        OnPropertyChanged(nameof(HasNoEffects));
        UpdatePositions();
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
