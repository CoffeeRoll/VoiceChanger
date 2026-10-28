using System.Diagnostics;
using System.Globalization;
using VoiceChanger.Core.Effects;
using VoiceChanger.Plugins.Hosting;
using VoiceChanger.Plugins.Native;

namespace VoiceChanger.Plugins;

/// <summary>A plug-in could not be loaded or started.</summary>
public sealed class PluginException(string message) : Exception(message);

/// <summary>
/// A VST3 effect running inside VoiceChanger, in this process or in a host process of its own.
/// <para>
/// Each plug-in parameter the user may change becomes an <see cref="EffectParameter"/>: continuous
/// from 0 to 1, a choice for a list, a toggle or a whole number for stepped ones, all shown with the
/// plug-in's own text. A change is sent to the plug-in's controller right away and queued for its
/// processor, which gets it at the start of the next block. The plug-in's programs and preset files
/// are its presets.
/// </para>
/// <para>
/// Threading: <see cref="AudioEffect.Process"/> runs on the audio thread; everything else on the UI
/// thread. Setting the plug-in up again (for a new latency, say) briefly passes the audio through
/// unprocessed instead of waiting for the audio thread.
/// </para>
/// <para>
/// A plug-in in a host process that crashes or hangs is started again, with its last saved state and
/// the current parameter values; after <see cref="MaxRestarts"/> crashes within a minute it is given
/// up (<see cref="Crashed"/>), like one in this process that crashes.
/// </para>
/// </summary>
public class PluginEffect : AudioEffect, IStatefulEffect, IDisposable, IPluginEvents
{
    /// <summary>Plug-in effect types are this prefix followed by the class id.</summary>
    public const string TypePrefix = "vst3:";

    /// <summary>Largest block the plug-in is set up for; longer blocks are split.</summary>
    public const int MaxBlockFrames = EffectChain.MaxBlockFrames;

    /// <summary>Restarts of a crashed host process within <see cref="RestartWindow"/> before the plug-in is given up.</summary>
    public const int MaxRestarts = 3;

    private static readonly TimeSpan RestartWindow = TimeSpan.FromMinutes(1);

    private readonly PluginHosting _hosting;

    // Held by the audio thread while it processes and by the UI thread while it sets the plug-in up,
    // never waited for on the audio thread.
    private readonly object _gate = new();
    private readonly Binding[] _bindings;
    private readonly Dictionary<uint, Binding> _bindingsById = [];
    private readonly Dictionary<EffectPreset, PresetSource> _presetSources = [];
    private readonly SynchronizationContext? _context;
    private readonly List<DateTime> _restarts = [];

    private IPluginInstance _plugin;
    private byte[]? _lastState;
    private float[]? _sidechain;
    private int _pendingChanges;
    private int _restartFlags;
    private volatile int _latency;
    private volatile bool _active;
    private volatile bool _crashed;
    private volatile bool _dead;
    private bool _disposed;
    private Origin _origin;
    private EffectPreset? _appliedPreset;
    private Dictionary<uint, float>? _presetValues;

    private protected PluginEffect(PluginInfo info, IPluginInstance plugin, PluginHosting hosting)
        : base(info.TypeId, info.Name)
    {
        Info = info;
        _plugin = plugin;
        _hosting = hosting;
        _context = SynchronizationContext.Current;

        var description = plugin.Description;
        InputName = string.IsNullOrWhiteSpace(description.InputName) ? "Audio" : description.InputName;
        SidechainName = string.IsNullOrWhiteSpace(description.SidechainName) ? "Side-chain" : description.SidechainName;

        var bindings = new List<Binding>();
        foreach (var parameter in description.Parameters)
        {
            if (_bindingsById.ContainsKey(parameter.Id))
            {
                continue;
            }

            var binding = CreateBinding(parameter);
            bindings.Add(binding);
            _bindingsById[binding.Id] = binding;

            // Subscribed before AddParameter, so the change is queued before the effect hears of it.
            binding.Parameter.ValueChanged += (_, _) => OnValueChanged(binding);
            if (binding.IsVisible)
            {
                AddParameter(binding.Parameter);
            }
        }

        _bindings = [.. bindings];
        AddPresets(description, hosting.PresetFolders);
        plugin.Events = this;
        _lastState = plugin.GetState();
    }

