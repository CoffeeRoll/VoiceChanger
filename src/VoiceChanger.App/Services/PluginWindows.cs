namespace VoiceChanger.App.Services;

/// <summary>Where plug-in windows go: they are owned by the main window, so they stay in front of it.</summary>
public static class PluginWindows
{
    /// <summary>The native handle of the window that owns plug-in windows (0 for none).</summary>
    public static Func<nint> Owner { get; set; } = () => 0;
}
