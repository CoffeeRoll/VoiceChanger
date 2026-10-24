namespace VoiceChanger.Core.Audio;

public enum AudioDeviceKind
{
    Input,
    Output,
}

/// <summary>An audio endpoint the user can pick.</summary>
public sealed record AudioDeviceInfo(string Id, string Name, AudioDeviceKind Kind, bool IsDefault = false)
{
    public override string ToString() => IsDefault ? $"{Name} (default)" : Name;
}

/// <summary>Sample rate and channel count of a stream of interleaved 32-bit float samples.</summary>
public readonly record struct AudioFormat(int SampleRate, int Channels)
{
    public override string ToString() => $"{SampleRate / 1000.0:0.#} kHz, {Channels} ch";
}

/// <summary>Called on the capture thread with interleaved samples in the stream's <see cref="AudioFormat"/>.</summary>
public delegate void AudioInputCallback(ReadOnlySpan<float> interleaved);

/// <summary>Called on the render thread; the callback must fill the whole buffer.</summary>
public delegate void AudioRenderCallback(Span<float> interleaved);

public sealed class AudioStreamStoppedEventArgs(Exception? error) : EventArgs
{
    /// <summary>The failure that stopped the stream, or <c>null</c> when it was stopped on purpose.</summary>
    public Exception? Error { get; } = error;
}

/// <summary>A platform audio API (for example WASAPI on Windows).</summary>
public interface IAudioBackend
{
    string Name { get; }

    /// <summary>Whether this backend can be used on the current machine.</summary>
    bool IsSupported { get; }

    IReadOnlyList<AudioDeviceInfo> GetDevices(AudioDeviceKind kind);

    /// <param name="bufferMilliseconds">Requested capture buffer length.</param>
    IAudioInputStream OpenInput(string deviceId, int bufferMilliseconds);

    /// <param name="latencyMilliseconds">Requested output buffer length.</param>
    IAudioOutputStream OpenOutput(string deviceId, int latencyMilliseconds);
}

public interface IAudioInputStream : IDisposable
{
    AudioFormat Format { get; }

    /// <summary>Raised when the stream stops. May be raised on any thread.</summary>
    event EventHandler<AudioStreamStoppedEventArgs>? Stopped;

    void Start(AudioInputCallback callback);

    void Stop();
}

public interface IAudioOutputStream : IDisposable
{
    AudioFormat Format { get; }

    /// <summary>The output buffer length actually in use.</summary>
    int LatencyMilliseconds { get; }

    /// <summary>Raised when the stream stops. May be raised on any thread.</summary>
    event EventHandler<AudioStreamStoppedEventArgs>? Stopped;

    void Start(AudioRenderCallback callback);

    void Stop();
}

/// <summary>Backend used on platforms without a supported audio API: it has no devices.</summary>
public sealed class NullAudioBackend : IAudioBackend
{
    public string Name => "No audio backend";

    public bool IsSupported => false;

    public IReadOnlyList<AudioDeviceInfo> GetDevices(AudioDeviceKind kind) => [];

    public IAudioInputStream OpenInput(string deviceId, int bufferMilliseconds) =>
        throw new PlatformNotSupportedException("Audio capture is not supported on this platform.");

    public IAudioOutputStream OpenOutput(string deviceId, int latencyMilliseconds) =>
        throw new PlatformNotSupportedException("Audio playback is not supported on this platform.");
}