    /// <summary>Where a parameter change comes from, so it isn't sent back where it came from.</summary>
    private enum Origin
    {
        /// <summary>VoiceChanger: tell the controller and the processor.</summary>
        Host,

        /// <summary>The plug-in's own window: the controller knows, tell the processor.</summary>
        Editor,

        /// <summary>The plug-in's state: both parts know.</summary>
        State,
    }

    /// <summary>The plug-in crashed (or kept crashing) and was given up; it passes audio through now. UI thread.</summary>
    public event EventHandler? Crashed;

    /// <summary><see cref="Problem"/>, <see cref="HasCrashed"/> or <see cref="IsEditorOpen"/> changed. UI thread.</summary>
    public event EventHandler? StatusChanged;

    public PluginInfo Info { get; }

    /// <summary>Name of the plug-in's main input.</summary>
    public string InputName { get; }

    /// <summary>Name of the plug-in's side-chain input (when it has one).</summary>
    public string SidechainName { get; }

    /// <summary>Whether the plug-in was given up after crashing; it passes audio through.</summary>
    public bool HasCrashed => _crashed;

    /// <summary>Why the plug-in isn't processing, or <c>null</c> while it is.</summary>
    public string? Problem { get; private set; }

    /// <summary>Whether it runs in a host process of its own.</summary>
    public bool RunsInSeparateProcess => _plugin is not LocalPlugin;

    /// <summary>Whether the plug-in has a window of its own to open.</summary>
    public bool HasEditor => _plugin.Description.HasEditor && !_crashed;

    public bool IsEditorOpen => _plugin.IsEditorOpen;

    public override int LatencyFrames => _active ? _latency : 0;

    /// <summary>The loaded plug-in (for the tests).</summary>
    internal IPluginInstance Instance => _plugin;

    /// <summary>Whether the native bridge is there to load plug-ins with, and why not when it isn't.</summary>
    public static bool IsBridgeAvailable(out string? problem) => VcPlugin.IsAvailable(out problem);

    /// <summary>
    /// Loads a plug-in. With a side-chain input it is also an <see cref="ISidechainEffect"/>, so its
    /// node gets a second input.
    /// </summary>
    /// <exception cref="PluginException">The bridge or the plug-in could not be loaded.</exception>
    public static PluginEffect Open(PluginInfo info, PluginHosting? hosting = null)
    {
        ArgumentNullException.ThrowIfNull(info);
        hosting ??= PluginHosting.InProcess;
        IPluginInstance plugin;
        try
        {
            plugin = hosting.Start(info);
        }
        catch (PluginException ex)
        {
            throw new PluginException($"{info.Name} could not be loaded: {ex.Message}");
        }

        try
        {
            return plugin.Description.HasSidechain ? new SidechainPluginEffect(info, plugin, hosting) : new PluginEffect(info, plugin, hosting);
        }
        catch
        {
            plugin.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Loads a plug-in, or returns a <see cref="MissingEffect"/> named after it when it doesn't load
    /// (so a saved one keeps its settings).
    /// </summary>
    public static AudioEffect Create(PluginInfo info, PluginHosting? hosting = null)
    {
        try
        {
            return Open(info, hosting);
        }
        catch (PluginException ex)
        {
            Trace.WriteLine(ex.Message);
            return MissingEffect.Create(info.TypeId, info.Name, null, null);
        }
    }

    public byte[]? SaveState()
    {
        if (!IsUsable)
        {
            return _lastState;
        }

        DeliverChanges();
        var state = _plugin.GetState();
        if (state is null)
        {
            Trace.WriteLine($"{Name} could not save its state.");
            return _lastState;
        }

        _lastState = state;
        return state;
    }

    public bool LoadState(byte[] state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!IsUsable)
        {
            return false;
        }

        string? error;
        lock (_gate)
        {
            error = _plugin.SetState(state);
            DropQueuedChanges();
        }

        if (error is not null)
        {
            Trace.WriteLine($"{Name} did not accept its saved state: {error}");
            return false;
        }

        _lastState = state;
        _appliedPreset = null;
        ReadParameters();
        HandleRestart();
        return true;
    }

    /// <summary>Saves the plug-in's settings as a .vstpreset file. Returns null, or why it failed.</summary>
    public string? SavePreset(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!IsUsable)
        {
            return Problem ?? "The plug-in isn't running.";
        }

        DeliverChanges();
        return _plugin.SavePreset(path);
    }

