using Avalonia.Controls;

namespace VoiceChanger.App.Views;

public sealed partial class EqualizerEditor : UserControl
{
    public EqualizerEditor()
    {
        InitializeComponent();
        SliderReset.Attach(this);
    }
}
