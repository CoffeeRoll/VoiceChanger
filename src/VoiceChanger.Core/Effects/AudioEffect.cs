namespace VoiceChanger.Core.Effects;

/// <summary>
/// A named set of parameter values. Parameters that are not listed go back to their defaults, unless
/// the preset is <see cref="IsPartial"/>, in which case they keep their current values.
/// </summary>
public sealed record EffectPreset(string Name, IReadOnlyDictionary<string, float> Values, bool IsPartial = false)
{
    /// <summary>Whether <paramref name="parameters"/> currently hold exactly this preset's values.</summary>
    public bool Matches(IEnumerable<EffectParameter> parameters) =>
        parameters.All(p => Values.TryGetValue(p.Id, out float value)
            ? p.Value.Equals(p.Coerce(value))
            : IsPartial || p.IsDefault);

    public override string ToString() => Name;
}

/// <summary>
/// Base class for real-time effects that process interleaved 32-bit float audio in place.
/// <para>
/// Threading model: <see cref="Prepare"/> is called while the effect is not being processed.
/// <see cref="Process"/> runs on the audio thread. Parameters may be changed from any thread at any
/// time; changes are picked up at the start of the next processed block through
/// <see cref="OnParametersChanged"/>, which always runs on the processing side.
/// </para>
/// </summary>
public abstract class AudioEffect
{
    private readonly List<EffectParameter> _parameters = [];
    private readonly List<EffectPreset> _presets = [];
    private int _parameterVersion;
    private int _appliedVersion = -1;
    private volatile bool _isEnabled = true;

    protected AudioEffect(string typeId, string name)
    {
        TypeId = typeId;
        Name = name;
    }

    /// <summary>Stable identifier used to save and restore effects.</summary>
    public string TypeId { get; }

    public string Name { get; }

    public IReadOnlyList<EffectParameter> Parameters => _parameters;

    public IReadOnlyList<EffectPreset> Presets => _presets;

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled == value)
            {
                return;
            }

            _isEnabled = value;
            EnabledChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? EnabledChanged;

    public int SampleRate { get; private set; }

    public int Channels { get; private set; }

    public bool IsPrepared => SampleRate > 0;

    /// <summary>Delay the effect adds to the signal, in frames at <see cref="SampleRate"/>.</summary>
    public virtual int LatencyFrames => 0;

    /// <summary>Whether the effect was audible in the previous block. Owned by <see cref="EffectChain"/>.</summary>
    internal bool WasActive { get; set; }

    public EffectParameter? FindParameter(string id) =>
        _parameters.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    public EffectParameter GetParameter(string id) =>
        FindParameter(id) ?? throw new KeyNotFoundException($"{Name} has no parameter '{id}'.");

    public void ApplyPreset(EffectPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        foreach (var parameter in _parameters)
        {
            if (preset.Values.TryGetValue(parameter.Id, out float value))
            {
                parameter.Value = value;
            }
            else if (!preset.IsPartial)
            {
                parameter.Value = parameter.DefaultValue;
            }
        }
    }

    /// <summary>The first preset whose values equal the current settings, if any.</summary>
    public EffectPreset? FindMatchingPreset() => _presets.FirstOrDefault(p => p.Matches(_parameters));

    public void ResetParameters()
    {
        foreach (var parameter in _parameters)
        {
            parameter.Reset();
        }
    }

    /// <summary>Configures the effect for a stream format and clears its state.</summary>
    public void Prepare(int sampleRate, int channels)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);

        SampleRate = sampleRate;
        Channels = channels;
        OnPrepare();
        _appliedVersion = Volatile.Read(ref _parameterVersion);
        OnParametersChanged();
        OnReset();
    }

    /// <summary>Clears delay lines and filter memories.</summary>
    public void Reset()
    {
        if (IsPrepared)
        {
            OnReset();
        }
    }

    /// <summary>Processes a block of interleaved audio in place.</summary>
    public void Process(Span<float> interleaved)
    {
        if (!IsPrepared || interleaved.IsEmpty)
        {
            return;
        }

        int version = Volatile.Read(ref _parameterVersion);
        if (version != _appliedVersion)
        {
            _appliedVersion = version;
            OnParametersChanged();
        }

        ProcessCore(interleaved);
    }

    protected EffectParameter AddParameter(EffectParameter parameter)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        if (FindParameter(parameter.Id) is not null)
        {
            throw new InvalidOperationException($"Duplicate parameter id '{parameter.Id}'.");
        }

        parameter.ValueChanged += (_, _) => Interlocked.Increment(ref _parameterVersion);
        _parameters.Add(parameter);
        return parameter;
    }

    protected void AddPreset(string name, params (string Id, float Value)[] values) =>
        _presets.Add(new EffectPreset(name, values.ToDictionary(v => v.Id, v => v.Value, StringComparer.OrdinalIgnoreCase)));

    /// <summary>Adds a preset that only changes the listed parameters and leaves the others alone.</summary>
    protected void AddPartialPreset(string name, params (string Id, float Value)[] values) =>
        _presets.Add(new EffectPreset(name, values.ToDictionary(v => v.Id, v => v.Value, StringComparer.OrdinalIgnoreCase), IsPartial: true));

    /// <summary>Allocate buffers for <see cref="SampleRate"/> and <see cref="Channels"/>.</summary>
    protected abstract void OnPrepare();

    /// <summary>Recalculate internal coefficients from the current parameter values.</summary>
    protected abstract void OnParametersChanged();

    /// <summary>Clear all signal history.</summary>
    protected abstract void OnReset();

    protected abstract void ProcessCore(Span<float> interleaved);
}