    public override void ApplyPreset(EffectPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        if (!_presetSources.TryGetValue(preset, out var source))
        {
            base.ApplyPreset(preset);
            return;
        }

        if (!IsUsable)
        {
            return;
        }

        string? error = source.Path is { } path ? LoadPresetFile(path) : SelectProgram(source.Program, source.Value);
        if (error is not null)
        {
            Trace.WriteLine($"{Name} could not load the preset {preset.Name}: {error}");
            return;
        }

        _appliedPreset = preset;
        _presetValues = _bindings.Where(b => b.IsVisible).ToDictionary(b => b.Id, b => b.Parameter.Value);
    }

    /// <summary>The plug-in preset last applied, while no setting changed since.</summary>
    public override EffectPreset? FindMatchingPreset()
    {
        if (_appliedPreset is not null && _presetValues is not null &&
            _bindings.Where(b => b.IsVisible).All(b => _presetValues.TryGetValue(b.Id, out float value) && value.Equals(b.Parameter.Value)))
        {
            return _appliedPreset;
        }

        return null;
    }

    /// <summary>Opens the plug-in's window, owned by <paramref name="owner"/> (a native window, or 0). Returns null, or why it failed.</summary>
    public string? OpenEditor(nint owner)
    {
        if (!HasEditor || !IsUsable)
        {
            return Problem ?? "The plug-in has no window.";
        }

        string? error = _plugin.OpenEditor(owner, Name);
        StatusChanged?.Invoke(this, EventArgs.Empty);
        return error;
    }

    public void CloseEditor()
    {
        if (_plugin.IsEditorOpen)
        {
            _plugin.CloseEditor();
        }
    }

    /// <summary>Unloads the plug-in. Only while it is not processed (like <see cref="AudioEffect.Prepare"/>).</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _active = false;
            _disposed = true;
            _plugin.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>The side-chain for the next block (see <see cref="ISidechainEffect.SetSidechain"/>).</summary>
    public void SetSidechain(float[]? samples) => _sidechain = samples;

    void IPluginEvents.Edited(uint id, double value)
    {
        if (!_bindingsById.TryGetValue(id, out var binding))
        {
            return;
        }

        Queue(binding, value);
        Show(Origin.Editor, () => binding.Parameter.Value = binding.FromNormalized(value));
    }

    void IPluginEvents.RestartRequested(RestartFlags flags)
    {
        Interlocked.Or(ref _restartFlags, (int)flags);

        // Requests made during VoiceChanger's own calls are handled when those return; others
        // (from the plug-in's window, say) once the UI thread is free.
        _context?.Post(static state => ((PluginEffect)state!).HandleRestart(), this);
    }

    void IPluginEvents.EditorClosed() => StatusChanged?.Invoke(this, EventArgs.Empty);

    void IPluginEvents.Died(string reason)
    {
        if (_disposed)
        {
            return;
        }

        _dead = true;
        Trace.WriteLine($"{Name}: {reason}");
        _context?.Post(static state => ((PluginEffect)state!).Recover(), this);
    }

