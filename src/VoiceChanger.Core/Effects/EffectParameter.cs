using System.Globalization;

namespace VoiceChanger.Core.Effects;

public enum ParameterKind
{
    /// <summary>A real number between <see cref="EffectParameter.Minimum"/> and <see cref="EffectParameter.Maximum"/>.</summary>
    Continuous,

    /// <summary>A whole number between <see cref="EffectParameter.Minimum"/> and <see cref="EffectParameter.Maximum"/>.</summary>
    Integer,

    /// <summary>An index into <see cref="EffectParameter.Choices"/>.</summary>
    Choice,

    /// <summary>On (1) or off (0).</summary>
    Toggle,
}

public enum ParameterScale
{
    Linear,

    /// <summary>Sliders should move geometrically (e.g. frequencies).</summary>
    Logarithmic,
}

/// <summary>
/// A user adjustable effect setting. Values are written by the UI thread and read lock-free by the
/// audio thread, so every read observes a complete value.
/// </summary>
public sealed class EffectParameter
{
    private readonly Func<float, string>? _formatter;
    private Func<bool>? _availableWhen;
    private float _value;

    private EffectParameter(
        string id,
        string name,
        ParameterKind kind,
        float minimum,
        float maximum,
        float defaultValue,
        string? unit,
        int decimals,
        ParameterScale scale,
        IReadOnlyList<string>? choices,
        Func<float, string>? formatter,
        string? description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!(maximum > minimum))
        {
            throw new ArgumentException("Maximum must be greater than minimum.");
        }

        if (scale == ParameterScale.Logarithmic && minimum <= 0)
        {
            throw new ArgumentException("Logarithmic parameters need a positive range.");
        }

        Id = id;
        Name = name;
        Kind = kind;
        Minimum = minimum;
        Maximum = maximum;
        Unit = unit;
        Decimals = decimals;
        Scale = scale;
        Choices = choices ?? [];
        Description = description;
        _formatter = formatter;
        DefaultValue = Coerce(defaultValue);
        _value = DefaultValue;
    }

    public string Id { get; }

    public string Name { get; }

    public string? Description { get; }

    public ParameterKind Kind { get; }

    public ParameterScale Scale { get; }

    public float Minimum { get; }

    public float Maximum { get; }

    public float DefaultValue { get; }

    public string? Unit { get; }

    /// <summary>Number of decimals values are rounded to and displayed with.</summary>
    public int Decimals { get; }

    public IReadOnlyList<string> Choices { get; }

    public float Value
    {
        get => Volatile.Read(ref _value);
        set
        {
            float coerced = Coerce(value);
            if (coerced.Equals(Volatile.Read(ref _value)))
            {
                return;
            }

            Volatile.Write(ref _value, coerced);
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool BoolValue => Value >= 0.5f;

    public int IntValue => (int)MathF.Round(Value);

    public bool IsDefault => Value.Equals(DefaultValue);

    /// <summary>
    /// Whether the parameter currently has any effect. Some settings only matter while another one is
    /// switched on; the UI greys them out otherwise.
    /// </summary>
    public bool IsAvailable => _availableWhen?.Invoke() ?? true;

    /// <summary>For a choice: show the options side by side as buttons rather than in a list.</summary>
    public bool ShowAsButtons { get; private set; }

    /// <summary>Raised on the thread that changed the value.</summary>
    public event EventHandler? ValueChanged;

    public static EffectParameter Continuous(
        string id,
        string name,
        float minimum,
        float maximum,
        float defaultValue,
        string? unit = null,
        int decimals = 1,
        ParameterScale scale = ParameterScale.Linear,
        Func<float, string>? formatter = null,
        string? description = null) =>
        new(id, name, ParameterKind.Continuous, minimum, maximum, defaultValue, unit, decimals, scale, null, formatter, description);

    public static EffectParameter Integer(
        string id,
        string name,
        int minimum,
        int maximum,
        int defaultValue,
        string? unit = null,
        Func<float, string>? formatter = null,
        string? description = null) =>
        new(id, name, ParameterKind.Integer, minimum, maximum, defaultValue, unit, 0, ParameterScale.Linear, null, formatter, description);

    public static EffectParameter Choice(
        string id,
        string name,
        IReadOnlyList<string> choices,
        int defaultIndex = 0,
        string? description = null)
    {
        ArgumentNullException.ThrowIfNull(choices);
        if (choices.Count < 2)
        {
            throw new ArgumentException("A choice parameter needs at least two options.", nameof(choices));
        }

        return new(id, name, ParameterKind.Choice, 0, choices.Count - 1, defaultIndex, null, 0, ParameterScale.Linear, choices, null, description);
    }

    public static EffectParameter Toggle(string id, string name, bool defaultValue, string? description = null) =>
        new(id, name, ParameterKind.Toggle, 0, 1, defaultValue ? 1 : 0, null, 0, ParameterScale.Linear, null, null, description);

    public void Reset() => Value = DefaultValue;

    /// <summary>Marks the parameter as only relevant while <paramref name="condition"/> holds.</summary>
    public EffectParameter AvailableWhen(Func<bool> condition)
    {
        _availableWhen = condition ?? throw new ArgumentNullException(nameof(condition));
        return this;
    }

    /// <summary>Shows a choice as a row of buttons, for a few short options.</summary>
    public EffectParameter AsButtons()
    {
        if (Kind != ParameterKind.Choice)
        {
            throw new InvalidOperationException("Only choices can be shown as buttons.");
        }

        ShowAsButtons = true;
        return this;
    }

    /// <summary>Clamps and rounds a value to what this parameter can hold.</summary>
    public float Coerce(float value)
    {
        if (float.IsNaN(value))
        {
            return DefaultValue;
        }

        value = Math.Clamp(value, Minimum, Maximum);
        return Kind switch
        {
            ParameterKind.Toggle => value >= 0.5f ? 1f : 0f,
            ParameterKind.Integer or ParameterKind.Choice => MathF.Round(value),
            _ => MathF.Round(value, Decimals, MidpointRounding.AwayFromZero),
        };
    }

    /// <summary>Maps a value to a 0..1 slider position, honouring <see cref="Scale"/>.</summary>
    public double ToNormalized(float value)
    {
        value = Math.Clamp(value, Minimum, Maximum);
        return Scale == ParameterScale.Logarithmic
            ? Math.Log(value / Minimum) / Math.Log(Maximum / Minimum)
            : (value - Minimum) / (Maximum - Minimum);
    }

    /// <summary>Maps a 0..1 slider position back to a value, honouring <see cref="Scale"/>.</summary>
    public float FromNormalized(double position)
    {
        position = Math.Clamp(position, 0, 1);
        double value = Scale == ParameterScale.Logarithmic
            ? Minimum * Math.Pow(Maximum / Minimum, position)
            : Minimum + position * (Maximum - Minimum);
        return Coerce((float)value);
    }

    public string Format(float value)
    {
        if (_formatter is not null)
        {
            return _formatter(value);
        }

        switch (Kind)
        {
            case ParameterKind.Choice:
                int index = Math.Clamp((int)MathF.Round(value), 0, Choices.Count - 1);
                return Choices[index];
            case ParameterKind.Toggle:
                return value >= 0.5f ? "On" : "Off";
        }

        string number = value.ToString("F" + Decimals, CultureInfo.CurrentCulture);
        return string.IsNullOrEmpty(Unit) ? number : $"{number} {Unit}";
    }

    public string FormattedValue => Format(Value);

    public override string ToString() => $"{Name}: {FormattedValue}";
}
