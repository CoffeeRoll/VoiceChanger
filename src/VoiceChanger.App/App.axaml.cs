using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using VoiceChanger.App.Services;
using VoiceChanger.App.ViewModels;
using VoiceChanger.App.Views;
using VoiceChanger.Core.Settings;
using VoiceChanger.Plugins;

namespace VoiceChanger.App;

public sealed partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Plug-ins found at the last start are there right away, so saved ones load; new ones
            // join the menus once the background scan finds them.
            var plugins = PluginService.Load(PluginCatalog.DefaultCachePath);
            var viewModel = new MainWindowViewModel(AudioBackendFactory.Create(), new SettingsStore(SettingsStore.DefaultPath), plugins.Registry);
            var window = new MainWindow { DataContext = viewModel };
            window.Opened += async (_, _) => viewModel.AddPlugins(await plugins.RescanAsync(PluginCatalog.DefaultFolders));

            // Stop the audio devices and remember the settings however the app ends.
            window.Closed += (_, _) => viewModel.Dispose();
            desktop.Exit += (_, _) => viewModel.Dispose();
            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