    /// <summary>Sets a plug-in parameter by its id, also one that isn't shown (UI thread).</summary>
    internal void SetNormalized(uint id, double value)
    {
        if (!_bindingsById.TryGetValue(id, out var binding))
        {
            return;
        }

        if (binding.IsVisible)
        {
            binding.Parameter.Value = binding.FromNormalized(value);
            return;
        }

        Queue(binding, value);
        _plugin.SetParam(id, value);
        HandleRestart();
    }

    /// <summary>A plug-in parameter's current value as the plug-in has it (UI thread).</summary>
    internal double GetNormalized(uint id) => _plugin.GetParam(id);

    /// <summary>Acts on restart requests the plug-in made (UI thread). Also runs on its own after each request.</summary>
    internal void HandleRestart()
    {
        var flags = (RestartFlags)Interlocked.Exchange(ref _restartFlags, 0);
        if (flags == RestartFlags.None || !IsUsable)
        {
            return;
        }

        // A new latency or new buses only take effect once the plug-in is set up again.
        if ((flags & (RestartFlags.ReloadComponent | RestartFlags.IoChanged | RestartFlags.LatencyChanged)) != 0 && IsPrepared)
        {
            lock (_gate)
            {
                FlushChanges();
                Setup();
            }
        }

        if ((flags & (RestartFlags.ReloadComponent | RestartFlags.ParamValuesChanged)) != 0)
        {
            ReadParameters();
        }
    }

