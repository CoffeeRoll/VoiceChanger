using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using VoiceChanger.Plugins.Native;

namespace VoiceChanger.Plugins.Hosting;

/// <summary>
/// A top-level window holding a plug-in's editor (Windows). Its client area is the editor's size in
/// pixels; it follows the editor's resize requests, lets the user resize it within the editor's
/// limits when the editor allows, and passes on the monitor's scale. Messages are dispatched by
/// the thread that opened it (the app's UI thread, or the host process's main thread).
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe partial class PluginEditorWindow
{
    private const string ClassName = "VoiceChangerPluginWindow";

    private const uint WsOverlapped = 0x00000000;
    private const uint WsCaption = 0x00C00000;
    private const uint WsSysMenu = 0x00080000;
    private const uint WsMinimizeBox = 0x00020000;
    private const uint WsMaximizeBox = 0x00010000;
    private const uint WsThickFrame = 0x00040000;
    private const uint FixedStyle = WsOverlapped | WsCaption | WsSysMenu | WsMinimizeBox;
    private const uint ResizableStyle = FixedStyle | WsThickFrame | WsMaximizeBox;
    private const uint WsExDlgModalFrame = 0x00000001;

    private const uint WmDestroy = 0x0002;
    private const uint WmSize = 0x0005;
    private const uint WmClose = 0x0010;
    private const uint WmSizing = 0x0214;
    private const uint WmDpiChanged = 0x02E0;

    private const int GwlStyle = -16;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const int SwShow = 5;
    private const int SwRestore = 9;
    private const int CwUseDefault = unchecked((int)0x80000000);

    private static readonly Dictionary<nint, PluginEditorWindow> OpenWindows = [];
    private static nint _className;

    private readonly PluginHandle _plugin;
    private readonly Action _closed;
    private bool _resizable;
    private bool _closing;

    private PluginEditorWindow(PluginHandle plugin, Action closed)
    {
        _plugin = plugin;
        _closed = closed;
    }

    /// <summary>The window's handle, or 0 once it is closed.</summary>
    public nint Handle { get; private set; }

    private uint Style => _resizable ? ResizableStyle : FixedStyle;

    /// <summary>Opens the plug-in's editor in a new window owned by <paramref name="owner"/>.</summary>
    public static PluginEditorWindow? Open(PluginHandle plugin, nint owner, string title, Action closed, out string? error)
    {
        EnsureClass();
        var window = new PluginEditorWindow(plugin, closed);
        nint handle = CreateWindowExW(WsExDlgModalFrame, ClassName, title, FixedStyle, CwUseDefault, CwUseDefault, 400, 300, owner, 0, GetModuleHandleW(null), 0);
        if (handle == 0)
        {
            error = $"The window could not be created (error {Marshal.GetLastPInvokeError()}).";
            return null;
        }

        window.Handle = handle;
        OpenWindows[handle] = window;
        if (VcPlugin.OpenEditor(plugin, handle, "HWND", out int width, out int height, out int resizable) != VcPlugin.Ok)
        {
            error = VcPlugin.LastError;
            window._closing = true;
            DestroyWindow(handle);
            return null;
        }

        window._resizable = resizable != 0;
        if (window._resizable)
        {
            SetWindowLongPtrW(handle, GwlStyle, (nint)ResizableStyle);
        }

        window.SetClientSize(width, height, frameChanged: true);

        // Editors that scale themselves ask for a new size here.
        VcPlugin.SetEditorScale(plugin, GetDpiForWindow(handle) / 96f);
        ShowWindow(handle, SwShow);
        error = null;
        return window;
    }

    /// <summary>Brings the window to the front.</summary>
    public void Activate()
    {
        if (Handle != 0)
        {
            ShowWindow(Handle, SwRestore);
            SetForegroundWindow(Handle);
        }
    }

    /// <summary>The editor asks for a new size: makes the window fit it.</summary>
    public bool ResizeTo(int width, int height)
    {
        if (Handle == 0 || width <= 0 || height <= 0)
        {
            return false;
        }

        SetClientSize(width, height, frameChanged: false);
        return true;
    }

    /// <summary>Closes the editor and the window.</summary>
    public void Close()
    {
        if (Handle == 0 || _closing)
        {
            return;
        }

        _closing = true;
        VcPlugin.CloseEditor(_plugin);
        DestroyWindow(Handle);
    }

    private static void EnsureClass()
    {
        if (_className != 0)
        {
            return;
        }

        _className = Marshal.StringToHGlobalUni(ClassName);
        var description = new WindowClass
        {
            Size = (uint)sizeof(WindowClass),
            WindowProcedure = &WindowProcedure,
            Instance = GetModuleHandleW(null),
            Cursor = LoadCursorW(0, 32512), // IDC_ARROW
            Background = 16, // COLOR_BTNFACE + 1
            ClassName = _className,
        };
        if (RegisterClassExW(&description) == 0)
        {
            Trace.WriteLine($"The plug-in window class could not be registered (error {Marshal.GetLastPInvokeError()}).");
        }
    }

    [UnmanagedCallersOnly]
    private static nint WindowProcedure(nint handle, uint message, nint wParam, nint lParam)
    {
        try
        {
            if (OpenWindows.TryGetValue(handle, out var window) && window.OnMessage(message, wParam, lParam, out nint result))
            {
                return result;
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Plug-in window message {message} failed: {ex}");
        }

        return DefWindowProcW(handle, message, wParam, lParam);
    }

    private bool OnMessage(uint message, nint wParam, nint lParam, out nint result)
    {
        result = 0;
        switch (message)
        {
            case WmSize when wParam != 1: // not minimised
                VcPlugin.SetEditorSize(_plugin, (int)(lParam & 0xFFFF), (int)((lParam >> 16) & 0xFFFF));
                return true;

            case WmSizing when _resizable:
                Constrain((int)wParam, (Rect*)lParam);
                result = 1;
                return true;

            case WmDpiChanged:
                VcPlugin.SetEditorScale(_plugin, (int)(wParam & 0xFFFF) / 96f);
                var suggested = (Rect*)lParam;
                SetWindowPos(Handle, 0, suggested->Left, suggested->Top, suggested->Right - suggested->Left, suggested->Bottom - suggested->Top, SwpNoZOrder | SwpNoActivate);
                return true;

            case WmClose:
                Close();
                return true;

            case WmDestroy:
                OpenWindows.Remove(Handle);
                Handle = 0;
                _closed();
                return true;
        }

        return false;
    }

    /// <summary>Keeps a window the user drags to a size the editor accepts.</summary>
    private void Constrain(int edge, Rect* window)
    {
        var frame = Frame();
        int width = (window->Right - window->Left) - (frame.Right - frame.Left);
        int height = (window->Bottom - window->Top) - (frame.Bottom - frame.Top);
        if (VcPlugin.ConstrainEditor(_plugin, ref width, ref height) != VcPlugin.Ok)
        {
            return;
        }

        int outerWidth = width + (frame.Right - frame.Left);
        int outerHeight = height + (frame.Bottom - frame.Top);

        // WMSZ_LEFT, WMSZ_TOPLEFT and WMSZ_BOTTOMLEFT move the left edge; WMSZ_TOP, TOPLEFT and TOPRIGHT the top.
        if (edge is 1 or 4 or 7)
        {
            window->Left = window->Right - outerWidth;
        }
        else
        {
            window->Right = window->Left + outerWidth;
        }

        if (edge is 3 or 4 or 5)
        {
            window->Top = window->Bottom - outerHeight;
        }
        else
        {
            window->Bottom = window->Top + outerHeight;
        }
    }

    /// <summary>The window's border around an empty client area, at its monitor's scale.</summary>
    private Rect Frame()
    {
        var frame = default(Rect);
        AdjustWindowRectExForDpi(&frame, Style, false, WsExDlgModalFrame, GetDpiForWindow(Handle));
        return frame;
    }

    private void SetClientSize(int width, int height, bool frameChanged)
    {
        var frame = Frame();
        SetWindowPos(
            Handle,
            0,
            0,
            0,
            width + (frame.Right - frame.Left),
            height + (frame.Bottom - frame.Top),
            SwpNoMove | SwpNoZOrder | SwpNoActivate | (frameChanged ? SwpFrameChanged : 0));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowClass
    {
        public uint Size;
        public uint Style;
        public delegate* unmanaged<nint, uint, nint, nint, nint> WindowProcedure;
        public int ClassExtra;
        public int WindowExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        public nint MenuName;
        public nint ClassName;
        public nint SmallIcon;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial ushort RegisterClassExW(WindowClass* description);

    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateWindowExW(uint exStyle, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint window);

    [LibraryImport("user32.dll")]
    private static partial nint DefWindowProcW(nint window, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint window, int command);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);

    [LibraryImport("user32.dll")]
    private static partial nint SetWindowLongPtrW(nint window, int index, nint value);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AdjustWindowRectExForDpi(Rect* rect, uint style, [MarshalAs(UnmanagedType.Bool)] bool menu, uint exStyle, uint dpi);

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint window);

    [LibraryImport("user32.dll")]
    private static partial nint LoadCursorW(nint instance, nint name);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandleW(string? name);
}
