using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VoiceChanger.App.Controls;
using VoiceChanger.App.ViewModels;
using VoiceChanger.App.Views;
using VoiceChanger.Core.Audio;
using VoiceChanger.Core.Effects;
using VoiceChanger.Core.Settings;
using VoiceChanger.Core.Tests;

namespace VoiceChanger.App.Tests;

public sealed class MainWindowTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "VoiceChangerUiTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private SettingsStore Store() => new(Path.Combine(_directory, "settings.json"));

    private static (MainWindow Window, MainWindowViewModel ViewModel) Show(MainWindowViewModel viewModel)
    {
        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, viewModel);
    }

    [AvaloniaFact]
    public void First_run_shows_devices_and_the_default_effect_chain()
    {
        var backend = new FakeAudioBackend();
        var (window, vm) = Show(new MainWindowViewModel(backend, Store()));

        Assert.Equal("mic", vm.SelectedInput?.Id);
        Assert.Equal("speakers", vm.SelectedOutput?.Id);
        Assert.Equal(["Pitch Shifter", "Equalizer"], vm.Effects.Select(e => e.Name));
        Assert.Equal(["Higher voice", "Voice clarity"], vm.Effects.Select(e => e.SelectedPreset?.Name));
        Assert.IsType<EqualizerViewModel>(vm.Effects[1]);

        // Both editors are actually materialised in the visual tree.
        Assert.Single(window.GetVisualDescendants().OfType<EqualizerEditor>());
        Assert.Single(window.GetVisualDescendants().OfType<PitchShiftEditor>());
        Assert.Equal(2, window.GetVisualDescendants().OfType<LevelMeter>().Count());
        Assert.Single(window.GetVisualDescendants().OfType<FrequencyResponseView>());

        var inputBox = window.FindControl<ComboBox>("InputDeviceBox")!;
        Assert.Equal(2, inputBox.ItemCount);
        Assert.Equal(vm.SelectedInput, inputBox.SelectedItem);

        window.Close();
        vm.Dispose();
    }

    [AvaloniaFact]
    public void Start_runs_audio_through_the_chain_and_stop_releases_the_devices()
    {
        var backend = new FakeAudioBackend { InputFormat = new AudioFormat(48000, 1), OutputFormat = new AudioFormat(48000, 2) };
        var (window, vm) = Show(new MainWindowViewModel(backend, Store()));

        vm.ToggleRunningCommand.Execute(null);
        Assert.True(vm.IsRunning);
        Assert.Equal("Stop", vm.StartStopText);
        Assert.Null(vm.ErrorMessage);

        var input = Assert.Single(backend.Inputs);
        var output = Assert.Single(backend.Outputs);
        var tone = new float[480];
        for (int block = 0; block < 20; block++)
        {
            for (int i = 0; i < tone.Length; i++)
            {
                tone[i] = 0.5f * MathF.Sin(2 * MathF.PI * 220 * (block * 480 + i) / 48000f);
            }

            input.Push(tone);
            output.Pull(480);
        }

        // Let the status timer tick.
        Thread.Sleep(80);
        Dispatcher.UIThread.RunJobs();
        Assert.StartsWith("Running", vm.StatusText);
        Assert.True(vm.InputLevelDb > -20, $"input level {vm.InputLevelDb}");
        Assert.True(vm.OutputLevelDb > -40, $"output level {vm.OutputLevelDb}");
        Assert.NotEqual("–", vm.LatencyText);

        vm.ToggleRunningCommand.Execute(null);
        Assert.False(vm.IsRunning);
        Assert.True(input.IsDisposed);
        Assert.True(output.IsDisposed);

        window.Close();
        vm.Dispose();
    }

    [AvaloniaFact]
    public void Changing_devices_while_running_restarts_on_the_new_devices()
    {
        var backend = new FakeAudioBackend();
        var (window, vm) = Show(new MainWindowViewModel(backend, Store()));
        vm.Start();

        vm.SelectedOutput = vm.OutputDevices.Single(d => d.Id == "cable");

        Assert.True(vm.IsRunning);
        Assert.Equal(2, backend.Outputs.Count);
        Assert.True(backend.Outputs[0].IsDisposed);
        Assert.Equal("cable", backend.Outputs[1].DeviceId);

        window.Close();
        vm.Dispose();
    }

    [AvaloniaFact]
    public void Refreshing_after_a_device_disappeared_moves_audio_to_another_device()
    {
        var backend = new FakeAudioBackend();
        var (window, vm) = Show(new MainWindowViewModel(backend, Store()));
        vm.Start();

        backend.OutputDevices.RemoveAll(d => d.Id == "speakers");
        vm.RefreshDevicesCommand.Execute(null);

        Assert.True(vm.IsRunning);
        Assert.Equal("cable", vm.SelectedOutput?.Id);
        Assert.Equal("cable", backend.Outputs[^1].DeviceId);

        // Nothing left to play on: stop instead of pretending to run.
        backend.OutputDevices.Clear();
        vm.RefreshDevicesCommand.Execute(null);
        Assert.False(vm.IsRunning);
        Assert.True(vm.HasError);

        window.Close();
        vm.Dispose();
    }

    [AvaloniaFact]
    public void Effects_can_be_added_reordered_toggled_and_removed()
    {
        var (window, vm) = Show(new MainWindowViewModel(new FakeAudioBackend(), Store()));

        vm.AddEffectOptions.Single(o => o.Name == "Equalizer").Command.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(3, vm.Effects.Count);
        Assert.Equal(2, window.GetVisualDescendants().OfType<EqualizerEditor>().Count());
        Assert.Equal([1, 2, 3], vm.Effects.Select(e => e.Position));
        Assert.False(vm.Effects[0].CanMoveUp);
        Assert.False(vm.Effects[2].CanMoveDown);

        var pitch = vm.Effects[0];
        pitch.MoveDownCommand!.Execute(null);
        Assert.Same(pitch, vm.Effects[1]);

        pitch.IsEnabled = false;
        Assert.False(pitch.Effect.IsEnabled);

        pitch.RemoveCommand!.Execute(null);
        Assert.DoesNotContain(pitch, vm.Effects);

        vm.EffectsEnabled = false;

        window.Close();
        vm.Dispose();
    }

    [AvaloniaFact]
    public void Presets_apply_and_manual_changes_clear_the_selection()
    {
        var (window, vm) = Show(new MainWindowViewModel(new FakeAudioBackend(), Store()));
        var pitch = vm.Effects.Single(e => e.Effect is PitchShiftEffect);
        var semitones = pitch.Parameters.Single(p => p.Parameter.Id == "semitones");

        pitch.SelectedPreset = pitch.Presets.Single(p => p.Name == "Chipmunk");
        Assert.Equal(12, semitones.Parameter.Value);
        Assert.Equal("+1 octave", semitones.DisplayValue);
        Assert.NotNull(pitch.SelectedPreset);

        // Integer parameters snap: halfway between -24 and +24 is 0.
        semitones.Position = 0.51;
        Assert.Equal(0, semitones.Parameter.Value);
        Assert.Equal(0.5, semitones.Position, 6);
        Assert.Null(pitch.SelectedPreset);

        pitch.ResetParametersCommand.Execute(null);
        Assert.True(pitch.Parameters.All(p => p.Parameter.IsDefault));
        Assert.Equal("Natural", pitch.SelectedPreset?.Name);

        window.Close();
        vm.Dispose();
    }

    [AvaloniaFact]
    public void Octave_buttons_shift_by_whole_octaves()
    {
        var (window, vm) = Show(new MainWindowViewModel(new FakeAudioBackend(), Store()));
        var pitch = vm.Effects.OfType<PitchShiftViewModel>().Single();
        var semitones = pitch.Parameters.Single(p => p.Parameter.Id == "semitones");

        // The buttons are on screen, from two octaves down to two octaves up.
        var buttons = window.GetVisualDescendants().OfType<ItemsControl>().Single(c => c.Name == "OctaveButtons");
        Assert.Equal(5, buttons.ItemCount);
        Assert.Equal(["-2", "-1", "0", "+1", "+2"], pitch.Octaves.Select(o => o.Label));

        pitch.Octaves.Single(o => o.Octaves == 1).SelectCommand.Execute(null);
        Assert.Equal(12, semitones.Parameter.Value);
        Assert.Equal("+1 octave", semitones.DisplayValue);
        Assert.Equal([1], pitch.Octaves.Where(o => o.IsSelected).Select(o => o.Octaves));

        pitch.Octaves.Single(o => o.Octaves == -2).SelectCommand.Execute(null);
        Assert.Equal(-24, semitones.Parameter.Value);
        Assert.Equal("-2 octaves", semitones.DisplayValue);
        Assert.Equal([-2], pitch.Octaves.Where(o => o.IsSelected).Select(o => o.Octaves));

        // In between whole octaves no button is lit and the label spells out the interval.
        semitones.Parameter.Value = 15;
        Assert.DoesNotContain(pitch.Octaves, o => o.IsSelected);
        Assert.Equal("+1 octave +3 semitones", semitones.DisplayValue);

        // Presets light up the matching octave.
        pitch.SelectedPreset = pitch.Presets.Single(p => p.Name == "Monster");
        Assert.Equal([-1], pitch.Octaves.Where(o => o.IsSelected).Select(o => o.Octaves));

        window.Close();
        vm.Dispose();
    }

    [AvaloniaFact]
    public void Equalizer_curve_follows_the_band_sliders()
    {
        var (window, vm) = Show(new MainWindowViewModel(new FakeAudioBackend(), Store()));
        var eq = vm.Effects.OfType<EqualizerViewModel>().Single();
        eq.ResetParametersCommand.Execute(null);
        Assert.All(eq.Response, db => Assert.Equal(0, db, 6));

        eq.Bands[5].Position = 1; // +12 dB at 1 kHz
        int index = Array.FindIndex(eq.ResponseFrequencies, f => f >= 1000);
        Assert.InRange(eq.Response[index], 10, 12.5);
        Assert.Equal(10, eq.Bands.Count);
        Assert.Equal(4, eq.Filters.Count);

        window.Close();
        vm.Dispose();
    }

    [AvaloniaFact]
    public void Settings_are_saved_on_exit_and_restored_next_time()
    {
        var store = Store();
        var first = new MainWindowViewModel(new FakeAudioBackend(), store);
        first.SelectedInput = first.InputDevices.Single(d => d.Id == "line");
        first.SelectedLatency = first.LatencyOptions.Single(o => o.Preset == LatencyPreset.Safe);
        first.EffectsEnabled = false;
        first.OutputGainDb = -6;
        first.Effects[0].Parameters.Single(p => p.Parameter.Id == "semitones").Parameter.Value = -9;
        first.Effects[1].RemoveCommand!.Execute(null);
        first.Dispose();

        var second = new MainWindowViewModel(new FakeAudioBackend(), store);
        Assert.Equal("line", second.SelectedInput?.Id);
        Assert.Equal(LatencyPreset.Safe, second.SelectedLatency.Preset);
        Assert.False(second.EffectsEnabled);
        Assert.Equal(-6, second.OutputGainDb);
        var effect = Assert.Single(second.Effects);
        Assert.Equal(-9, effect.Parameters.Single(p => p.Parameter.Id == "semitones").Parameter.Value);
        second.Dispose();
    }

    [AvaloniaFact]
    public void Device_failure_is_reported_and_stops_the_engine()
    {
        var backend = new FakeAudioBackend();
        var (window, vm) = Show(new MainWindowViewModel(backend, Store()));
        vm.Start();

        backend.Inputs[0].Fail(new IOException("Device unplugged."));
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (vm.IsRunning && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(10);
            Dispatcher.UIThread.RunJobs();
        }

        Assert.False(vm.IsRunning);
        Assert.True(vm.HasError);
        Assert.Contains("Device unplugged.", vm.ErrorMessage);

        vm.DismissErrorCommand.Execute(null);
        Assert.False(vm.HasError);

        window.Close();
        vm.Dispose();
    }

    [AvaloniaFact]
    public void Start_failure_is_shown_instead_of_crashing()
    {
        var backend = new FakeAudioBackend { OpenOutputError = new InvalidOperationException("Device in use.") };
        var (window, vm) = Show(new MainWindowViewModel(backend, Store()));

        vm.ToggleRunningCommand.Execute(null);
        Assert.False(vm.IsRunning);
        Assert.Contains("Device in use.", vm.ErrorMessage);

        window.Close();
        vm.Dispose();
    }

    [AvaloniaFact]
    public void Unsupported_platform_disables_start()
    {
        var (window, vm) = Show(new MainWindowViewModel(new NullAudioBackend(), Store()));
        Assert.False(vm.IsBackendSupported);
        Assert.Empty(vm.InputDevices);
        Assert.False(window.FindControl<Button>("StartStopButton")!.IsEnabled);
        Assert.Contains("Windows", vm.StatusText);

        window.Close();
        vm.Dispose();
    }

    [AvaloniaFact]
    public void Captures_screenshots_of_the_main_window()
    {
        var backend = new FakeAudioBackend();
        var (window, vm) = Show(new MainWindowViewModel(backend, Store()));
        vm.Start();

        // Feed a little audio so the meters show something.
        var tone = Enumerable.Range(0, 480).Select(i => 0.3f * MathF.Sin(i * 0.05f)).ToArray();
        for (int i = 0; i < 10; i++)
        {
            backend.Inputs[0].Push(tone);
            backend.Outputs[0].Pull(480);
        }

        Thread.Sleep(80);
        Dispatcher.UIThread.RunJobs();

        string directory = ScreenshotDirectory();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(directory, "main-window.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        Assert.True(frame.PixelSize.Width >= 1120);

        // A taller window shows both effect editors completely (used in the README).
        window.Height = 1240;
        Dispatcher.UIThread.RunJobs();
        var full = window.CaptureRenderedFrame();
        Assert.NotNull(full);
        full.Save(Path.Combine(directory, "main-window-full.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);

        window.Close();
        vm.Dispose();
    }

    /// <summary>artifacts/screenshots at the repository root (falls back to the temp folder).</summary>
    private static string ScreenshotDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "VoiceChanger.sln")))
        {
            directory = directory.Parent;
        }

        string path = Path.Combine(directory?.FullName ?? Path.GetTempPath(), "artifacts", "screenshots");
        Directory.CreateDirectory(path);
        return path;
    }
}
