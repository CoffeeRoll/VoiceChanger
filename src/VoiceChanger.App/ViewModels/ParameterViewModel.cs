using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoiceChanger.Core.Effects;

namespace VoiceChanger.App.ViewModels;

/// <summary>One option of a choice shown as a row of buttons.</summary>
public sealed class ChoiceOptionViewModel(EffectParameter parameter, int index) : ObservableObject
{
    public string Label => parameter.Choices[index];

    public bool IsSelected
    {
        get => parameter.IntValue == index;
        set
        {
            if (value)
            {
                parameter.Value = index;
            }
        }
    }

    internal void Refresh() => OnPropertyChanged(nameof(IsSelected));
}

/// <summary>Binds one <see cref="EffectParameter"/> to a slider, combo box, row of buttons or switch.</summary>
public sealed partial class ParameterViewModel : ObservableObject
{
    public ParameterViewModel(EffectParameter parameter)
    {
        Parameter = parameter;
        Options = IsButtons
            ? Enumerable.Range(0, parameter.Choices.Count).Select(i => new ChoiceOptionViewModel(parameter, i)).ToList()
            : [];
        parameter.ValueChanged += OnValueChanged;
    }

    public EffectParameter Parameter { get; }

    public string Name => Parameter.Name;

    public string? Description => Parameter.Description;

    public bool IsSlider => Parameter.Kind is ParameterKind.Continuous or ParameterKind.Integer;

    public bool IsChoice => Parameter.Kind == ParameterKind.Choice && !Parameter.ShowAsButtons;

    public bool IsButtons => Parameter.Kind == ParameterKind.Choice && Parameter.ShowAsButtons;

    public bool IsToggle => Parameter.Kind == ParameterKind.Toggle;

    public IReadOnlyList<string> Choices => Parameter.Choices;

    /// <summary>The options of a choice shown as buttons (empty otherwise).</summary>
    public IReadOnlyList<ChoiceOptionViewModel> Options { get; }

    /// <summary>Slider position from 0 to 1 (logarithmic for frequencies).</summary>
    public double Position
    {
        get => Parameter.ToNormalized(Parameter.Value);
        set
        {
            Parameter.Value = Parameter.FromNormalized(value);

            // Always report back so the slider snaps to the rounded value.
            OnPropertyChanged();
        }
    }

    /// <summary>Slider step for keyboard and snapping: one unit for whole-number parameters.</summary>
    public double SmallChange => Parameter.Kind == ParameterKind.Integer
        ? 1.0 / (Parameter.Maximum - Parameter.Minimum)
        : 0.01;

    public int SelectedIndex
    {
        get => Parameter.IntValue;
        set
        {
            if (value >= 0)
            {
                Parameter.Value = value;
            }
        }
    }

    public bool IsOn
    {
        get => Parameter.BoolValue;
        set => Parameter.Value = value ? 1 : 0;
    }

    public string DisplayValue => Parameter.FormattedValue;

    /// <summary>The value <see cref="ResetCommand"/> goes back to, as shown, e.g. "0 cents".</summary>
    public string DefaultText => Parameter.Format(Parameter.DefaultValue);

    /// <summary>Whether a slider is away from its default, so the reset button shows.</summary>
    public bool CanReset => IsSlider && !Parameter.IsDefault;

    /// <summary>The parameter's description, plus how to reset it for sliders.</summary>
    public string? ToolTip => IsSlider
        ? (Description is null ? "" : Description + "\n\n") + $"Double-click, Ctrl+click or press Delete to reset to {DefaultText}."
        : Description;

    /// <summary>False while the setting has no effect (it depends on another one); shown greyed out.</summary>
    public bool IsAvailable => Parameter.IsAvailable;

    /// <summary>Raised on the UI thread after the value changed.</summary>
    public event EventHandler? Changed;

    /// <summary>Re-evaluates <see cref="IsAvailable"/> after another parameter of the effect changed.</summary>
    public void RefreshAvailability() => OnPropertyChanged(nameof(IsAvailable));

    [RelayCommand]
    private void Reset() => Parameter.Reset();

    private void OnValueChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            RaiseValueProperties();
        }
        else
        {
            Dispatcher.UIThread.Post(RaiseValueProperties);
        }
    }

    private void RaiseValueProperties()
    {
        OnPropertyChanged(nameof(Position));
        OnPropertyChanged(nameof(SelectedIndex));
        OnPropertyChanged(nameof(IsOn));
        OnPropertyChanged(nameof(DisplayValue));
        OnPropertyChanged(nameof(CanReset));
        foreach (var option in Options)
        {
            option.Refresh();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
