using Avalonia.Controls;

namespace VoiceChanger.App.Views;

public sealed partial class AutoTuneEditor : UserControl
{
    public AutoTuneEditor()
    {
        InitializeComponent();
        SliderReset.Attach(this);
    }
}
