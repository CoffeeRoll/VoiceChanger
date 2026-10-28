using VoiceChanger.Plugins;
using VoiceChanger.Plugins.Hosting;

namespace VoiceChanger.PluginHost;

/// <summary>
/// The host VoiceChanger runs a plug-in in when the plug-in can't run in the app itself, e.g. an x64
/// plug-in next to the ARM64 app. The app runs itself the same way otherwise.
/// </summary>
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 3 && args[0] == PluginHostServer.Command)
        {
            return PluginHostServer.Run(args[1], args[2]);
        }

        if (args.Length == 2 && args[0] == PluginScanner.Command)
        {
            return PluginScanner.RunCommand(args[1], Console.Out);
        }

        Console.Error.WriteLine($"Usage: VoiceChanger.PluginHost {PluginHostServer.Command} <input pipe> <output pipe>");
        Console.Error.WriteLine($"       VoiceChanger.PluginHost {PluginScanner.Command} <plug-in path>");
        return 2;
    }
}
