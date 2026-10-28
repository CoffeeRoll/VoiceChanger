using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using VoiceChanger.Core.Effects;
using VoiceChanger.Plugins.Native;

namespace VoiceChanger.Plugins;

/// <summary>A plug-in could not be loaded or started.</summary>
public sealed class PluginException(string message) : Exception(message);

/// <summary>
/// A VST3 effect running inside VoiceChanger, through the native bridge.
/// <para>
/// Each plug-in parameter the user may change becomes an <see cref="EffectParameter"/>: continuous
/// from 0 to 1, a choice for a list, a toggle or a whole number for stepped ones, all shown with the
/// plug-in's own text. A change is sent to the plug-in's controller right away and queued for its
/// processor, which gets it at the start of the next block.
/// </para>
/// <para>
/// Threading: <see cref="AudioEffect.Process"/> runs on the audio thread; everything else on the UI
/// thread. Setting the plug-in up again (for a new latency, say) briefly passes the audio through
/// unprocessed instead of waiting for the audio thread.
/// </para>
/// </summary>
public class PluginEffect : AudioEffect, IStatefulEffect, IDisposable
{
    /// <summary>Plug-in effect types are this prefix followed by the class id.</summary>
    public const string TypePrefix = "vst3:";

    /// <summary>Largest block the plug-in is set up for; longer blocks are split.</summary>
    public const int MaxBlockFrames = EffectChain.MaxBlockFrames;

    /// <summary>Lists with more entries than this are shown as a number rather than a choice.</summary>
    private const int MaxChoices = 64;

    private const int InputRegion = 0;
    private const int SidechainRegion = 1;
    private const int OutputRegion = 2;

    private readonly PluginHandle _handle;

    // Held by the audio thread while it processes and by the UI thread while it sets the plug-in up,
    // never waited for on the audio thread.
    private readonly object _gate = new();
    private readonly Binding[] _bindings;
    private readonly Dictionary<uint, Binding> _bindingsById = [];
    private readonly SynchronizationContext? _context;

    // Pinned: the channels of the input, side-chain and output (two each, mono uses the first), the
    // pointers to them the bridge reads, and the parameter changes for the next block.
    private readonly float[] _audio = GC.AllocateArray<float>(3 * 2 * MaxBlockFrames, pinned: true);
    private readonly nint[] _channels = GC.AllocateArray<nint>(3 * 2, pinned: true);
    private readonly ParamChange[] _changes;

    private float[]? _sidechain;
    private int _pendingChanges;
    private int _restartFlags;
    private volatile int _latency;
    private volatile bool _active;
    private volatile bool _crashed;
    private Origin _origin;

