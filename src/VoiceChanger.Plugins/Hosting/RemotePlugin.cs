using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using VoiceChanger.Plugins.Native;
using static VoiceChanger.Plugins.Hosting.HostProtocol;

namespace VoiceChanger.Plugins.Hosting;

/// <summary>
/// A plug-in in a host process of its own (<see cref="PluginHostServer"/>). Requests go over a pipe;
/// audio goes through shared memory with a semaphore handshake in each block, so it adds no
/// buffering, only the switch to the host and back. When the host crashes or doesn't answer within
/// <see cref="HangTimeoutMilliseconds"/>, the instance is dead (<see cref="IPluginEvents.Died"/>) and
/// the effect starts a new one.
/// </summary>
internal sealed class RemotePlugin : IPluginInstance
{
    /// <summary>How long a block may take in the host before it counts as hung.</summary>
    public const int HangTimeoutMilliseconds = 1000;

    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(10);

    private readonly string _name;
    private readonly Process _process;
    private readonly AnonymousPipeServerStream _toHost;
    private readonly AnonymousPipeServerStream _fromHost;
    private readonly SharedAudio _shared;
    private readonly NamedSignal _request;
    private readonly NamedSignal _done;
    private readonly SynchronizationContext? _context;
    private readonly object _callGate = new();
    private readonly object _responseGate = new();
    private readonly ManualResetEventSlim _responded = new(false);
    private readonly ManualResetEvent _stuckSignal = new(false);
    private readonly RegisteredWaitHandle _watchdog;
    private int _nextId;
    private int _waitingFor;
    private BinaryReader? _response;
    private int _sequence;
    private volatile bool _alive = true;
    private volatile bool _stuck;
    private volatile bool _disposed;
    private bool _editorOpen;

    private RemotePlugin(string name, string session, Process process, AnonymousPipeServerStream toHost, AnonymousPipeServerStream fromHost, SharedAudio shared, NamedSignal request, NamedSignal done)
    {
        _name = name;
        Session = session;
        _process = process;
        _toHost = toHost;
        _fromHost = fromHost;
        _shared = shared;
        _request = request;
        _done = done;
        _context = SynchronizationContext.Current;

        // The audio thread only flags a hung host; this kills it (on a thread-pool thread).
        _watchdog = ThreadPool.RegisterWaitForSingleObject(_stuckSignal, (state, _) => ((RemotePlugin)state!).Kill(), this, Timeout.Infinite, executeOnlyOnce: true);
        new Thread(ReadReplies) { IsBackground = true, Name = $"Plug-in host replies ({name})" }.Start();
    }

    public PluginBuffers Buffers => _shared.Buffers;

    public PluginDescription Description { get; private set; } = null!;

    public IPluginEvents? Events { get; set; }

    public bool IsAlive => _alive && !_stuck;

    public bool IsEditorOpen => _editorOpen;

    /// <summary>The host process (for the tests).</summary>
    internal Process HostProcess => _process;

    /// <summary>The name of the shared memory and the signals (for the tests).</summary>
    internal string Session { get; }

    /// <summary>Starts a host process and loads the plug-in in it.</summary>
    /// <exception cref="PluginException">The host or the plug-in could not be started.</exception>
    public static RemotePlugin Start(PluginInfo info, PluginHostCommand command)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(command);
        if (!VcPlugin.IsAvailable(out string? problem))
        {
            throw new PluginException(problem!);
        }

        string session = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        var shared = SharedAudio.Create(session);
        NamedSignal? request = null;
        NamedSignal? done = null;
        AnonymousPipeServerStream? toHost = null;
        AnonymousPipeServerStream? fromHost = null;
        Process? process = null;
        try
        {
            request = NamedSignal.Create(session + "q");
            done = NamedSignal.Create(session + "d");
            toHost = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
            fromHost = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
            var start = new ProcessStartInfo(command.Executable)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (string argument in command.Arguments)
            {
                start.ArgumentList.Add(argument);
            }

            start.ArgumentList.Add(PluginHostServer.Command);
            start.ArgumentList.Add(toHost.GetClientHandleAsString());
            start.ArgumentList.Add(fromHost.GetClientHandleAsString());
            process = System.Diagnostics.Process.Start(start) ?? throw new PluginException("The plug-in host could not be started.");
            toHost.DisposeLocalCopyOfClientHandle();
            fromHost.DisposeLocalCopyOfClientHandle();

            // What the host or the plug-in prints goes to the log.
            process.OutputDataReceived += (_, e) => Log(info.Name, e.Data);
            process.ErrorDataReceived += (_, e) => Log(info.Name, e.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or PluginException)
        {
            process?.Dispose();
            toHost?.Dispose();
            fromHost?.Dispose();
            request?.Dispose();
            done?.Dispose();
            shared.Dispose();
            throw ex as PluginException ?? new PluginException($"The plug-in host could not be started: {ex.Message}");
        }

        var plugin = new RemotePlugin(info.Name, session, process, toHost, fromHost, shared, request, done);
        try
        {
            var reply = plugin.Call(Op.Open, writer =>
            {
                writer.Write(info.Path);
                writer.Write(info.ClassId);
                writer.Write(session);
            }, StartTimeout);
            plugin.Description = reply.ReadDescription();

            // Both sides have the memory now: its name can go.
            shared.Unlink();
            return plugin;
        }
        catch
        {
            plugin.Dispose();
            throw;
        }
    }

