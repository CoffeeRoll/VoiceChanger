using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VoiceChanger.App.ViewModels;
using VoiceChanger.App.Views;
using VoiceChanger.Core.Dsp;
using VoiceChanger.Core.Settings;
using VoiceChanger.Core.Tests;

namespace VoiceChanger.App.Tests;

public sealed class CollapseTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "VoiceChangerCollapseTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private SettingsStore Store() => new(Path.Combine(_directory, "settings.json"));

    private static (MainWindow Window, MainWindowViewModel ViewModel) Show(MainWindowViewModel viewModel, double height = 820)
    {
        var window = new MainWindow { DataContext = viewModel, Height = height };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, viewModel);
    }

    /// <summary>The card (border) that shows <paramref name="effect"/>.</summary>
    private static Border CardOf(Window window, EffectViewModel effect) =>
        window.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("effect") && b.DataContext == effect);

    [AvaloniaFact]
    public void Collapsing_a_card_hides_its_settings_and_shows_a_summary()
    {
        var (window, vm) = Show(new MainWindowViewModel(new FakeAudioBackend(), Store()));
        var eq = vm.Effects.OfType<EqualizerViewModel>().Single();
        double openHeight = CardOf(window, eq).Bounds.Height;
        Assert.Equal(eq.Description, eq.Subtitle);
        Assert.True(window.GetVisualDescendants().OfType<EqualizerEditor>().Single().IsEffectivelyVisible);

        eq.ToggleCollapsedCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(eq.IsCollapsed);
        Assert.False(window.GetVisualDescendants().OfType<EqualizerEditor>().Single().IsEffectivelyVisible);
        double collapsedHeight = CardOf(window, eq).Bounds.Height;
        Assert.True(collapsedHeight < 100, $"collapsed card is {collapsedHeight} px high");
        Assert.True(openHeight > collapsedHeight * 4, $"open {openHeight} px vs collapsed {collapsedHeight} px");
        Assert.Equal("Low cut 80 Hz · +4.0 dB at 4 kHz · -2.0 dB at 125 Hz", eq.Subtitle);
        Assert.False(eq.ShowPresets);
        Assert.DoesNotContain(CardOf(window, eq).GetVisualDescendants().OfType<ComboBox>(), c => c.IsEffectivelyVisible);

        // The title button on the card toggles it back open.
        var header = CardOf(window, eq).GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("cardHeader"));
        header.Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(eq.IsCollapsed);
        Assert.True(eq.ShowPresets);
        Assert.True(window.GetVisualDescendants().OfType<EqualizerEditor>().Single().IsEffectivelyVisible);

        window.Close();
        vm.Dispose();
    }

    [AvaloniaFact]
    public void Summaries_describe_the_current_settings()
    {
        var (window, vm) = Show(new MainWindowViewModel(new FakeAudioBackend(), Store()));
        var pitch = vm.Effects.OfType<PitchShiftViewModel>().Single();
        var eq = vm.Effects.OfType<EqualizerViewModel>().Single();
        pitch.IsCollapsed = true;
        eq.IsCollapsed = true;

        Assert.Equal("+4 semitones", pitch.Subtitle);

        var changes = new List<string?>();
        pitch.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        pitch.SelectedPreset = pitch.Presets.Single(p => p.Name == "Natural octave up");
        Assert.Equal("+1 octave · formants kept (spectral)", pitch.Subtitle);
        Assert.Contains(nameof(EffectViewModel.Subtitle), changes);

        var effect = (Core.Effects.PitchShiftEffect)pitch.Effect;
        effect.FormantShift.Value = -2.5f;
        effect.Cents.Value = 20;
        effect.Mix.Value = 60;
        Assert.Equal("+1 octave +20 cents · formants -2.5 semitones (spectral) · 60 % mix", pitch.Subtitle);

        effect.Semitones.Value = 0;
        effect.Cents.Value = 0;
        effect.PreserveFormants.Value = 0;
        effect.Mix.Value = 100;
        Assert.Equal("No pitch change", pitch.Subtitle);

        eq.Bands[0].Position = 1;
        Assert.Equal("Low cut 80 Hz · +12.0 dB at 31 Hz · -2.0 dB at 125 Hz", eq.Subtitle);
        eq.SelectedPreset = eq.Presets.Single(p => p.Name == "Flat");
        Assert.Equal("Flat", eq.Subtitle);
        eq.SelectedPreset = eq.Presets.Single(p => p.Name == "Telephone");
        Assert.StartsWith("Low cut 300 Hz · high cut 3.4 kHz · +5.0 dB at 2 kHz", eq.Subtitle);

        window.Close();
        vm.Dispose();
    }

    [AvaloniaFact]
    public void Collapsed_auto_tune_still_shows_the_live_note()
    {
        var backend = new FakeAudioBackend();
        var (window, vm) = Show(new MainWindowViewModel(backend, Store()));
        vm.AddEffectOptions.Single(o => o.Name == "Auto Tune").Command.Execute(null);
        var autoTune = vm.Effects.OfType<AutoTuneViewModel>().Single();

        // First in the chain, so it hears the voice before the pitch shifter raises it.
        autoTune.MoveUpCommand!.Execute(null);
        autoTune.MoveUpCommand!.Execute(null);
        autoTune.IsCollapsed = true;
        Assert.Equal("Chromatic · 20 ms retune", autoTune.Subtitle);

        vm.Start();
        var block = new float[480];
        double phase = 0;
        for (int b = 0; b < 100; b++)
        {
            for (int i = 0; i < block.Length; i++)
            {
                phase += 2 * Math.PI * 452 / 48000;
                block[i] = (float)(0.2 * (Math.Sin(phase) + Math.Sin(2 * phase) / 2 + Math.Sin(3 * phase) / 3));
            }

            backend.Inputs[^1].Push(block);
            backend.Outputs[^1].Pull(480);
            if (b % 3 == 0)
            {
                vm.UpdateStatus();
            }
        }

        Assert.Equal("A4 → A4 · Chromatic · 20 ms retune", autoTune.Subtitle);

        window.Close();
        vm.Dispose();
    }

    [AvaloniaFact]
    public void Collapse_all_toggles_every_card()
    {
        var (window, vm) = Show(new MainWindowViewModel(new FakeAudioBackend(), Store()));
        var button = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "CollapseAllButton");
        Assert.Equal("Collapse all", button.Content);

        vm.ToggleCollapseAllCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.All(vm.Effects, e => Assert.True(e.IsCollapsed));
        Assert.Equal("Expand all", button.Content);

        // Opening a single card offers to collapse everything again.
        vm.Effects[0].IsCollapsed = false;
        Assert.Equal("Collapse all", vm.CollapseAllText);

        vm.ToggleCollapseAllCommand.Execute(null);
        vm.ToggleCollapseAllCommand.Execute(null);
        Assert.All(vm.Effects, e => Assert.False(e.IsCollapsed));

        // New effects arrive open; removing every effect hides the button.
        vm.ToggleCollapseAllCommand.Execute(null);
        vm.AddEffectOptions.Single(o => o.Name == "Equalizer").Command.Execute(null);
        Assert.False(vm.Effects[^1].IsCollapsed);
        Assert.Equal("Collapse all", vm.CollapseAllText);
        foreach (var effect in vm.Effects.ToList())
        {
            effect.RemoveCommand!.Execute(null);
        }

        Dispatcher.UIThread.RunJobs();
        Assert.False(button.IsVisible);

        window.Close();
        vm.Dispose();
    }

    [AvaloniaFact]
    public void Collapsed_cards_are_remembered()
    {
        var store = Store();
        var first = new MainWindowViewModel(new FakeAudioBackend(), store);
        first.Effects.OfType<EqualizerViewModel>().Single().IsCollapsed = true;
        first.Dispose();

        var second = new MainWindowViewModel(new FakeAudioBackend(), store);
        Assert.True(second.Effects.OfType<EqualizerViewModel>().Single().IsCollapsed);
        Assert.False(second.Effects.OfType<PitchShiftViewModel>().Single().IsCollapsed);
        Assert.Contains("\"collapsed\": true", File.ReadAllText(store.FilePath));
        second.Dispose();
    }

    [AvaloniaFact]
    public void Captures_a_screenshot_with_collapsed_cards()
    {
        var backend = new FakeAudioBackend();
        var (window, vm) = Show(new MainWindowViewModel(backend, Store()));
        vm.AddEffectOptions.Single(o => o.Name == "Auto Tune").Command.Execute(null);
        var autoTune = vm.Effects.OfType<AutoTuneViewModel>().Single();
        autoTune.MoveUpCommand!.Execute(null);
        autoTune.MoveUpCommand!.Execute(null);
        autoTune.Parameters.Single(p => p.Parameter.Id == "key").SelectedIndex = 9;
        autoTune.Parameters.Single(p => p.Parameter.Id == "scale").SelectedIndex = (int)ScaleType.NaturalMinor;
        autoTune.SelectedPreset = autoTune.Presets.Single(p => p.Name == "Hard tune");
        autoTune.IsCollapsed = true;
        vm.Effects.OfType<EqualizerViewModel>().Single().IsCollapsed = true;

        vm.Start();
        var block = new float[480];
        double phase = 0, time = 0;
        double[] notes = [57.3, 60 - 0.35, 64.25, 62 + 0.3];
        foreach (double note in notes)
        {
            for (int b = 0; b < 90; b++)
            {
                for (int i = 0; i < block.Length; i++, time += 1.0 / 48000)
                {
                    double frequency = MusicalScale.NoteToFrequency(note + 0.25 * Math.Sin(2 * Math.PI * 5.5 * time));
                    phase += 2 * Math.PI * frequency / 48000;
                    block[i] = (float)(0.2 * (Math.Sin(phase) + Math.Sin(2 * phase) / 2 + Math.Sin(3 * phase) / 3));
                }

                backend.Inputs[^1].Push(block);
                backend.Outputs[^1].Pull(480);
                if (b % 3 == 0)
                {
                    vm.UpdateStatus();
                }
            }
        }

        Dispatcher.UIThread.RunJobs();
        Assert.Equal("D4 → D4 · A minor · instant retune", autoTune.Subtitle);
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        string directory = Path.Combine(FindRepositoryRoot(), "artifacts", "screenshots");
        Directory.CreateDirectory(directory);
        frame.Save(Path.Combine(directory, "collapsed.png"), PngBitmapEncoderOptions.Default);

        window.Close();
        vm.Dispose();
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "VoiceChanger.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? Path.GetTempPath();
    }
}
