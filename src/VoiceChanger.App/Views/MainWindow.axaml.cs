using Avalonia.Controls;
using Avalonia.Interactivity;
using VoiceChanger.App.ViewModels;

namespace VoiceChanger.App.Views;

public sealed partial class MainWindow : Window
{
    private PluginManagerWindow? _pluginManager;

    public MainWindow()
    {
        InitializeComponent();

        // Every slider in the window can be put back to its default the same ways.
        SliderReset.Attach(this);
    }

    /// <summary>Opens the plug-in manager (or brings it to the front).</summary>
    public PluginManagerWindow? ShowPluginManager()
    {
        if (DataContext is not MainWindowViewModel { PluginManager: { } manager })
        {
            return null;
        }

        if (_pluginManager is null)
        {
            _pluginManager = new PluginManagerWindow { DataContext = manager };
            _pluginManager.Closed += (_, _) => _pluginManager = null;
            _pluginManager.Show(this);
        }
        else
        {
            _pluginManager.Activate();
        }

        return _pluginManager;
    }

    private void OnPluginManagerClick(object? sender, RoutedEventArgs e) => ShowPluginManager();
}
