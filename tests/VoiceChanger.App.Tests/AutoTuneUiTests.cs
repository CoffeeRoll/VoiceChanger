using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VoiceChanger.App.Controls;
using VoiceChanger.App.ViewModels;
using VoiceChanger.App.Views;
using VoiceChanger.Core.Dsp;
using VoiceChanger.Core.Effects;
using VoiceChanger.Core.Settings;
using VoiceChanger.Core.Tests;

namespace VoiceChanger.App.Tests;

public sealed class AutoTuneUiTests : IDisposable
{
    private const int SampleRate = 48000;
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "VoiceChangerAutoTuneTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private (MainWindow Window, MainWindowViewModel ViewModel, FakeAudioBackend Backend, AutoTuneViewModel AutoTune) ShowWithAutoTune(double height = 820)
    {
        var backend = new FakeAudioBackend();
        var vm = new MainWindowViewModel(backend, new SettingsStore(Path.Combine(_directory, "settings.json")));
        vm.AddEffectOptions.Single(o => o.Name == "Auto Tune").Command.Execute(null);
        var autoTune = vm.Effects.OfType<AutoTuneViewModel>().Single();

        // Put it first in the chain.
        autoTune.MoveUpCommand!.Execute(null);
        autoTune.MoveUpCommand!.Execute(null);

        var window = new MainWindow { DataContext = vm, Height = height };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, vm, backend, autoTune);
    }

    /// <summary>Plays a voice-like tone through the fake devices, refreshing the UI like the 30 Hz timer would.</summary>
    private static void Sing(MainWindowViewModel vm, FakeAudioBackend backend, Func<double, double> frequencyAt, double seconds, ref double phase, ref double time)
    {
        var block = new float[480];
        int blocks = (int)(seconds * 100);
        for (int b = 0; b < blocks; b++)
        {
            for (int i = 0; i < block.Length; i++, time += 1.0 / SampleRate)
            {
                double frequency = frequencyAt(time);
                if (frequency <= 0)
                {
                    block[i] = 0; // a rest
                    continue;
                }

                phase += 2 * Math.PI * frequency / SampleRate;
                double value = 0;
                for (int h = 1; h <= 8; h++)
                {
                    value += Math.Sin(h * phase) / h;
                }

                block[i] = (float)(0.2 * value);
            }

            backend.Inputs[^1].Push(block);
            backend.Outputs[^1].Pull(480);
            if (b % 3 == 0)
            {
                vm.UpdateStatus();
            }
        }

        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Shows_the_sung_pitch_and_the_note_it_is_corrected_to()
    {
        var (window, vm, backend, autoTune) = ShowWithAutoTune();
        Assert.Single(window.GetVisualDescendants().OfType<AutoTuneEditor>());
        Assert.Single(window.GetVisualDescendants().OfType<PitchHistoryView>());
        Assert.Single(window.GetVisualDescendants().OfType<TunerMeter>());
        Assert.Equal("–", autoTune.InputNote);
        Assert.Contains("Start", autoTune.InputDetail);

        vm.Start();
        double phase = 0, time = 0;
        Sing(vm, backend, _ => 452, 1.0, ref phase, ref time); // A4, 47 cents sharp

        Assert.True(autoTune.HasPitch);
        Assert.Equal("A4", autoTune.InputNote);
        Assert.Equal("A4", autoTune.OutputNote);
        Assert.StartsWith("45", autoTune.InputDetail);       // "452.x Hz · +47 cents"
        Assert.Contains("+4", autoTune.InputDetail);
        Assert.StartsWith("44", autoTune.OutputDetail);      // "440.x Hz"
        Assert.StartsWith("-4", autoTune.CorrectionText);    // pulled down ~47 cents
        Assert.InRange(autoTune.TunerCents, 40, 50);
        Assert.True(autoTune.Notes[9].IsTarget);             // A
        Assert.True(autoTune.Notes[9].IsInput);
        Assert.Contains(autoTune.InputHistory, double.IsFinite);
        Assert.Contains(autoTune.OutputHistory, double.IsFinite);

        // The labels in the window show the same thing.
        Assert.Equal("A4", window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "InputNoteText").Text);

        vm.Stop();
        Assert.False(autoTune.HasPitch);
        Assert.Equal("–", autoTune.OutputNote);
        Assert.DoesNotContain(autoTune.OutputHistory, double.IsFinite);

        window.Close();
        vm.Dispose();
    }

    [AvaloniaFact]
    public void Explains_when_there_is_nothing_to_show()
    {
        var (window, vm, backend, autoTune) = ShowWithAutoTune();
        vm.Start();
        double phase = 0, time = 0;

        Sing(vm, backend, _ => 0, 0.3, ref phase, ref time); // silence
        Assert.False(autoTune.HasPitch);
        Assert.Contains("Sing or speak", autoTune.InputDetail);

        autoTune.IsEnabled = false;
        Sing(vm, backend, _ => 330, 0.3, ref phase, ref time);
        Assert.False(autoTune.HasPitch);
        Assert.Contains("Turn the effect on", autoTune.InputDetail);

        window.Close();
        vm.Dispose();
    }

    [AvaloniaFact]
    public void Key_and_scale_update_the_allowed_notes()
    {
        var (window, vm, _, autoTune) = ShowWithAutoTune();
        Assert.Equal("Chromatic (all notes)", autoTune.ScaleName);
        Assert.All(autoTune.Notes, n => Assert.True(n.IsInScale));
        Assert.Equal(-1, autoTune.RootPitchClass);

        autoTune.Parameters.Single(p => p.Parameter.Id == "key").SelectedIndex = 9;
        autoTune.Parameters.Single(p => p.Parameter.Id == "scale").SelectedIndex = (int)ScaleType.NaturalMinor;

        Assert.Equal("A minor", autoTune.ScaleName);
        Assert.Equal(9, autoTune.RootPitchClass);
        Assert.Equal(["C", "D", "E", "F", "G", "A", "B"], autoTune.Notes.Where(n => n.IsInScale).Select(n => n.Name));

        // Picking a style keeps the key and scale and stays selected.
        autoTune.SelectedPreset = autoTune.Presets.Single(p => p.Name == "Hard tune");
        Assert.Equal("A minor", autoTune.ScaleName);
        autoTune.Parameters.Single(p => p.Parameter.Id == "key").SelectedIndex = 0;
        Assert.Equal("Hard tune", autoTune.SelectedPreset?.Name);

        window.Close();
        vm.Dispose();
    }

    [AvaloniaFact]
    public void Auto_tune_settings_are_saved_and_restored()
    {
        var store = new SettingsStore(Path.Combine(_directory, "settings.json"));
        var first = new MainWindowViewModel(new FakeAudioBackend(), store);
        first.AddEffectOptions.Single(o => o.Name == "Auto Tune").Command.Execute(null);
        var effect = (AutoTuneEffect)first.Effects.OfType<AutoTuneViewModel>().Single().Effect;
        effect.Key.Value = 7;
        effect.Scale.Value = (int)ScaleType.Major;
        effect.RetuneSpeed.Value = 0;
        first.Dispose();

        var second = new MainWindowViewModel(new FakeAudioBackend(), store);
        var restored = (AutoTuneEffect)second.Effects.OfType<AutoTuneViewModel>().Single().Effect;
        Assert.Equal(7, restored.Key.Value);
        Assert.Equal((int)ScaleType.Major, restored.Scale.Value);
        Assert.Equal(0, restored.RetuneSpeed.Value);
        second.Dispose();
    }

    [AvaloniaFact]
    public void Captures_a_screenshot_of_auto_tune_in_action()
    {
        var (window, vm, backend, autoTune) = ShowWithAutoTune(height: 1200);
        autoTune.Parameters.Single(p => p.Parameter.Id == "key").SelectedIndex = 9;
        autoTune.Parameters.Single(p => p.Parameter.Id == "scale").SelectedIndex = (int)ScaleType.NaturalMinor;
        autoTune.SelectedPreset = autoTune.Presets.Single(p => p.Name == "Hard tune");
        vm.Start();

        // A slightly out-of-tune phrase in A minor with vibrato and slides: A3 C4 E4 D4 C4.
        double[] notes = [57.3, 60 - 0.35, 64.25, 62 + 0.3, 60.4];
        double phase = 0, time = 0;
        foreach (double note in notes)
        {
            double start = time;
            Sing(vm, backend, t =>
            {
                double vibrato = 0.25 * Math.Sin(2 * Math.PI * 5.5 * (t - start)) * Math.Min(1, (t - start) / 0.25);
                return MusicalScale.NoteToFrequency(note + vibrato);
            }, 0.75, ref phase, ref time);
            Sing(vm, backend, _ => 0, 0.15, ref phase, ref time);
        }

        Sing(vm, backend, t => MusicalScale.NoteToFrequency(57.4 + 0.2 * Math.Sin(2 * Math.PI * 5 * t)), 0.3, ref phase, ref time);
        Assert.True(autoTune.HasPitch);

        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        string directory = Path.Combine(FindRepositoryRoot(), "artifacts", "screenshots");
        Directory.CreateDirectory(directory);
        frame.Save(Path.Combine(directory, "auto-tune.png"), PngBitmapEncoderOptions.Default);

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