    public string? Setup(int sampleRate, int channels, out int latency)
    {
        latency = 0;
        try
        {
            var reply = Call(Op.Setup, writer =>
            {
                writer.Write(sampleRate);
                writer.Write(channels);
            });
            string? error = reply.ReadText();
            latency = reply.ReadInt32();
            return error;
        }
        catch (PluginException ex)
        {
            return ex.Message;
        }
    }

    public void Suspend() => TryCall(Op.Suspend);

    public double GetParam(uint id)
    {
        try
        {
            return Call(Op.GetParam, writer => writer.Write(id)).ReadDouble();
        }
        catch (PluginException)
        {
            return 0;
        }
    }

    public void SetParam(uint id, double value) => TryCall(Op.SetParam, writer =>
    {
        writer.Write(id);
        writer.Write(value);
    });

    public string? FormatParam(uint id, double value)
    {
        try
        {
            return Call(Op.Format, writer =>
            {
                writer.Write(id);
                writer.Write(value);
            }).ReadText();
        }
        catch (PluginException)
        {
            return null;
        }
    }

    public byte[]? GetState()
    {
        try
        {
            return Call(Op.GetState).ReadBytes();
        }
        catch (PluginException)
        {
            return null;
        }
    }

    public string? SetState(byte[] state) => CallForError(Op.SetState, writer => writer.WriteBytes(state));

    public string? LoadPreset(string path) => CallForError(Op.LoadPreset, writer => writer.Write(path));

    public string? SavePreset(string path) => CallForError(Op.SavePreset, writer => writer.Write(path));

    public string? OpenEditor(nint owner, string title)
    {
        string? error = CallForError(Op.OpenEditor, writer =>
        {
            writer.Write((long)owner);
            writer.Write(title);
        });
        _editorOpen |= error is null;
        return error;
    }

    public void CloseEditor()
    {
        TryCall(Op.CloseEditor);
        _editorOpen = false;
    }

    public int Process(int frames, bool withSidechain, int changeCount)
    {
        if (!_alive || _stuck)
        {
            return VcPlugin.ErrorFailed;
        }

        _shared.Field(SharedAudio.OpField) = SharedAudio.ProcessOp;
        _shared.Field(SharedAudio.FramesField) = frames;
        _shared.Field(SharedAudio.FlagsField) = withSidechain ? SharedAudio.SidechainFlag : 0;
        _shared.Field(SharedAudio.ChangesField) = changeCount;
        return Exchange();
    }

    public int Reset()
    {
        if (!_alive || _stuck)
        {
            return VcPlugin.ErrorFailed;
        }

        _shared.Field(SharedAudio.OpField) = SharedAudio.ResetOp;
        return Exchange();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_alive && !_stuck)
        {
            try
            {
                lock (_callGate)
                {
                    WriteFrame(_toHost, writer =>
                    {
                        writer.Write((byte)Kind.Request);
                        writer.Write(++_nextId);
                        writer.Write((byte)Op.Close);
                    });
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // It is gone already.
            }
        }

        _toHost.Dispose();
        try
        {
            if (!_process.WaitForExit(2000))
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // It ended on its own.
        }

        _watchdog.Unregister(null);
        _fromHost.Dispose();
        _process.Dispose();
        _request.Dispose();
        _done.Dispose();
        _shared.Dispose();
    }

    private static void Log(string name, string? line)
    {
        if (!string.IsNullOrEmpty(line))
        {
            Trace.WriteLine($"[{name}] {line}");
        }
    }

