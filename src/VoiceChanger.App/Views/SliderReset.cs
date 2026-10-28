using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using VoiceChanger.App.ViewModels;

namespace VoiceChanger.App.Views;

/// <summary>
/// Puts a slider back to its default value: double-click it (anywhere on it, not just the thumb),
/// Ctrl+click it, or press Delete or Backspace while it has focus. Parameter sliders go back to
/// their parameter's default; other sliders to their <see cref="DefaultProperty"/>, when set.
/// </summary>
public sealed class SliderReset : AvaloniaObject
{
    /// <summary>The value a slider that isn't bound to a parameter resets to.</summary>
    public static readonly AttachedProperty<double?> DefaultProperty =
        AvaloniaProperty.RegisterAttached<SliderReset, Slider, double?>("Default");

    private SliderReset()
    {
    }

    public static double? GetDefault(Slider slider) => slider.GetValue(DefaultProperty);

    public static void SetDefault(Slider slider, double? value) => slider.SetValue(DefaultProperty, value);

    /// <summary>Handles resets for every slider inside <paramref name="root"/>.</summary>
    public static void Attach(Control root)
    {
        // Tunnelling, so the slider never sees the press: a double-click on the track would
        // otherwise move the value first, and the second click start a drag.
        root.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        root.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>Resets a slider. Returns whether it has a default to go back to.</summary>
    public static bool Reset(Slider slider)
    {
        if (!slider.IsEffectivelyEnabled)
        {
            return false;
        }

        if (slider.DataContext is ParameterViewModel parameter)
        {
            parameter.ResetCommand.Execute(null);
            return true;
        }

        if (GetDefault(slider) is double value)
        {
            slider.Value = value;
            return true;
        }

        return false;
    }

    private static void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is not Control source || source.FindAncestorOfType<Slider>(includeSelf: true) is not { } slider)
        {
            return;
        }

        bool reset = e.ClickCount >= 2 || e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if (reset && e.GetCurrentPoint(slider).Properties.IsLeftButtonPressed && Reset(slider))
        {
            e.Handled = true;
        }
    }

    private static void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Delete or Key.Back && e.KeyModifiers == KeyModifiers.None && e.Source is Slider slider && Reset(slider))
        {
            // Handled, so Delete on a focused slider never deletes the node it belongs to.
            e.Handled = true;
        }
    }
}
