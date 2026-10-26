using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoiceChanger.Core.Effects;

namespace VoiceChanger.App.ViewModels;

/// <summary>Binds one <see cref="EffectParameter"/> to a slider, combo box or switch.</summary>
public sealed partial class ParameterViewModel : ObservableObject
{
    public ParameterViewModel(EffectParameter parameter)
    {
        Parameter = parameter;
        parameter.ValueChanged += OnValueChanged;
    }

    public EffectParameter Parameter { get; }

    public string Name => Parameter.Name;

    public string? Description => Parameter.Description;

    public bool IsSlider => Parameter.Kind is ParameterKind.Continuous or ParameterKind.Integer;

    public bool IsChoice => Parameter.Kind == ParameterKind.Choice;

    public bool IsToggle => Parameter.Kind == ParameterKind.Toggle;

    public IReadOnlyList<string> Choices => Parameter.Choices;

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
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
