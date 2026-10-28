using System.Diagnostics;
using System.Runtime.InteropServices;
using VoiceChanger.Plugins.Native;

namespace VoiceChanger.Plugins.Hosting;

/// <summary>A plug-in loaded into this process through the native bridge.</summary>
internal sealed unsafe class LocalPlugin : IPluginInstance
{
    /// <summary>Lists with more entries than this are shown as a number rather than a choice.</summary>
    public const int MaxChoices = 64;

    private readonly PluginHandle _handle;
    private PluginEditorWindow? _editor;

    private LocalPlugin(PluginHandle handle, PluginBuffers buffers)
    {
        _handle = handle;
        Buffers = buffers;
        Description = Describe(handle);
        handle.SetCallbacks(this, &OnEditedCallback, &OnRestartCallback, &OnResizeCallback);
    }

    public PluginBuffers Buffers { get; }

    public PluginDescription Description { get; }

    public IPluginEvents? Events { get; set; }

    public bool IsAlive => !_handle.IsClosed;

    public bool IsEditorOpen => _editor is not null;

    /// <summary>The window the plug-in's editor is in, or 0 (for the tests).</summary>
    internal nint EditorWindow => OperatingSystem.IsWindows() ? _editor?.Handle ?? 0 : 0;

    /// <summary>Loads a plug-in; <paramref name="buffers"/> defaults to pinned memory of this process.</summary>
    /// <exception cref="PluginException">The bridge or the plug-in could not be loaded.</exception>
    public static LocalPlugin Open(string path, string classId, PluginBuffers? buffers = null)
    {
        if (!VcPlugin.IsAvailable(out string? problem))
        {
            throw new PluginException(problem!);
        }

        var handle = PluginHandle.Open(path, classId, out string? error) ?? throw new PluginException(error ?? "The plug-in could not be loaded.");
        try
        {
            return new LocalPlugin(handle, buffers ?? PluginBuffers.Allocate());
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public string? Setup(int sampleRate, int channels, out int latency)
    {
        latency = 0;
        if (VcPlugin.Setup(_handle, sampleRate, PluginBuffers.MaxFrames, channels) != VcPlugin.Ok)
        {
            return VcPlugin.LastError;
        }

        latency = Math.Max(0, VcPlugin.GetLatency(_handle));
        return null;
    }

    public void Suspend() => VcPlugin.Suspend(_handle);

    public double GetParam(uint id) => VcPlugin.GetParam(_handle, id);

    public void SetParam(uint id, double value) => VcPlugin.SetParam(_handle, id, value);

    public string? FormatParam(uint id, double value) => Format(_handle, id, value);

    public byte[]? GetState()
    {
        int size = VcPlugin.GetState(_handle, null, 0);
        for (int attempt = 0; attempt < 3 && size >= 0; attempt++)
        {
            var state = new byte[size];
            fixed (byte* buffer = state)
            {
                int written = VcPlugin.GetState(_handle, buffer, state.Length);
                if (written == state.Length)
                {
                    return state;
                }

                // The state grew between asking for its size and writing it: ask again.
                size = written;
            }
        }

        return null;
    }

    public string? SetState(byte[] state)
    {
        fixed (byte* data = state)
        {
            return VcPlugin.SetState(_handle, data, state.Length) == VcPlugin.Ok ? null : VcPlugin.LastError;
        }
    }

    public string? LoadPreset(string path) => VcPlugin.LoadPreset(_handle, path) == VcPlugin.Ok ? null : VcPlugin.LastError;

    public string? SavePreset(string path) => VcPlugin.SavePreset(_handle, path) == VcPlugin.Ok ? null : VcPlugin.LastError;

    public string? OpenEditor(nint owner, string title)
    {
        if (!OperatingSystem.IsWindows())
        {
            return "Plug-in windows are only available on Windows.";
        }

        if (_editor is not null)
        {
            _editor.Activate();
            return null;
        }

        _editor = PluginEditorWindow.Open(_handle, owner, title, OnEditorClosed, out string? error);
        return error;
    }

    public void CloseEditor()
    {
        if (OperatingSystem.IsWindows())
        {
            _editor?.Close();
        }
    }

    public int Process(int frames, bool withSidechain, int changeCount) => VcPlugin.Process(
        _handle,
        Buffers.Pointers(PluginBuffers.Input),
        withSidechain ? Buffers.Pointers(PluginBuffers.Sidechain) : null,
        Buffers.Pointers(PluginBuffers.Output),
        frames,
        Buffers.Changes,
        changeCount);

    public int Reset() => VcPlugin.Reset(_handle);

    public void Dispose()
    {
        CloseEditor();
        _handle.Dispose();
        Buffers.Dispose();
    }

    private static string? Format(PluginHandle handle, uint id, double value)
    {
        const int capacity = 128;
        byte* text = stackalloc byte[capacity];
        return VcPlugin.FormatParam(handle, id, value, text, capacity) == VcPlugin.Ok ? VcPlugin.Text(text, capacity) : null;
    }

    private static PluginDescription Describe(PluginHandle handle)
    {
        PluginInfoNative info;
        if (VcPlugin.GetInfo(handle, &info) != VcPlugin.Ok)
        {
            throw new PluginException(VcPlugin.LastError);
        }

        var parameters = new List<PluginParameterInfo>();
        int count = VcPlugin.GetParamCount(handle);
        for (int index = 0; index < count; index++)
        {
            ParamInfo parameter;
            if (VcPlugin.DescribeParam(handle, index, &parameter) != VcPlugin.Ok)
            {
                continue;
            }

            int steps = Math.Max(0, parameter.StepCount);
            IReadOnlyList<string>? choices = null;
            if ((parameter.Flags & ParamFlags.List) != 0 && steps > 0 && steps < MaxChoices)
            {
                uint id = parameter.Id;
                choices = Enumerable.Range(0, steps + 1).Select(step => Format(handle, id, (double)step / steps) ?? step.ToString()).ToList();
            }

            parameters.Add(new PluginParameterInfo(
                parameter.Id,
                VcPlugin.Text(parameter.Title, 128),
                VcPlugin.Text(parameter.Units, 32),
                steps,
                parameter.DefaultValue,
                parameter.Flags,
                VcPlugin.GetParam(handle, parameter.Id),
                choices));
        }

        var programs = new List<string>();
        int programCount = VcPlugin.GetProgramCount(handle);
        byte* name = stackalloc byte[128];
        for (int index = 0; index < programCount; index++)
        {
            programs.Add(VcPlugin.GetProgramName(handle, index, name, 128) == VcPlugin.Ok ? VcPlugin.Text(name, 128) : $"Program {index + 1}");
        }

        long programParameter = VcPlugin.GetProgramParam(handle);
        return new PluginDescription(
            VcPlugin.Text(info.InputName, 64),
            VcPlugin.Text(info.SidechainName, 64),
            info.HasSidechain != 0,
            parameters,
            programs,
            programParameter >= 0 && programs.Count > 0 ? (uint)programParameter : null,
            OperatingSystem.IsWindows() && VcPlugin.IsEditorAvailable(handle, "HWND") != 0);
    }

    private void OnEditorClosed()
    {
        _editor = null;
        Events?.EditorClosed();
    }

    private static LocalPlugin? From(nint user) => GCHandle.FromIntPtr(user).Target as LocalPlugin;

    [UnmanagedCallersOnly]
    private static void OnEditedCallback(nint user, uint id, double value)
    {
        try
        {
            From(user)?.Events?.Edited(id, value);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Plug-in parameter edit failed: {ex}");
        }
    }

    [UnmanagedCallersOnly]
    private static void OnRestartCallback(nint user, int flags)
    {
        try
        {
            From(user)?.Events?.RestartRequested((RestartFlags)flags);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Plug-in restart request failed: {ex}");
        }
    }

    [UnmanagedCallersOnly]
    private static int OnResizeCallback(nint user, int width, int height)
    {
        try
        {
            return OperatingSystem.IsWindows() && From(user)?._editor is { } editor && editor.ResizeTo(width, height) ? 1 : 0;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Plug-in window resize failed: {ex}");
            return 0;
        }
    }
}