    /// <summary>Hands the block to the host and waits for it (audio thread).</summary>
    private int Exchange()
    {
        int sequence = ++_sequence;
        Volatile.Write(ref _shared.Field(SharedAudio.RequestField), sequence);
        _request.Post();
        long deadline = Environment.TickCount64 + HangTimeoutMilliseconds;
        while (true)
        {
            int remaining = (int)(deadline - Environment.TickCount64);
            if (remaining <= 0 || _done.Wait(remaining) != 1 || !_alive)
            {
                return GiveUpOnHost();
            }

            if (Volatile.Read(ref _shared.Field(SharedAudio.DoneField)) == sequence)
            {
                break;
            }
        }

        int result = _shared.Field(SharedAudio.ResultField);

        // The plug-in crashed and the host caught it: its memory may be broken, so start afresh.
        return result == VcPlugin.ErrorCrashed ? GiveUpOnHost() : result;
    }

    private int GiveUpOnHost()
    {
        _stuck = true;
        _stuckSignal.Set();
        return VcPlugin.ErrorFailed;
    }

    private void Kill()
    {
        try
        {
            _process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // It ended on its own.
        }
    }

    /// <summary>Sends a request and waits for its response (UI thread).</summary>
    private BinaryReader Call(Op op, Action<BinaryWriter>? arguments = null, TimeSpan? timeout = null)
    {
        lock (_callGate)
        {
            if (!_alive || _disposed)
            {
                throw new PluginException("The plug-in's host process has ended.");
            }

            int id = ++_nextId;
            lock (_responseGate)
            {
                _waitingFor = id;
                _response = null;
                _responded.Reset();
            }

            try
            {
                WriteFrame(_toHost, writer =>
                {
                    writer.Write((byte)Kind.Request);
                    writer.Write(id);
                    writer.Write((byte)op);
                    arguments?.Invoke(writer);
                });
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                throw new PluginException("The plug-in's host process has ended.");
            }

            if (!_responded.Wait(timeout ?? CallTimeout))
            {
                GiveUpOnHost();
                throw new PluginException("The plug-in stopped responding.");
            }

            var response = _response ?? throw new PluginException("The plug-in's host process has ended.");
            if (!response.ReadBoolean())
            {
                throw new PluginException(response.ReadString());
            }

            return response;
        }
    }

    private void TryCall(Op op, Action<BinaryWriter>? arguments = null)
    {
        try
        {
            Call(op, arguments);
        }
        catch (PluginException ex)
        {
            Trace.WriteLine($"{_name}: {op} failed: {ex.Message}");
        }
    }

    private string? CallForError(Op op, Action<BinaryWriter> arguments)
    {
        try
        {
            return Call(op, arguments).ReadText();
        }
        catch (PluginException ex)
        {
            return ex.Message;
        }
    }

    /// <summary>Reads the host's responses and events until it ends.</summary>
    private void ReadReplies()
    {
        try
        {
            while (ReadFrame(_fromHost) is { } frame)
            {
                var kind = (Kind)frame.ReadByte();
                if (kind == Kind.Response)
                {
                    int id = frame.ReadInt32();
                    lock (_responseGate)
                    {
                        if (id == _waitingFor)
                        {
                            _response = frame;
                            _responded.Set();
                        }
                    }
                }
                else if (kind == Kind.Event)
                {
                    OnEvent(frame);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ObjectDisposedException or EndOfStreamException)
        {
            // The host is gone.
        }

        _alive = false;
        _responded.Set();

        // Wakes an audio thread waiting for a block that won't come.
        _done.Post();
        if (_disposed)
        {
            return;
        }

        string reason = _stuck ? "The plug-in stopped responding." : "The plug-in crashed.";
        try
        {
            if (_process.WaitForExit(1000) && !_stuck)
            {
                reason = $"The plug-in crashed (its host ended with code {_process.ExitCode}).";
            }
        }
        catch (InvalidOperationException)
        {
        }

        Events?.Died(reason);
    }

    private void OnEvent(BinaryReader frame)
    {
        switch ((EventType)frame.ReadByte())
        {
            case EventType.Edited:
                uint id = frame.ReadUInt32();
                double value = frame.ReadDouble();
                Post(() => Events?.Edited(id, value));
                break;

            case EventType.Restart:
                // Any thread: the effect handles it when the call that caused it returns.
                Events?.RestartRequested((RestartFlags)frame.ReadInt32());
                break;

            case EventType.EditorClosed:
                _editorOpen = false;
                Post(() => Events?.EditorClosed());
                break;
        }
    }

    private void Post(Action action)
    {
        if (_context is { } context)
        {
            context.Post(static state => ((Action)state!)(), action);
        }
        else
        {
            action();
        }
    }
}
