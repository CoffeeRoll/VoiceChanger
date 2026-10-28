using Avalonia.Controls;

namespace VoiceChanger.App.Views;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Every slider in the window can be put back to its default the same ways.
        SliderReset.Attach(this);
    }
}
