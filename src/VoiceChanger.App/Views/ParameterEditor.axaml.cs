using Avalonia.Controls;

namespace VoiceChanger.App.Views;

public sealed partial class ParameterEditor : UserControl
{
    public ParameterEditor()
    {
        InitializeComponent();
        SliderReset.Attach(this);
    }
}