    /// <summary>
    /// After its host process died: starts the plug-in again with its last state and the current
    /// values, or gives it up when it keeps crashing. UI thread; runs on its own after a crash.
    /// Returns whether the plug-in runs again.
    /// </summary>
    internal bool Recover()
    {
        if (!_dead || _disposed || _crashed)
        {
            return false;
        }

        IPluginInstance old;
        lock (_gate)
        {
            _active = false;
            old = _plugin;
        }

        old.Dispose();
        var now = DateTime.UtcNow;
        _restarts.RemoveAll(time => now - time > RestartWindow);
        if (_restarts.Count >= MaxRestarts)
        {
            GiveUp("The plug-in kept crashing and was stopped.");
            return false;
        }

        _restarts.Add(now);
        IPluginInstance plugin;
        try
        {
            plugin = _hosting.Start(Info);
        }
        catch (PluginException ex)
        {
            GiveUp($"The plug-in could not be started again: {ex.Message}");
            return false;
        }

        // Its settings: the last saved state, then the values set since.
        plugin.Events = this;
        if (_lastState is not null)
        {
            plugin.SetState(_lastState);
        }

        foreach (var binding in _bindings.Where(b => b.IsVisible))
        {
            double value = binding.ToNormalized(binding.Parameter.Value);
            plugin.SetParam(binding.Id, value);
            Queue(binding, value);
        }

        lock (_gate)
        {
            _plugin = plugin;
            _dead = false;
            Problem = null;
            if (IsPrepared)
            {
                Setup();
            }
        }

        Interlocked.Exchange(ref _restartFlags, 0);
        StatusChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    protected override void OnPrepare()
    {
        lock (_gate)
        {
            Setup();
        }
    }

    protected override void OnParametersChanged()
    {
        // Changes are queued as they happen and sent with the next block.
    }

    protected override void OnReset()
    {
        if (!_active || _crashed || _dead || !Monitor.TryEnter(_gate))
        {
            return;
        }

        try
        {
            if (_active)
            {
                _plugin.Reset();
            }
        }
        finally
        {
            Monitor.Exit(_gate);
        }
    }

    protected override unsafe void ProcessCore(Span<float> interleaved)
    {
        // Not set up, crashed, restarting, or being set up again: the audio passes through.
        if (!_active || _crashed || _dead || !Monitor.TryEnter(_gate))
        {
            return;
        }

        try
        {
            if (!_active)
            {
                return;
            }

            var buffers = _plugin.Buffers;
            int channels = Channels;
            int totalFrames = interleaved.Length / channels;
            float[]? sidechain = _sidechain;
            if (sidechain is not null && sidechain.Length < totalFrames * channels)
            {
                sidechain = null;
            }

            for (int start = 0; start < totalFrames; start += MaxBlockFrames)
            {
                int frames = Math.Min(MaxBlockFrames, totalFrames - start);
                var block = interleaved.Slice(start * channels, frames * channels);
                Deinterleave(block, buffers, PluginBuffers.Input, channels);
                if (sidechain is not null)
                {
                    Deinterleave(sidechain.AsSpan(start * channels, frames * channels), buffers, PluginBuffers.Sidechain, channels);
                }

                int result = _plugin.Process(frames, sidechain is not null, CollectChanges(buffers));
                if (result == VcPlugin.ErrorCrashed)
                {
                    // In this process: whatever it broke may still be broken. Give it up.
                    _crashed = true;
                    Problem = "The plug-in crashed and was stopped.";
                    _context?.Post(static state => ((PluginEffect)state!).OnCrashed(), this);
                    return;
                }

                if (result == VcPlugin.Ok)
                {
                    Interleave(block, buffers, channels);
                }
            }
        }
        finally
        {
            Monitor.Exit(_gate);
        }
    }

    private bool IsUsable => !_disposed && !_crashed && !_dead;

    private static void Deinterleave(ReadOnlySpan<float> interleaved, PluginBuffers buffers, int region, int channels)
    {
        int frames = interleaved.Length / channels;
        for (int channel = 0; channel < channels; channel++)
        {
            var target = buffers.Span(region, channel, frames);
            for (int frame = 0, i = channel; frame < frames; frame++, i += channels)
            {
                target[frame] = interleaved[i];
            }
        }
    }

    private static void Interleave(Span<float> interleaved, PluginBuffers buffers, int channels)
    {
        int frames = interleaved.Length / channels;
        for (int channel = 0; channel < channels; channel++)
        {
            var source = buffers.Span(PluginBuffers.Output, channel, frames);
            for (int frame = 0, i = channel; frame < frames; frame++, i += channels)
            {
                interleaved[i] = source[frame];
            }
        }
    }

    private Binding CreateBinding(PluginParameterInfo info)
    {
        uint id = info.Id;
        int steps = info.StepCount;
        string key = id.ToString(CultureInfo.InvariantCulture);
        string title = string.IsNullOrWhiteSpace(info.Title) ? "Parameter " + key : info.Title;
        string units = info.Units;
        var hidden = ParamFlags.Hidden | ParamFlags.ReadOnly | ParamFlags.Bypass | ParamFlags.ProgramChange;
        bool visible = (info.Flags & hidden) == 0;
        int defaultStep = Binding.StepOf(info.DefaultValue, steps);

        EffectParameter parameter;
        if (steps == 0)
        {
            parameter = EffectParameter.Continuous(key, title, 0, 1, (float)info.DefaultValue, units, decimals: 6, formatter: v => Format(id, v, units));
        }
        else if (info.Choices is { Count: >= 2 } choices)
        {
            parameter = EffectParameter.Choice(key, title, choices, defaultStep);
        }
        else if (steps == 1)
        {
            parameter = EffectParameter.Toggle(key, title, defaultStep == 1);
        }
        else
        {
            parameter = EffectParameter.Integer(key, title, 0, steps, defaultStep, units, formatter: v => Format(id, v / steps, units));
        }

        var binding = new Binding(id, steps, parameter, visible);
        parameter.Value = binding.FromNormalized(info.Value);
        return binding;
    }

    /// <summary>Programs first, then the preset files.</summary>
    private void AddPresets(PluginDescription description, IReadOnlyList<string> folders)
    {
        if (description.ProgramParameter is uint program && description.Programs.Count > 1)
        {
            for (int index = 0; index < description.Programs.Count; index++)
            {
                AddPreset(description.Programs[index], new PresetSource(program, (double)index / (description.Programs.Count - 1), null));
            }
        }

        foreach (var (name, path) in PluginPresetFiles.Find(Info, folders))
        {
            AddPreset(name, new PresetSource(0, 0, path));
        }
    }

    private void AddPreset(string name, PresetSource source)
    {
        // The values belong to the plug-in: the record only names the preset.
        var preset = new EffectPreset(name, new Dictionary<string, float>(), IsPartial: true);
        _presetSources[preset] = source;
        AddPreset(preset);
    }

    private string? SelectProgram(uint id, double value)
    {
        if (_bindingsById.TryGetValue(id, out var binding))
        {
            Queue(binding, value);
        }

        _plugin.SetParam(id, value);
        ReadParameters();
        HandleRestart();
        return null;
    }

    private string? LoadPresetFile(string path)
    {
        string? error;
        lock (_gate)
        {
            error = _plugin.LoadPreset(path);
            if (error is null)
            {
                DropQueuedChanges();
            }
        }

        if (error is null)
        {
            ReadParameters();
            HandleRestart();
        }

        return error;
    }

    /// <summary>The plug-in's text for a value, e.g. "-6.0 dB". UI thread.</summary>
    private string Format(uint id, double normalized, string units)
    {
        string? value = IsUsable ? _plugin.FormatParam(id, normalized) : null;
        if (value is null)
        {
            return normalized.ToString("0.###", CultureInfo.CurrentCulture);
        }

        return string.IsNullOrEmpty(units) ? value : $"{value} {units}";
    }

    private void OnValueChanged(Binding binding)
    {
        if (_origin != Origin.Host || !IsUsable)
        {
            return;
        }

        double normalized = binding.ToNormalized(binding.Parameter.Value);
        Queue(binding, normalized);
        _plugin.SetParam(binding.Id, normalized);
        HandleRestart();
    }

    private void OnCrashed()
    {
        StatusChanged?.Invoke(this, EventArgs.Empty);
        Crashed?.Invoke(this, EventArgs.Empty);
    }

    private void GiveUp(string problem)
    {
        _crashed = true;
        _dead = false;
        Problem = problem;
        Trace.WriteLine($"{Name}: {problem}");
        OnCrashed();
    }

    /// <summary>Shows the plug-in's current values in the parameters, without sending them back.</summary>
    private void ReadParameters()
    {
        Show(Origin.State, () =>
        {
            foreach (var binding in _bindings)
            {
                binding.Parameter.Value = binding.FromNormalized(_plugin.GetParam(binding.Id));
            }
        });
    }

    private void Show(Origin origin, Action update)
    {
        var previous = _origin;
        _origin = origin;
        try
        {
            update();
        }
        finally
        {
            _origin = previous;
        }
    }

    private void Queue(Binding binding, double normalized)
    {
        Interlocked.Exchange(ref binding.Pending, normalized);
        Volatile.Write(ref binding.Dirty, 1);
        Interlocked.Increment(ref _pendingChanges);
    }

    /// <summary>Forgets edits that haven't reached the processor: a state or preset replaced them. With the gate held.</summary>
    private void DropQueuedChanges()
    {
        foreach (var binding in _bindings)
        {
            Volatile.Write(ref binding.Dirty, 0);
        }

        Volatile.Write(ref _pendingChanges, 0);
    }

    /// <summary>
    /// Moves queued changes into the buffers' change list, as many as fit; the rest go with the next
    /// block. With the gate held.
    /// </summary>
    private unsafe int CollectChanges(PluginBuffers buffers)
    {
        if (Volatile.Read(ref _pendingChanges) == 0)
        {
            return 0;
        }

        Interlocked.Exchange(ref _pendingChanges, 0);
        var changes = buffers.Changes;
        int count = 0;
        foreach (var binding in _bindings)
        {
            if (Volatile.Read(ref binding.Dirty) == 0)
            {
                continue;
            }

            if (count == PluginBuffers.MaxChanges)
            {
                Interlocked.Increment(ref _pendingChanges);
                break;
            }

            Interlocked.Exchange(ref binding.Dirty, 0);
            changes[count++] = new ParamChange { Id = binding.Id, Value = Volatile.Read(ref binding.Pending) };
        }

        return count;
    }

    /// <summary>Hands queued changes to the processor without audio. With the gate held.</summary>
    private void FlushChanges()
    {
        if (!_active || !IsUsable)
        {
            return;
        }

        while (Volatile.Read(ref _pendingChanges) != 0)
        {
            int count = CollectChanges(_plugin.Buffers);
            if (count == 0)
            {
                break;
            }

            _plugin.Process(0, false, count);
        }
    }

    /// <summary>
    /// Makes sure the processor has every edit before its state is saved. While the audio runs it gets
    /// them with the next block anyway; settings are saved after stopping, when nothing would.
    /// </summary>
    private void DeliverChanges()
    {
        if (Volatile.Read(ref _pendingChanges) == 0 || !IsUsable)
        {
            return;
        }

        lock (_gate)
        {
            if (_active)
            {
                FlushChanges();
                return;
            }

            // Never set up (or it failed): only a set-up processor takes parameter changes.
            int sampleRate = IsPrepared ? SampleRate : 48000;
            int channels = IsPrepared ? Math.Min(Channels, 2) : 2;
            if (_plugin.Setup(sampleRate, channels, out _) is null)
            {
                _active = true;
                FlushChanges();
                _active = false;
                _plugin.Suspend();
            }
        }
    }

    /// <summary>Sets the plug-in up for <see cref="AudioEffect.SampleRate"/> and <see cref="AudioEffect.Channels"/>. With the gate held.</summary>
    private void Setup()
    {
        _active = false;
        if (!IsUsable)
        {
            return;
        }

        if (Channels > 2)
        {
            Problem = "Plug-ins work on mono or stereo audio only.";
            return;
        }

        if (_plugin.Setup(SampleRate, Channels, out int latency) is { } error)
        {
            Problem = $"{Name} can't run at {SampleRate} Hz: {error}";
            Trace.WriteLine(Problem);
            return;
        }

        _latency = latency;
        Problem = null;
        _active = true;
    }

    /// <summary>A program, or a preset file.</summary>
    private sealed record PresetSource(uint Program, double Value, string? Path);

    /// <summary>A plug-in parameter and the <see cref="EffectParameter"/> that shows it.</summary>
    private sealed class Binding(uint id, int steps, EffectParameter parameter, bool isVisible)
    {
        /// <summary>The normalised value for the processor, valid while <see cref="Dirty"/> is set.</summary>
        public double Pending;

        public int Dirty;

        public uint Id { get; } = id;

        /// <summary>0 for a continuous parameter, otherwise the number of steps (values - 1).</summary>
        public int Steps { get; } = steps;

        public EffectParameter Parameter { get; } = parameter;

        /// <summary>Whether the user sees it: not read-only, hidden, the bypass or the program.</summary>
        public bool IsVisible { get; } = isVisible;

        /// <summary>The step a normalised value falls on, the way VST3 rounds.</summary>
        public static int StepOf(double normalized, int steps) =>
            Math.Min(steps, (int)(Math.Clamp(normalized, 0, 1) * (steps + 1)));

        public double ToNormalized(float value) => Steps == 0 ? Math.Clamp(value, 0, 1) : Math.Clamp(value / Steps, 0, 1);

        public float FromNormalized(double normalized) => Steps == 0 ? (float)Math.Clamp(normalized, 0, 1) : StepOf(normalized, Steps);
    }
}

/// <summary>A plug-in with a side-chain input: its node gets a second socket for it.</summary>
internal sealed class SidechainPluginEffect : PluginEffect, ISidechainEffect
{
    internal SidechainPluginEffect(PluginInfo info, IPluginInstance plugin, PluginHosting hosting)
        : base(info, plugin, hosting)
    {
    }
}
