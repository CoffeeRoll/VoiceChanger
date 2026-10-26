using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using VoiceChanger.App.Services;
using VoiceChanger.App.ViewModels;
using VoiceChanger.App.Views;
using VoiceChanger.Core.Settings;

namespace VoiceChanger.App;

public sealed partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var viewModel = new MainWindowViewModel(AudioBackendFactory.Create(), new SettingsStore(SettingsStore.DefaultPath));
            var window = new MainWindow { DataContext = viewModel };

            // Stop the audio devices and remember the settings however the app ends.
            window.Closed += (_, _) => viewModel.Dispose();
            desktop.Exit += (_, _) => viewModel.Dispose();
            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
