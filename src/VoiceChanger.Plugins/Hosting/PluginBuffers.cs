using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using VoiceChanger.Plugins.Native;

namespace VoiceChanger.Plugins.Hosting;

/// <summary>
/// The memory a plug-in processes from and into: two channels each of input, side-chain and output,
/// and the parameter changes for the block. It lives in pinned memory for a plug-in in this process,
/// and in memory shared with the host process for one in another, so neither copies the audio.
/// </summary>
internal sealed unsafe class PluginBuffers : IDisposable
{
    public const int MaxChannels = 2;
    public const int MaxFrames = PluginEffect.MaxBlockFrames;
    public const int MaxChanges = 512;

    public const int Input = 0;
    public const int Sidechain = 1;
    public const int Output = 2;

    /// <summary>Bytes of one <see cref="ParamChange"/> (an id, padding and a double).</summary>
    public const int ChangeSize = 16;

    private const int ChannelBytes = MaxFrames * sizeof(float);
    private const int AudioBytes = 3 * MaxChannels * ChannelBytes;

    private readonly byte* _memory;
    private readonly IDisposable? _owner;

    // Pinned: per region, the pointers to its channels, as the bridge takes them.
    private readonly nint[] _pointers = GC.AllocateArray<nint>(3 * MaxChannels, pinned: true);

    private PluginBuffers(byte* memory, IDisposable? owner)
    {
        _memory = memory;
        _owner = owner;
        for (int i = 0; i < _pointers.Length; i++)
        {
            _pointers[i] = (nint)(memory + (i * ChannelBytes));
        }
    }

    /// <summary>Bytes the buffers take.</summary>
    public static int Size => AudioBytes + (MaxChanges * ChangeSize);

    public ParamChange* Changes => (ParamChange*)(_memory + AudioBytes);

    /// <summary>Buffers in pinned memory of this process.</summary>
    public static PluginBuffers Allocate()
    {
        var memory = GC.AllocateArray<byte>(Size, pinned: true);
        return new PluginBuffers((byte*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(memory)), new Keeper(memory));
    }

    /// <summary>Buffers in memory someone else owns (shared memory); <paramref name="owner"/> releases it.</summary>
    public static PluginBuffers Over(byte* memory, IDisposable? owner) => new(memory, owner);

    public float* Channel(int region, int channel) => (float*)_pointers[(region * MaxChannels) + channel];

    public Span<float> Span(int region, int channel, int frames) => new(Channel(region, channel), frames);

    /// <summary>The channel pointers of a region (input, side-chain or output).</summary>
    public float** Pointers(int region) => (float**)Unsafe.AsPointer(ref _pointers[region * MaxChannels]);

    public void Dispose() => _owner?.Dispose();

    // Keeps the pinned array alive as long as the buffers.
    private sealed class Keeper(byte[] memory) : IDisposable
    {
        public byte[] Memory { get; } = memory;

        public void Dispose()
        {
        }
    }
}
