using System.Runtime.InteropServices;
using System.Text;

namespace VoiceChanger.Plugins.Native;

/// <summary>The C API of the native bridge (native/bridge/vcplugin.h).</summary>
internal static unsafe partial class VcPlugin
{
    public const string Library = "vcplugin";

    /// <summary>The API version these declarations are written against.</summary>
    public const int ApiVersion = 1;

    public const int Ok = 0;
    public const int ErrorFailed = -1;
    public const int ErrorArgument = -2;
    public const int ErrorNotActive = -3;
    public const int ErrorCrashed = -4;

    [LibraryImport(Library, EntryPoint = "vc_api_version")]
    public static partial int GetApiVersion();

    [LibraryImport(Library, EntryPoint = "vc_last_error")]
    private static partial byte* GetLastError();

    [LibraryImport(Library, EntryPoint = "vc_scan", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int Scan(string path, ClassInfo* classes, int capacity);

    [LibraryImport(Library, EntryPoint = "vc_open", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint Open(string path, string classId);

    [LibraryImport(Library, EntryPoint = "vc_close")]
    public static partial void Close(nint plugin);

    [LibraryImport(Library, EntryPoint = "vc_setup")]
    public static partial int Setup(PluginHandle plugin, double sampleRate, int maxFrames, int channels);

    [LibraryImport(Library, EntryPoint = "vc_suspend")]
    public static partial void Suspend(PluginHandle plugin);

    [LibraryImport(Library, EntryPoint = "vc_reset")]
    public static partial int Reset(PluginHandle plugin);

    [LibraryImport(Library, EntryPoint = "vc_info")]
    public static partial int GetInfo(PluginHandle plugin, PluginInfoNative* info);

    [LibraryImport(Library, EntryPoint = "vc_process")]
    public static partial int Process(
        PluginHandle plugin,
        float** inputs,
        float** sidechain,
        float** outputs,
        int frames,
        ParamChange* changes,
        int changeCount);

    [LibraryImport(Library, EntryPoint = "vc_latency")]
    public static partial int GetLatency(PluginHandle plugin);

    [LibraryImport(Library, EntryPoint = "vc_param_count")]
    public static partial int GetParamCount(PluginHandle plugin);

    [LibraryImport(Library, EntryPoint = "vc_param_describe")]
    public static partial int DescribeParam(PluginHandle plugin, int index, ParamInfo* info);

    [LibraryImport(Library, EntryPoint = "vc_param_get")]
    public static partial double GetParam(PluginHandle plugin, uint id);

    [LibraryImport(Library, EntryPoint = "vc_param_set")]
    public static partial int SetParam(PluginHandle plugin, uint id, double value);

    [LibraryImport(Library, EntryPoint = "vc_param_format")]
    public static partial int FormatParam(PluginHandle plugin, uint id, double value, byte* text, int capacity);

    [LibraryImport(Library, EntryPoint = "vc_state_get")]
    public static partial int GetState(PluginHandle plugin, byte* buffer, int capacity);

    [LibraryImport(Library, EntryPoint = "vc_state_set")]
    public static partial int SetState(PluginHandle plugin, byte* data, int length);

    [LibraryImport(Library, EntryPoint = "vc_set_callbacks")]
    public static partial void SetCallbacks(
        PluginHandle plugin,
        nint user,
        delegate* unmanaged<nint, uint, double, void> edited,
        delegate* unmanaged<nint, int, void> restart);

    /// <summary>Why the last call on this thread failed.</summary>
    public static string LastError => Text(GetLastError(), int.MaxValue);

    /// <summary>Reads a zero-terminated UTF-8 string of at most <paramref name="capacity"/> bytes.</summary>
    public static string Text(byte* text, int capacity)
    {
        if (text is null)
        {
            return "";
        }

        var bytes = MemoryMarshal.CreateReadOnlySpanFromNullTerminated(text);
        return Encoding.UTF8.GetString(bytes[..Math.Min(bytes.Length, capacity)]);
    }

    /// <summary>Whether the native bridge can be loaded, with the reason when it can't.</summary>
    public static bool IsAvailable(out string? problem)
    {
        try
        {
            int version = GetApiVersion();
            problem = version == ApiVersion ? null : $"The plug-in bridge has version {version}, expected {ApiVersion}.";
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            problem = $"The plug-in bridge ({Library}) is missing: {ex.Message}";
        }

        return problem is null;
    }
}

/// <summary>Restart flags a plug-in sends (Steinberg::Vst::RestartFlags).</summary>
[Flags]
internal enum RestartFlags
{
    None = 0,
    ReloadComponent = 1 << 0,
    IoChanged = 1 << 1,
    ParamValuesChanged = 1 << 2,
    LatencyChanged = 1 << 3,
    ParamTitlesChanged = 1 << 4,
}

/// <summary>Parameter flags (Steinberg::Vst::ParameterInfo::ParameterFlags).</summary>
[Flags]
internal enum ParamFlags
{
    None = 0,
    CanAutomate = 1 << 0,
    ReadOnly = 1 << 1,
    Wrap = 1 << 2,
    List = 1 << 3,
    Hidden = 1 << 4,
    ProgramChange = 1 << 15,
    Bypass = 1 << 16,
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ClassInfo
{
    public fixed byte ClassId[40];
    public fixed byte Name[128];
    public fixed byte Vendor[128];
    public fixed byte Version[64];
    public fixed byte SdkVersion[64];
    public fixed byte Subcategories[128];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct PluginInfoNative
{
    public fixed byte Name[128];
    public fixed byte Vendor[128];
    public fixed byte InputName[64];
    public fixed byte SidechainName[64];
    public int HasSidechain;
    public int InputChannels;
    public int OutputChannels;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ParamInfo
{
    public uint Id;
    public int StepCount;
    public double DefaultValue;
    public ParamFlags Flags;
    public fixed byte Title[128];
    public fixed byte ShortTitle[64];
    public fixed byte Units[32];
}

[StructLayout(LayoutKind.Sequential)]
internal struct ParamChange
{
    public uint Id;
    public double Value;
}

/// <summary>An open plug-in. Closing it (or letting it be collected) unloads it.</summary>
internal sealed class PluginHandle : SafeHandle
{
    private GCHandle _callbackTarget;

    public PluginHandle()
        : base(0, ownsHandle: true)
    {
    }

    public override bool IsInvalid => handle == 0;

    /// <summary>Opens a plug-in, or returns <c>null</c> with the reason.</summary>
    public static PluginHandle? Open(string path, string classId, out string? error)
    {
        nint plugin = VcPlugin.Open(path, classId);
        if (plugin == 0)
        {
            error = VcPlugin.LastError;
            return null;
        }

        var result = new PluginHandle();
        result.SetHandle(plugin);
        error = null;
        return result;
    }

    /// <summary>
    /// Sends the plug-in's callbacks to <paramref name="target"/>, which receives them as the
    /// <c>user</c> argument (a weak handle: the plug-in doesn't keep its effect alive).
    /// </summary>
    public unsafe void SetCallbacks(object target, delegate* unmanaged<nint, uint, double, void> edited, delegate* unmanaged<nint, int, void> restart)
    {
        if (!_callbackTarget.IsAllocated)
        {
            _callbackTarget = GCHandle.Alloc(target, GCHandleType.Weak);
        }

        VcPlugin.SetCallbacks(this, GCHandle.ToIntPtr(_callbackTarget), edited, restart);
    }

    protected override bool ReleaseHandle()
    {
        VcPlugin.Close(handle);
        if (_callbackTarget.IsAllocated)
        {
            _callbackTarget.Free();
        }

        return true;
    }
}
