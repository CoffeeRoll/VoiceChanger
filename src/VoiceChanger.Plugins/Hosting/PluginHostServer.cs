using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using VoiceChanger.Plugins.Native;
using static VoiceChanger.Plugins.Hosting.HostProtocol;

namespace VoiceChanger.Plugins.Hosting;

/// <summary>
/// The host process side of a plug-in that runs outside the app: it loads the plug-in, answers the
/// app's requests on its main thread (which on Windows also runs the plug-in's window), and
/// processes audio on a thread of its own, through memory shared with the app. It ends when the app
/// closes the pipes, which also happens when the app ends.
/// </summary>
public static class PluginHostServer
{
    /// <summary>The app (or VoiceChanger.PluginHost) hosts a plug-in when run with this and the two pipe handles.</summary>
    public const string Command = "--plugin-host";

    /// <summary>Hosts a plug-in until the app lets go. Returns the process exit code.</summary>
    public static int Run(string inputHandle, string outputHandle)
    {
        using var input = new AnonymousPipeClientStream(PipeDirection.In, inputHandle);
        using var output = new AnonymousPipeClientStream(PipeDirection.Out, outputHandle);
        using var server = new HostServer(input, output);
        return server.Run();
    }
}

internal sealed class HostServer(Stream input, Stream output) : IPluginEvents, IDisposable
{
    private readonly object _writeGate = new();
    private readonly ConcurrentQueue<BinaryReader> _requests = new();
    private readonly AutoResetEvent _work = new(false);
    private volatile bool _inputClosed;
    private volatile bool _stopAudio;
    private bool _quit;
    private LocalPlugin? _plugin;
    private SharedAudio? _shared;
    private NamedSignal? _request;
    private NamedSignal? _done;
    private Thread? _audio;

    public int Run()
    {
        new Thread(ReadRequests) { IsBackground = true, Name = "Plug-in host requests" }.Start();
        while (!_quit)
        {
            WaitForWork();
            while (!_quit && _requests.TryDequeue(out var request))
            {
                Handle(request);
            }

            _quit |= _inputClosed && _requests.IsEmpty;
        }

        return 0;
    }

    public void Dispose()
    {
        _stopAudio = true;
        _request?.Post();
        _audio?.Join(TimeSpan.FromSeconds(2));
        _plugin?.Dispose();
        _request?.Dispose();
        _done?.Dispose();
        _shared?.Dispose();
        _work.Dispose();
    }

    void IPluginEvents.Edited(uint id, double value) => Send(writer =>
    {
        writer.Write((byte)Kind.Event);
        writer.Write((byte)EventType.Edited);
        writer.Write(id);
        writer.Write(value);
    });

    void IPluginEvents.RestartRequested(RestartFlags flags) => Send(writer =>
    {
        writer.Write((byte)Kind.Event);
        writer.Write((byte)EventType.Restart);
        writer.Write((int)flags);
    });

    void IPluginEvents.EditorClosed() => Send(writer =>
    {
        writer.Write((byte)Kind.Event);
        writer.Write((byte)EventType.EditorClosed);
    });

    void IPluginEvents.Died(string reason)
    {
    }

