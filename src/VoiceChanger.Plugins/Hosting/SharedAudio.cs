using System.IO.MemoryMappedFiles;
using VoiceChanger.Plugins.Native;

namespace VoiceChanger.Plugins.Hosting;

/// <summary>A named semaphore shared by the app and a host process (through the native bridge).</summary>
internal sealed class NamedSignal : IDisposable
{
    private nint _handle;

    private NamedSignal(nint handle) => _handle = handle;

    /// <summary>Creates the signal; the creator's <see cref="Dispose"/> removes the name.</summary>
    public static NamedSignal Create(string name)
    {
        nint handle = VcPlugin.CreateSignal(name);
        return handle != 0 ? new NamedSignal(handle) : throw new PluginException(VcPlugin.LastError);
    }

    public static NamedSignal Open(string name)
    {
        nint handle = VcPlugin.OpenSignal(name);
        return handle != 0 ? new NamedSignal(handle) : throw new PluginException(VcPlugin.LastError);
    }

    public void Post() => VcPlugin.PostSignal(_handle);

    /// <summary>Returns 1 when posted, 0 on timeout, negative on error.</summary>
    public int Wait(int timeoutMilliseconds) => VcPlugin.WaitSignal(_handle, timeoutMilliseconds);

    public void Dispose()
    {
        nint handle = Interlocked.Exchange(ref _handle, 0);
        if (handle != 0)
        {
            VcPlugin.CloseSignal(handle);
        }
    }
}

/// <summary>
/// The memory the app and a host process exchange audio through: a small header (the handshake's
/// sequence numbers and the block's details), then the <see cref="PluginBuffers"/>. The app creates
/// it, the host opens it by name.
/// </summary>
internal sealed unsafe class SharedAudio : IDisposable
{
    public const int HeaderSize = 64;
    public const int Magic = 0x31484356; // "VCH1"

    // Header fields (32-bit each).
    public const int MagicField = 0;
    public const int RequestField = 1;
    public const int DoneField = 2;
    public const int OpField = 3;
    public const int FramesField = 4;
    public const int FlagsField = 5;
    public const int ChangesField = 6;
    public const int ResultField = 7;

    public const int ProcessOp = 0;
    public const int ResetOp = 1;
    public const int SidechainFlag = 1;

    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _view;
    private readonly string? _path;
    private byte* _memory;

    private SharedAudio(MemoryMappedFile file, string? path)
    {
        _file = file;
        _path = path;
        _view = file.CreateViewAccessor(0, Size);
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref _memory);
        _memory += _view.PointerOffset;
        Buffers = PluginBuffers.Over(_memory + HeaderSize, null);
    }

    public static int Size => HeaderSize + PluginBuffers.Size;

    public PluginBuffers Buffers { get; }

    /// <summary>Creates the memory (the app's side).</summary>
    public static SharedAudio Create(string name)
    {
        SharedAudio shared;
        if (OperatingSystem.IsWindows())
        {
            shared = new SharedAudio(MemoryMappedFile.CreateNew("vc-" + name, Size), null);
        }
        else
        {
            // Named mappings are Windows only: elsewhere a file in memory (/dev/shm) both map.
            string path = PathOf(name);
            shared = new SharedAudio(MemoryMappedFile.CreateFromFile(path, FileMode.CreateNew, null, Size), path);
        }

        shared.Field(MagicField) = Magic;
        return shared;
    }

    /// <summary>Opens the memory the app created (the host's side).</summary>
    public static SharedAudio Open(string name)
    {
        var shared = OperatingSystem.IsWindows()
            ? new SharedAudio(MemoryMappedFile.OpenExisting("vc-" + name), null)
            : new SharedAudio(MemoryMappedFile.CreateFromFile(PathOf(name), FileMode.Open, null, Size), null);
        if (shared.Field(MagicField) != Magic)
        {
            shared.Dispose();
            throw new PluginException("The shared audio memory isn't VoiceChanger's.");
        }

        return shared;
    }

    /// <summary>A header field, read and written with <see cref="Volatile"/> where order matters.</summary>
    public ref int Field(int index) => ref ((int*)_memory)[index];

    /// <summary>Removes the memory's name once both sides have it (the memory stays until both let go).</summary>
    public void Unlink()
    {
        if (_path is not null)
        {
            try
            {
                File.Delete(_path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A stale file in /dev/shm or the temp folder is harmless.
            }
        }
    }

    public void Dispose()
    {
        if (_memory != null)
        {
            _memory = null;
            _view.SafeMemoryMappedViewHandle.ReleasePointer();
        }

        _view.Dispose();
        _file.Dispose();
        Unlink();
    }

    private static string PathOf(string name)
    {
        string folder = Directory.Exists("/dev/shm") ? "/dev/shm" : Path.GetTempPath();
        return Path.Combine(folder, "vc-" + name);
    }
}
