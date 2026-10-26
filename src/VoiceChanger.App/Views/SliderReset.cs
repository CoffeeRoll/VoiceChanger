using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using VoiceChanger.App.ViewModels;

namespace VoiceChanger.App.Views;

/// <summary>Double-clicking a parameter slider resets it to its default value.</summary>
internal static class SliderReset
{
    public static void Attach(Control root) =>
        root.AddHandler(InputElement.DoubleTappedEvent, OnDoubleTapped, RoutingStrategies.Bubble, handledEventsToo: true);

    private static void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Control source &&
            source.FindAncestorOfType<Slider>(includeSelf: true) is { DataContext: ParameterViewModel parameter })
        {
            parameter.ResetCommand.Execute(null);
            e.Handled = true;
        }
    }
}
