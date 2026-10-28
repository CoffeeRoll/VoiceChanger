using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using VoiceChanger.App.ViewModels;

namespace VoiceChanger.App.Views;

public sealed partial class PluginManagerWindow : Window
{
    public PluginManagerWindow() => InitializeComponent();

    private async void OnAddFolderClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not PluginManagerViewModel manager)
        {
            return;
        }

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "A folder with VST3 plug-ins" });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
        {
            await manager.AddFolderAsync(path);
        }
    }
}
