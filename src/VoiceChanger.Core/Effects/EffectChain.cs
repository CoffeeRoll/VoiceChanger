namespace VoiceChanger.Core.Effects;

/// <summary>
/// An ordered list of effects applied one after the other.
/// <para>
/// The list can be edited from the UI thread while audio is running: the audio thread always works
/// on an immutable snapshot. Switching an effect (or the whole chain) on or off cross-fades over one
/// block so there are no clicks.
/// </para>
/// </summary>
public sealed class EffectChain
{
    /// <summary>Largest block handed to an effect in one call; bigger buffers are split.</summary>
    public const int MaxBlockFrames = 1024;

    private readonly object _gate = new();
    private AudioEffect[] _effects = [];
    private float[] _dry = [];
    private volatile bool _isEnabled = true;
    private int _sampleRate;
    private int _channels;

    public IReadOnlyList<AudioEffect> Effects => Volatile.Read(ref _effects);

    public int Count => Volatile.Read(ref _effects).Length;

    /// <summary>Master switch. When off every effect is bypassed.</summary>
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

    public int SampleRate => _sampleRate;

    public int Channels => _channels;

    public bool IsPrepared => _sampleRate > 0;

    /// <summary>Total delay of the effects that are currently audible, in frames. Allocation free (the graph reads it on the audio thread).</summary>
    public int LatencyFrames
    {
        get
        {
            if (!IsEnabled)
            {
                return 0;
            }

            int total = 0;
            foreach (var effect in Volatile.Read(ref _effects))
            {
                total += effect.IsEnabled ? effect.LatencyFrames : 0;
            }

            return total;
        }
    }

    /// <summary>Raised when effects are added, removed or reordered.</summary>
    public event EventHandler? Changed;

    public event EventHandler? EnabledChanged;

    /// <summary>Prepares every effect for a new stream format. Call only while audio is stopped.</summary>
    public void Prepare(int sampleRate, int channels)
    {
        lock (_gate)
        {
            _sampleRate = sampleRate;
            _channels = channels;
            _dry = new float[MaxBlockFrames * channels];
            foreach (var effect in _effects)
            {
                effect.Prepare(sampleRate, channels);
                effect.WasActive = false;
            }
        }
    }

    public void Add(AudioEffect effect) => Insert(Count, effect);

    public void Insert(int index, AudioEffect effect)
    {
        ArgumentNullException.ThrowIfNull(effect);
        lock (_gate)
        {
            if (Array.IndexOf(_effects, effect) >= 0)
            {
                throw new InvalidOperationException("The effect is already part of the chain.");
            }

            if (IsPrepared)
            {
                effect.Prepare(_sampleRate, _channels);
            }

            effect.WasActive = false;
            var list = _effects.ToList();
            list.Insert(Math.Clamp(index, 0, list.Count), effect);
            Volatile.Write(ref _effects, list.ToArray());
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool Remove(AudioEffect effect)
    {
        lock (_gate)
        {
            if (Array.IndexOf(_effects, effect) < 0)
            {
                return false;
            }

            Volatile.Write(ref _effects, _effects.Where(e => e != effect).ToArray());
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void Move(int fromIndex, int toIndex)
    {
        lock (_gate)
        {
            var list = _effects.ToList();
            if (fromIndex < 0 || fromIndex >= list.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(fromIndex));
            }

            toIndex = Math.Clamp(toIndex, 0, list.Count - 1);
            if (fromIndex == toIndex)
            {
                return;
            }

            var effect = list[fromIndex];
            list.RemoveAt(fromIndex);
            list.Insert(toIndex, effect);
            Volatile.Write(ref _effects, list.ToArray());
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        lock (_gate)
        {
            Volatile.Write(ref _effects, []);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Clears the signal history of every effect. Audio thread only (or while stopped).</summary>
    public void ResetEffects()
    {
        foreach (var effect in Volatile.Read(ref _effects))
        {
            effect.Reset();
        }
    }

    /// <summary>Processes interleaved audio in place. Audio thread only.</summary>
    public void Process(Span<float> interleaved)
    {
        var effects = Volatile.Read(ref _effects);
        int channels = _channels;
        if (effects.Length == 0 || channels == 0)
        {
            return;
        }

        bool chainEnabled = _isEnabled;
        int blockSamples = MaxBlockFrames * channels;
        for (int offset = 0; offset < interleaved.Length; offset += blockSamples)
        {
            var block = interleaved.Slice(offset, Math.Min(blockSamples, interleaved.Length - offset));
            foreach (var effect in effects)
            {
                ProcessEffect(effect, block, chainEnabled, channels);
            }
        }
    }

    private void ProcessEffect(AudioEffect effect, Span<float> block, bool chainEnabled, int channels)
    {
        bool active = chainEnabled && effect.IsEnabled;
        bool wasActive = effect.WasActive;

        if (!active && !wasActive)
        {
            return;
        }

        if (active && wasActive)
        {
            effect.Process(block);
            return;
        }

        // Switching on or off: render both paths and cross-fade between them.
        var dry = _dry.AsSpan(0, block.Length);
        block.CopyTo(dry);
        if (active)
        {
            effect.Reset();
        }

        effect.Process(block);
        effect.WasActive = active;

        int frames = block.Length / channels;
        for (int frame = 0, i = 0; frame < frames; frame++)
        {
            float ramp = (frame + 1f) / frames;
            float wetGain = active ? ramp : 1f - ramp;
            for (int ch = 0; ch < channels; ch++, i++)
            {
                block[i] = dry[i] + (block[i] - dry[i]) * wetGain;
            }
        }
    }
}
