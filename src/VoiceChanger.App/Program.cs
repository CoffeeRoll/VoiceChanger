using Avalonia;
using VoiceChanger.Plugins;
using VoiceChanger.Plugins.Hosting;

namespace VoiceChanger.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // The app scans a plug-in by running itself with this command, so a plug-in that crashes
        // while loading only takes that run down.
        if (args.Length == 2 && args[0] == PluginScanner.Command)
        {
            return PluginScanner.RunCommand(args[1], Console.Out);
        }

        // Each plug-in that runs in a process of its own runs in one of these.
        if (args.Length == 3 && args[0] == PluginHostServer.Command)
        {
            return PluginHostServer.Run(args[1], args[2]);
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Also used by the visual designer.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