    private void ReadRequests()
    {
        try
        {
            while (ReadFrame(input) is { } frame)
            {
                _requests.Enqueue(frame);
                _work.Set();
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ObjectDisposedException)
        {
            // The app is gone.
        }

        _inputClosed = true;
        _work.Set();
    }

    private void WaitForWork()
    {
        if (OperatingSystem.IsWindows())
        {
            MessageLoop.WaitAndDispatch(_work);
        }
        else
        {
            _work.WaitOne();
        }
    }

    private void Handle(BinaryReader request)
    {
        request.ReadByte(); // Kind.Request
        int id = request.ReadInt32();
        var op = (Op)request.ReadByte();
        using var body = new MemoryStream();
        using var writer = new BinaryWriter(body);
        try
        {
            writer.Write((byte)Kind.Response);
            writer.Write(id);
            writer.Write(true);
            Execute(op, request, writer);
        }
        catch (Exception ex)
        {
            body.SetLength(0);
            writer.Write((byte)Kind.Response);
            writer.Write(id);
            writer.Write(false);
            writer.Write(ex is PluginException ? ex.Message : ex.ToString());
        }

        writer.Flush();
        Send(frame => frame.Write(body.GetBuffer(), 0, (int)body.Length));
    }

    private void Execute(Op op, BinaryReader request, BinaryWriter response)
    {
        if (op == Op.Open)
        {
            Open(request.ReadString(), request.ReadString(), request.ReadString());
            response.WriteDescription(_plugin!.Description);
            return;
        }

        if (op == Op.Close)
        {
            _quit = true;
            return;
        }

        var plugin = _plugin ?? throw new PluginException("No plug-in is open.");
        switch (op)
        {
            case Op.Setup:
                response.WriteText(plugin.Setup(request.ReadInt32(), request.ReadInt32(), out int latency));
                response.Write(latency);
                break;
            case Op.Suspend:
                plugin.Suspend();
                break;
            case Op.GetParam:
                response.Write(plugin.GetParam(request.ReadUInt32()));
                break;
            case Op.SetParam:
                plugin.SetParam(request.ReadUInt32(), request.ReadDouble());
                break;
            case Op.Format:
                response.WriteText(plugin.FormatParam(request.ReadUInt32(), request.ReadDouble()));
                break;
            case Op.GetState:
                response.WriteBytes(plugin.GetState());
                break;
            case Op.SetState:
                response.WriteText(plugin.SetState(request.ReadBytes() ?? []));
                break;
            case Op.LoadPreset:
                response.WriteText(plugin.LoadPreset(request.ReadString()));
                break;
            case Op.SavePreset:
                response.WriteText(plugin.SavePreset(request.ReadString()));
                break;
            case Op.OpenEditor:
                response.WriteText(plugin.OpenEditor((nint)request.ReadInt64(), request.ReadString()));
                break;
            case Op.CloseEditor:
                plugin.CloseEditor();
                break;
            default:
                throw new PluginException($"Unknown request {op}.");
        }
    }

    private void Open(string path, string classId, string session)
    {
        if (_plugin is not null)
        {
            throw new PluginException("A plug-in is open already.");
        }

        _shared = SharedAudio.Open(session);
        _request = NamedSignal.Open(session + "q");
        _done = NamedSignal.Open(session + "d");
        _plugin = LocalPlugin.Open(path, classId, _shared.Buffers);
        _plugin.Events = this;
        _audio = new Thread(ProcessAudio) { IsBackground = true, Name = "Plug-in audio", Priority = ThreadPriority.Highest };
        _audio.Start();
    }

    /// <summary>Waits for the app's blocks and processes them (the audio thread).</summary>
    private void ProcessAudio()
    {
        if (OperatingSystem.IsWindows())
        {
            MessageLoop.JoinProAudio();
        }

        var shared = _shared!;
        var plugin = _plugin!;
        while (!_stopAudio)
        {
            if (_request!.Wait(250) <= 0 || _stopAudio)
            {
                continue;
            }

            int sequence = Volatile.Read(ref shared.Field(SharedAudio.RequestField));
            int result = shared.Field(SharedAudio.OpField) == SharedAudio.ResetOp
                ? plugin.Reset()
                : plugin.Process(
                    shared.Field(SharedAudio.FramesField),
                    (shared.Field(SharedAudio.FlagsField) & SharedAudio.SidechainFlag) != 0,
                    shared.Field(SharedAudio.ChangesField));
            shared.Field(SharedAudio.ResultField) = result;
            Volatile.Write(ref shared.Field(SharedAudio.DoneField), sequence);
            _done!.Post();
        }
    }

    private void Send(Action<BinaryWriter> write)
    {
        lock (_writeGate)
        {
            try
            {
                WriteFrame(output, write);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // The app is gone; the request loop ends too.
                _inputClosed = true;
            }
        }
    }
}

/// <summary>Keeps a Windows thread's windows (the plug-in's) alive while it waits for work.</summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class MessageLoop
{
    private const uint Infinite = 0xFFFFFFFF;
    private const uint QsAllInput = 0x04FF;
    private const uint MwmoInputAvailable = 0x0004;
    private const uint PmRemove = 0x0001;

    /// <summary>Dispatches window messages until <paramref name="work"/> is set.</summary>
    public static void WaitAndDispatch(WaitHandle work)
    {
        nint handle = work.SafeWaitHandle.DangerousGetHandle();
        while (true)
        {
            uint result = MsgWaitForMultipleObjectsEx(1, &handle, Infinite, QsAllInput, MwmoInputAvailable);
            Message message;
            while (PeekMessageW(&message, 0, 0, 0, PmRemove))
            {
                TranslateMessage(&message);
                DispatchMessageW(&message);
            }

            if (result == 0)
            {
                return;
            }
        }
    }

    /// <summary>Asks Windows to schedule the calling thread as pro audio (MMCSS).</summary>
    public static void JoinProAudio()
    {
        try
        {
            uint task = 0;
            AvSetMmThreadCharacteristicsW("Pro Audio", ref task);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Trace.WriteLine("Pro audio scheduling isn't available.");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public nint Window;
        public uint Id;
        public nint WParam;
        public nint LParam;
        public uint Time;
        public int X;
        public int Y;
        public uint Private;
    }

    [LibraryImport("user32.dll")]
    private static partial uint MsgWaitForMultipleObjectsEx(uint count, nint* handles, uint milliseconds, uint wakeMask, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PeekMessageW(Message* message, nint window, uint first, uint last, uint remove);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TranslateMessage(Message* message);

    [LibraryImport("user32.dll")]
    private static partial nint DispatchMessageW(Message* message);

    [LibraryImport("avrt.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint AvSetMmThreadCharacteristicsW(string task, ref uint index);
}
