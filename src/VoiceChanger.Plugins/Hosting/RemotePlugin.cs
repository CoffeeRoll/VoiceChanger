namespace VoiceChanger.Plugins.Hosting;

/// <summary>A plug-in in a host process of its own.</summary>
internal sealed class RemotePlugin
{
    public static IPluginInstance Start(PluginInfo info, PluginHostCommand command) =>
        throw new PluginException("Not yet.");
}
