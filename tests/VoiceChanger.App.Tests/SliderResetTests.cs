using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VoiceChanger.App.ViewModels;
using VoiceChanger.App.Views;
using VoiceChanger.Core.Graph;
using VoiceChanger.Core.Settings;
using VoiceChanger.Core.Tests;

namespace VoiceChanger.App.Tests;

public sealed class SliderResetTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "VoiceChangerResetTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private (MainWindow Window, MainWindowViewModel ViewModel) Show()
    {
        var vm = new MainWindowViewModel(new FakeAudioBackend(), new SettingsStore(Path.Combine(_directory, "settings.json")));
        var window = new MainWindow { DataContext = vm, Width = 1300, Height = 1000 };
        window.Show();
        Render(window);
        return (window, vm);
    }

    private static void Render(Window window)
    {
        for (int i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.CaptureRenderedFrame();
        }
    }

    private static Slider SliderFor(Window window, ParameterViewModel parameter) =>
        window.GetVisualDescendants().OfType<ParameterEditor>().Single(e => e.DataContext == parameter)
            .GetVisualDescendants().OfType<Slider>().Single();

    /// <summary>A point on <paramref name="control"/> (fractions of its size), in window coordinates.</summary>
    private static Point At(Window window, Control control, double x = 0.5, double y = 0.5) =>
        control.TranslatePoint(new Point(control.Bounds.Width * x, control.Bounds.Height * y), window)!.Value;

    private static void Click(Window window, Point point, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.MouseDown(point, MouseButton.Left, modifiers);
        window.MouseUp(point, MouseButton.Left, modifiers);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Double_click_anywhere_on_a_slider_resets_it_and_a_single_click_still_moves_it()
    {
        var (window, vm) = Show();
        var pitch = vm.Effects.OfType<PitchShiftViewModel>().Single();
        var cents = pitch.Parameters.Single(p => p.Parameter.Id == "cents");
        var slider = SliderFor(window, cents);

        // One click on the track moves the value there, as before.
        Click(window, At(window, slider, 0.2));
        Assert.InRange(cents.Parameter.Value, -80, -40);

        // Double-clicking the track (the first click moves it, the second resets it)...
        cents.Parameter.Value = 40;
        Render(window);
        var track = At(window, slider, 0.85);
        Click(window, track);
        Click(window, track);
        Assert.Equal(0, cents.Parameter.Value);

        // ... or the thumb.
        cents.Parameter.Value = -55;
        Render(window);
        var thumb = At(window, slider.GetVisualDescendants().OfType<Thumb>().Single());
        Click(window, thumb);
        Click(window, thumb);
        Assert.Equal(0, cents.Parameter.Value);

        window.Close();
        vm.Dispose();
    }

    [AvaloniaFact]
    public void Ctrl_click_and_Delete_or_Backspace_reset_a_slider()
    {
        var (window, vm) = Show();
        var eq = vm.Effects.OfType<EqualizerViewModel>().Single();
        var band = eq.Bands[3];
        band.Parameter.Value = 7;
        Render(window);
        var slider = window.GetVisualDescendants().OfType<Slider>().Single(s => s.DataContext == band);

        Click(window, At(window, slider, 0.5, 0.1), RawInputModifiers.Control);
        Assert.Equal(band.Parameter.DefaultValue, band.Parameter.Value);

        foreach (var key in new[] { Key.Delete, Key.Back })
        {
            band.Parameter.Value = -5;
            slider.Focus();
            window.KeyPress(key, RawInputModifiers.None, key == Key.Delete ? PhysicalKey.Delete : PhysicalKey.Backspace, null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(band.Parameter.DefaultValue, band.Parameter.Value);
        }

        window.Close();
        vm.Dispose();
    }

    [AvaloniaFact]
    public void A_reset_button_shows_only_while_a_slider_is_away_from_its_default()
    {
        var (window, vm) = Show();
        var pitch = vm.Effects.OfType<PitchShiftViewModel>().Single();
        var formantShift = pitch.Parameters.Single(p => p.Parameter.Id == "formantShift");
        var windowSize = pitch.Parameters.Single(p => p.Parameter.Id == "window");
        Button ResetButton(ParameterViewModel parameter) =>
            window.GetVisualDescendants().OfType<ParameterEditor>().Single(e => e.DataContext == parameter)
                .GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("reset"));

        Assert.False(windowSize.CanReset);
        Assert.False(ResetButton(windowSize).IsVisible);
        Assert.Contains("reset to 30 ms", windowSize.ToolTip);

        windowSize.Parameter.Value = 70;
        Render(window);
        Assert.True(ResetButton(windowSize).IsVisible);
        Assert.Equal("Reset to 30 ms", ToolTip.GetTip(ResetButton(windowSize)));
        Click(window, At(window, ResetButton(windowSize)));
        Assert.Equal(30, windowSize.Parameter.Value);
        Render(window);
        Assert.False(ResetButton(windowSize).IsVisible);

        // Choices and switches have no reset button; greyed-out sliders don't reset.
        Assert.False(pitch.Parameters.Single(p => p.Parameter.Id == "formants").CanReset);
        formantShift.Parameter.Value = 3;
        Render(window);
        var greyed = SliderFor(window, formantShift);
        Assert.False(greyed.IsEffectivelyEnabled);
        Click(window, At(window, greyed));
        Click(window, At(window, greyed));
        Assert.Equal(3, formantShift.Parameter.Value);

        window.Close();
        vm.Dispose();
    }

    [AvaloniaFact]
    public void Input_gain_and_volume_reset_to_0_dB()
    {
        var (window, vm) = Show();
        vm.InputGainDb = 6;
        vm.OutputGainDb = -12;
        Render(window);
        Assert.True(vm.IsInputGainChanged);
        Assert.True(vm.IsOutputGainChanged);

        var gain = window.FindControl<Slider>("InputGainSlider")!;
        var point = At(window, gain, 0.9);
        Click(window, point);
        Click(window, point);
        Assert.Equal(0, vm.InputGainDb);
        Assert.False(vm.IsInputGainChanged);

        vm.ResetOutputGainCommand.Execute(null);
        Assert.Equal(0, vm.OutputGainDb);

        window.Close();
        vm.Dispose();
    }

    [AvaloniaFact]
    public void Delete_on_a_slider_in_the_inspector_resets_it_and_keeps_the_node()
    {
        var (window, vm) = Show();
        vm.Mode = EditorMode.Nodes;
        Render(window);
        var editor = vm.NodeEditor;
        var mixer = Assert.IsType<MixerNodeViewModel>(editor.Nodes.Single(n => n.Node is MixerNode));
        editor.SelectedNode = mixer;
        mixer.Channels[0].Gain.Parameter.Value = -9;
        Render(window);

        var inspector = window.GetVisualDescendants().OfType<NodeInspector>().Single();
        var fader = inspector.GetVisualDescendants().OfType<Slider>().First(s => s.DataContext == mixer.Channels[0].Gain);
        var reset = inspector.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("reset") && b.IsVisible);
        Assert.Equal("Reset to 0.0 dB", ToolTip.GetTip(reset));

        fader.Focus();
        window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, mixer.Channels[0].Gain.Parameter.Value);
        Assert.Contains(mixer, editor.Nodes);

        window.Close();
        vm.Dispose();
    }
}
