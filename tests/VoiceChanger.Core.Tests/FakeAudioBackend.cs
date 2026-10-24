using VoiceChanger.Core.Audio;

namespace VoiceChanger.Core.Tests;

internal sealed class FakeAudioBackend : IAudioBackend
{
    public AudioFormat InputFormat { get; set; } = new(48000, 1);

    public AudioFormat OutputFormat { get; set; } = new(48000, 2);

    public Exception? OpenOutputError { get; set; }

    public List<FakeInputStream> Inputs { get; } = [];

    public List<FakeOutputStream> Outputs { get; } = [];

    public string Name => "Fake";

    public bool IsSupported => true;

    public IReadOnlyList<AudioDeviceInfo> GetDevices(AudioDeviceKind kind) =>
        kind == AudioDeviceKind.Input
            ? [new("mic", "Microphone", kind, IsDefault: true), new("line", "Line In", kind)]
            : [new("speakers", "Speakers", kind, IsDefault: true), new("cable", "Virtual Cable", kind)];

    public IAudioInputStream OpenInput(string deviceId, int bufferMilliseconds)
    {
        var stream = new FakeInputStream(deviceId, InputFormat);
        Inputs.Add(stream);
        return stream;
    }

    public IAudioOutputStream OpenOutput(string deviceId, int latencyMilliseconds)
    {
        if (OpenOutputError is not null)
        {
            throw OpenOutputError;
        }

        var stream = new FakeOutputStream(deviceId, OutputFormat, latencyMilliseconds);
        Outputs.Add(stream);
        return stream;
    }
}

internal sealed class FakeInputStream(string deviceId, AudioFormat format) : IAudioInputStream
{
    private AudioInputCallback? _callback;

    public string DeviceId { get; } = deviceId;

    public AudioFormat Format { get; } = format;

    public bool IsStarted => _callback is not null;

    public bool IsDisposed { get; private set; }

    public event EventHandler<AudioStreamStoppedEventArgs>? Stopped;

    public void Start(AudioInputCallback callback) => _callback = callback;

    public void Stop() => _callback = null;

    public void Push(ReadOnlySpan<float> samples) => _callback?.Invoke(samples);

    public void Fail(Exception error) => Stopped?.Invoke(this, new AudioStreamStoppedEventArgs(error));

    public void Dispose() => IsDisposed = true;
}

internal sealed class FakeOutputStream(string deviceId, AudioFormat format, int latencyMilliseconds) : IAudioOutputStream
{
    private AudioRenderCallback? _callback;

    public string DeviceId { get; } = deviceId;

    public AudioFormat Format { get; } = format;

    public int LatencyMilliseconds { get; } = latencyMilliseconds;

    public bool IsStarted => _callback is not null;

    public bool IsDisposed { get; private set; }

    public event EventHandler<AudioStreamStoppedEventArgs>? Stopped;

    public void Start(AudioRenderCallback callback) => _callback = callback;

    public void Stop() => _callback = null;

    public float[] Pull(int frames)
    {
        var buffer = new float[frames * Format.Channels];
        _callback?.Invoke(buffer);
        return buffer;
    }

    public void Fail(Exception error) => Stopped?.Invoke(this, new AudioStreamStoppedEventArgs(error));

    public void Dispose() => IsDisposed = true;
}