    private protected unsafe PluginEffect(PluginInfo info, PluginHandle handle, in PluginInfoNative native)
        : base(info.TypeId, info.Name)
    {
        Info = info;
        _handle = handle;
        _context = SynchronizationContext.Current;

        fixed (byte* inputName = native.InputName)
        {
            string name = VcPlugin.Text(inputName, 64);
            InputName = string.IsNullOrWhiteSpace(name) ? "Audio" : name;
        }

        fixed (byte* sidechainName = native.SidechainName)
        {
            string name = VcPlugin.Text(sidechainName, 64);
            SidechainName = string.IsNullOrWhiteSpace(name) ? "Side-chain" : name;
        }

        fixed (float* audio = _audio)
        {
            for (int i = 0; i < _channels.Length; i++)
            {
                _channels[i] = (nint)(audio + i * MaxBlockFrames);
            }
        }

        var bindings = new List<Binding>();
        int count = VcPlugin.GetParamCount(handle);
        for (int index = 0; index < count; index++)
        {
            ParamInfo parameter;
            if (VcPlugin.DescribeParam(handle, index, &parameter) != VcPlugin.Ok || _bindingsById.ContainsKey(parameter.Id))
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
        _changes = GC.AllocateArray<ParamChange>(Math.Max(1, _bindings.Length), pinned: true);
        handle.SetCallbacks(this, &OnEditedCallback, &OnRestartCallback);
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

    public PluginInfo Info { get; }

    /// <summary>Name of the plug-in's main input.</summary>
    public string InputName { get; }

    /// <summary>Name of the plug-in's side-chain input (when it has one).</summary>
    public string SidechainName { get; }

    /// <summary>Whether the plug-in crashed; it then passes audio through until it is loaded again.</summary>
    public bool HasCrashed => _crashed;

    /// <summary>Why the plug-in isn't processing, or <c>null</c> while it is.</summary>
    public string? Problem { get; private set; }

    public override int LatencyFrames => _active ? _latency : 0;

    /// <summary>
    /// Loads a plug-in. With a side-chain input it is also an <see cref="ISidechainEffect"/>, so its
    /// node gets a second input.
    /// </summary>
    /// <exception cref="PluginException">The bridge or the plug-in could not be loaded.</exception>
    public static unsafe PluginEffect Open(PluginInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (!VcPlugin.IsAvailable(out string? problem))
        {
            throw new PluginException(problem!);
        }

        var handle = PluginHandle.Open(info.Path, info.ClassId, out string? error)
            ?? throw new PluginException($"{info.Name} could not be loaded: {error}");
        try
        {
            PluginInfoNative native;
            if (VcPlugin.GetInfo(handle, &native) != VcPlugin.Ok)
            {
                throw new PluginException($"{info.Name} could not be loaded: {VcPlugin.LastError}");
            }

            return native.HasSidechain != 0 ? new SidechainPluginEffect(info, handle, native) : new PluginEffect(info, handle, native);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Loads a plug-in, or returns a <see cref="MissingEffect"/> named after it when it doesn't load
    /// (so a saved one keeps its settings).
    /// </summary>
    public static AudioEffect Create(PluginInfo info)
    {
        try
        {
            return Open(info);
        }
        catch (PluginException ex)
        {
            Trace.WriteLine(ex.Message);
            return MissingEffect.Create(info.TypeId, info.Name, null, null);
        }
    }

    public unsafe byte[]? SaveState()
    {
        if (_handle.IsClosed)
        {
            return null;
        }

        DeliverChanges();
        int size = VcPlugin.GetState(_handle, null, 0);
        for (int attempt = 0; attempt < 3 && size >= 0; attempt++)
        {
            var state = new byte[size];
            fixed (byte* buffer = state)
            {
                int written = VcPlugin.GetState(_handle, buffer, state.Length);
                if (written == state.Length)
                {
                    return state;
                }

                // The state grew between asking for its size and writing it: ask again.
                size = written;
            }
        }

        Trace.WriteLine($"{Name} could not save its state: {VcPlugin.LastError}");
        return null;
    }

    public unsafe bool LoadState(byte[] state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (_handle.IsClosed)
        {
            return false;
        }

        int result;
        lock (_gate)
        {
            fixed (byte* data = state)
            {
                result = VcPlugin.SetState(_handle, data, state.Length);
            }

            // The state wins over edits that haven't reached the processor yet.
            foreach (var binding in _bindings)
            {
                Volatile.Write(ref binding.Dirty, 0);
            }

            Volatile.Write(ref _pendingChanges, 0);
        }

        if (result != VcPlugin.Ok)
        {
            Trace.WriteLine($"{Name} did not accept its saved state: {VcPlugin.LastError}");
            return false;
        }

        ReadParameters();
        HandleRestart();
        return true;
    }

    /// <summary>Unloads the plug-in. Only while it is not processed (like <see cref="AudioEffect.Prepare"/>).</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _active = false;
            _handle.Dispose();
        }

        GC.SuppressFinalize(this);
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
        VcPlugin.SetParam(_handle, id, value);
        HandleRestart();
    }

    /// <summary>The side-chain for the next block (see <see cref="ISidechainEffect.SetSidechain"/>).</summary>
    public void SetSidechain(float[]? samples) => _sidechain = samples;

    /// <summary>Acts on restart requests the plug-in made (UI thread). Also runs on its own after each request.</summary>
    internal void HandleRestart()
    {
        var flags = (RestartFlags)Interlocked.Exchange(ref _restartFlags, 0);
        if (flags == RestartFlags.None || _handle.IsClosed)
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
        if (!_active || _crashed || !Monitor.TryEnter(_gate))
        {
            return;
        }

        try
        {
            if (_active)
            {
                VcPlugin.Reset(_handle);
            }
        }
        finally
        {
            Monitor.Exit(_gate);
        }
    }

    protected override unsafe void ProcessCore(Span<float> interleaved)
    {
        // Not set up, crashed, or being set up again: the audio passes through.
        if (!_active || _crashed || !Monitor.TryEnter(_gate))
        {
            return;
        }

        try
        {
            if (!_active)
            {
                return;
            }

            int channels = Channels;
            int totalFrames = interleaved.Length / channels;
            float[]? sidechain = _sidechain;
            if (sidechain is not null && sidechain.Length < totalFrames * channels)
            {
                sidechain = null;
            }

            fixed (nint* pointers = _channels)
            fixed (ParamChange* changes = _changes)
            {
                for (int start = 0; start < totalFrames; start += MaxBlockFrames)
                {
                    int frames = Math.Min(MaxBlockFrames, totalFrames - start);
                    var block = interleaved.Slice(start * channels, frames * channels);
                    Deinterleave(block, InputRegion, channels);
                    if (sidechain is not null)
                    {
                        Deinterleave(sidechain.AsSpan(start * channels, frames * channels), SidechainRegion, channels);
                    }

                    int count = CollectChanges();
                    int result = VcPlugin.Process(
                        _handle,
                        (float**)(pointers + (InputRegion * 2)),
                        sidechain is null ? null : (float**)(pointers + (SidechainRegion * 2)),
                        (float**)(pointers + (OutputRegion * 2)),
                        frames,
                        changes,
                        count);
                    if (result == VcPlugin.ErrorCrashed)
                    {
                        _crashed = true;
                        Problem = "The plug-in stopped working.";
                        return;
                    }

                    if (result == VcPlugin.Ok)
                    {
                        Interleave(block, channels);
                    }
                }
            }
        }
        finally
        {
            Monitor.Exit(_gate);
        }
    }

    [UnmanagedCallersOnly]
    private static void OnEditedCallback(nint user, uint id, double value)
    {
        try
        {
            if (GCHandle.FromIntPtr(user).Target is PluginEffect effect)
            {
                effect.OnEdited(id, value);
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Plug-in parameter edit failed: {ex}");
        }
    }

    [UnmanagedCallersOnly]
    private static void OnRestartCallback(nint user, int flags)
    {
        try
        {
            if (GCHandle.FromIntPtr(user).Target is PluginEffect effect)
            {
                effect.RequestRestart((RestartFlags)flags);
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Plug-in restart request failed: {ex}");
        }
    }

    private unsafe Binding CreateBinding(in ParamInfo info)
    {
        string title;
        string units;
        fixed (byte* text = info.Title)
        {
            title = VcPlugin.Text(text, 128);
        }

        fixed (byte* text = info.Units)
        {
            units = VcPlugin.Text(text, 32);
        }

        uint id = info.Id;
        int steps = Math.Max(0, info.StepCount);
        string key = id.ToString(CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(title))
        {
            title = "Parameter " + key;
        }

        bool isList = (info.Flags & ParamFlags.List) != 0;
        var hidden = ParamFlags.Hidden | ParamFlags.ReadOnly | ParamFlags.Bypass | ParamFlags.ProgramChange;
        bool visible = (info.Flags & hidden) == 0;
        int defaultStep = Binding.StepOf(info.DefaultValue, steps);

        EffectParameter parameter;
        if (steps == 0)
        {
            parameter = EffectParameter.Continuous(key, title, 0, 1, (float)info.DefaultValue, units, decimals: 6, formatter: v => Format(id, v, units));
        }
        else if (isList && steps < MaxChoices)
        {
            var choices = Enumerable.Range(0, steps + 1).Select(step => Format(id, (double)step / steps, "")).ToList();
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
        parameter.Value = binding.FromNormalized(VcPlugin.GetParam(_handle, id));
        return binding;
    }

    /// <summary>The plug-in's text for a value, e.g. "-6.0 dB". UI thread.</summary>
    private unsafe string Format(uint id, double normalized, string units)
    {
        if (_handle.IsClosed)
        {
            return "";
        }

        const int capacity = 128;
        byte* text = stackalloc byte[capacity];
        if (VcPlugin.FormatParam(_handle, id, normalized, text, capacity) != VcPlugin.Ok)
        {
            return normalized.ToString("0.###", CultureInfo.CurrentCulture);
        }

        string value = VcPlugin.Text(text, capacity);
        return string.IsNullOrEmpty(units) ? value : $"{value} {units}";
    }

    private void OnValueChanged(Binding binding)
    {
        if (_origin != Origin.Host || _handle.IsClosed)
        {
            return;
        }

        double normalized = binding.ToNormalized(binding.Parameter.Value);
        Queue(binding, normalized);
        VcPlugin.SetParam(_handle, binding.Id, normalized);
        HandleRestart();
    }

    /// <summary>A parameter changed in the plug-in's own window (UI thread).</summary>
    private void OnEdited(uint id, double value)
    {
        if (!_bindingsById.TryGetValue(id, out var binding))
        {
            return;
        }

        Queue(binding, value);
        Show(Origin.Editor, () => binding.Parameter.Value = binding.FromNormalized(value));
    }

    private void RequestRestart(RestartFlags flags)
    {
        Interlocked.Or(ref _restartFlags, (int)flags);

        // Requests made during VoiceChanger's own calls are handled when those return; others
        // (from the plug-in's window, say) once the UI thread is free.
        _context?.Post(static state => ((PluginEffect)state!).HandleRestart(), this);
    }

    /// <summary>Shows the plug-in's current values in the parameters, without sending them back.</summary>
    private void ReadParameters()
    {
        Show(Origin.State, () =>
        {
            foreach (var binding in _bindings)
            {
                binding.Parameter.Value = binding.FromNormalized(VcPlugin.GetParam(_handle, binding.Id));
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

    /// <summary>Moves the queued changes into <see cref="_changes"/>. With the gate held.</summary>
    private int CollectChanges()
    {
        if (Volatile.Read(ref _pendingChanges) == 0)
        {
            return 0;
        }

        Interlocked.Exchange(ref _pendingChanges, 0);
        int count = 0;
        foreach (var binding in _bindings)
        {
            if (Interlocked.Exchange(ref binding.Dirty, 0) != 0)
            {
                _changes[count++] = new ParamChange { Id = binding.Id, Value = Volatile.Read(ref binding.Pending) };
            }
        }

        return count;
    }

    /// <summary>Hands queued changes to the processor without audio. With the gate held.</summary>
    private unsafe void FlushChanges()
    {
        if (!_active || _crashed)
        {
            return;
        }

        int count = CollectChanges();
        if (count == 0)
        {
            return;
        }

        fixed (nint* pointers = _channels)
        fixed (ParamChange* changes = _changes)
        {
            VcPlugin.Process(_handle, (float**)pointers, null, (float**)(pointers + (OutputRegion * 2)), 0, changes, count);
        }
    }

    /// <summary>
    /// Makes sure the processor has every edit before its state is saved. While the audio runs it gets
    /// them with the next block anyway; settings are saved after stopping, when nothing would.
    /// </summary>
    private void DeliverChanges()
    {
        if (Volatile.Read(ref _pendingChanges) == 0 || _crashed)
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
            if (VcPlugin.Setup(_handle, sampleRate, MaxBlockFrames, channels) == VcPlugin.Ok)
            {
                _active = true;
                FlushChanges();
                _active = false;
                VcPlugin.Suspend(_handle);
            }
        }
    }

    /// <summary>Sets the plug-in up for <see cref="AudioEffect.SampleRate"/> and <see cref="AudioEffect.Channels"/>. With the gate held.</summary>
    private void Setup()
    {
        _active = false;
        if (_handle.IsClosed || _crashed)
        {
            return;
        }

        if (Channels > 2)
        {
            Problem = "Plug-ins work on mono or stereo audio only.";
            return;
        }

        if (VcPlugin.Setup(_handle, SampleRate, MaxBlockFrames, Channels) != VcPlugin.Ok)
        {
            Problem = $"{Name} can't run at {SampleRate} Hz: {VcPlugin.LastError}";
            Trace.WriteLine(Problem);
            return;
        }

        _latency = Math.Max(0, VcPlugin.GetLatency(_handle));
        Problem = null;
        _active = true;
    }

    private void Deinterleave(ReadOnlySpan<float> interleaved, int region, int channels)
    {
        int frames = interleaved.Length / channels;
        for (int channel = 0; channel < channels; channel++)
        {
            var target = _audio.AsSpan(((region * 2) + channel) * MaxBlockFrames, frames);
            for (int frame = 0, i = channel; frame < frames; frame++, i += channels)
            {
                target[frame] = interleaved[i];
            }
        }
    }

    private void Interleave(Span<float> interleaved, int channels)
    {
        int frames = interleaved.Length / channels;
        for (int channel = 0; channel < channels; channel++)
        {
            var source = _audio.AsSpan(((OutputRegion * 2) + channel) * MaxBlockFrames, frames);
            for (int frame = 0, i = channel; frame < frames; frame++, i += channels)
            {
                interleaved[i] = source[frame];
            }
        }
    }

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
    internal SidechainPluginEffect(PluginInfo info, PluginHandle handle, in PluginInfoNative native)
        : base(info, handle, native)
    {
    }
}
