using System.Runtime.InteropServices;
using VoiceChanger.Plugins.Hosting;

namespace VoiceChanger.Plugins.Tests;

/// <summary>The plug-in's own window (Windows only: the test plug-in's editor is a Win32 window).</summary>
public sealed partial class PluginEditorTests
{
    private const uint WmClose = 0x0010;
    private const uint WmLButtonDown = 0x0201;

    [Fact]
    public void The_window_follows_the_plugins_size_and_reports_its_edits()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Plug-in windows need Windows.");
        using var effect = TestPlugin.Open();
        Assert.True(effect.HasEditor);

        Assert.Null(effect.OpenEditor(0));
        Assert.True(effect.IsEditorOpen);
        nint window = ((LocalPlugin)effect.Instance).EditorWindow;
        nint view = FindWindowExW(window, 0, "VoiceChangerTestEditor", null);
        Assert.NotEqual(0, view);
        Assert.Equal((400, 300), ClientSize(window));
        Assert.Equal((400, 300), ClientSize(view));

        // It was told the monitor's scale (the test plug-in shows it as a quarter).
        Assert.Equal(GetDpiForWindow(window) / 96.0 / 4, effect.GetNormalized(TestPlugin.EditorScale), 3);

        // The plug-in asks for a new width: the window follows, and so does the editor in it.
        effect.SetNormalized(TestPlugin.EditorWidth, 1);
        Assert.Equal((600, 300), ClientSize(window));
        Assert.Equal((600, 300), ClientSize(view));

        // A click in it is an edit: the parameter shows it and the processor gets it.
        SendMessageW(view, WmLButtonDown, 0, 0);
        Assert.Equal(1f, effect.GetParameter("0").Value);
        effect.Prepare(48000, 1);
        var block = Enumerable.Repeat(0.01f, 64).ToArray();
        effect.Process(block);
        Assert.All(block, sample => Assert.Equal(0.01f * MathF.Pow(10, 12 / 20f), sample, 5));

        // Closed the way the user closes it.
        int changes = 0;
        effect.StatusChanged += (_, _) => changes++;
        SendMessageW(window, WmClose, 0, 0);
        Assert.False(effect.IsEditorOpen);
        Assert.False(IsWindow(window));
        Assert.Equal(1, changes);

        // And again, closed by the app.
        Assert.Null(effect.OpenEditor(0));
        effect.CloseEditor();
        Assert.False(effect.IsEditorOpen);
    }

    [Fact]
    public void Closing_the_plugin_closes_its_window()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Plug-in windows need Windows.");
        var effect = TestPlugin.Open();
        Assert.Null(effect.OpenEditor(0));
        nint window = ((LocalPlugin)effect.Instance).EditorWindow;

        effect.Dispose();

        Assert.False(IsWindow(window));
    }

    private static (int Width, int Height) ClientSize(nint window)
    {
        Assert.True(GetClientRect(window, out var rect));
        return (rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint FindWindowExW(nint parent, nint after, string? className, string? title);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetClientRect(nint window, out Rect rect);

    [LibraryImport("user32.dll")]
    private static partial nint SendMessageW(nint window, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindow(nint window);

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint window);
}
