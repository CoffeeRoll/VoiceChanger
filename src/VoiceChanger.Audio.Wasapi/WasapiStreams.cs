using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using VoiceChanger.Core.Audio;

namespace VoiceChanger.Audio.Wasapi;

[SupportedOSPlatform("windows")]
internal sealed class WasapiInputStream : IAudioInputStream
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(2);

    private readonly MMDevice _device;
    private readonly WasapiCapture _capture;
    private readonly SampleEncoding _encoding;
    private readonly ManualResetEventSlim _stopped = new(initialState: true);
    private AudioInputCallback? _callback;
    private float[] _samples = new float[8192];
    private bool _disposed;

    public WasapiInputStream(MMDevice device, int bufferMilliseconds)
    {
        _device = device;
        try
        {
            _capture = WasapiAudioBackend.WithoutSynchronizationContext(
                () => new WasapiCapture(device, useEventSync: true, audioBufferMillisecondsLength: Math.Max(3, bufferMilliseconds)));

            // WasapiCapture reports the shared-mode mix format in its standard (non-extensible) form.
            var format = _capture.WaveFormat;
            _encoding = Formats.GetEncoding(format);
            Format = new AudioFormat(format.SampleRate, format.Channels);
        }
        catch
        {
            _capture?.Dispose();
            device.Dispose();
            throw;
        }

        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;
    }

    public AudioFormat Format { get; }

    public event EventHandler<AudioStreamStoppedEventArgs>? Stopped;

    public void Start(AudioInputCallback callback)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _callback = callback ?? throw new ArgumentNullException(nameof(callback));
        _stopped.Reset();
        try
        {
            _capture.StartRecording();
        }
        catch
        {
            _stopped.Set();
            throw;
        }
    }

    public void Stop()
    {
        if (_stopped.IsSet)
        {
            return;
        }

        _capture.StopRecording();
        _stopped.Wait(StopTimeout);
        _callback = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        _capture.DataAvailable -= OnDataAvailable;
        _capture.RecordingStopped -= OnRecordingStopped;
        _capture.Dispose();
        _device.Dispose();
        _stopped.Dispose();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        var callback = _callback;
        if (callback is null || e.BytesRecorded == 0)
        {
            return;
        }

        AudioThread.Register();

        int count = e.BytesRecorded / SampleConverter.BytesPerSample(_encoding);
        if (_samples.Length < count)
        {
            _samples = new float[count];
        }

        int converted = SampleConverter.ToFloat(e.Buffer.AsSpan(0, e.BytesRecorded), _encoding, _samples);
        callback(_samples.AsSpan(0, converted - converted % Format.Channels));
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        _stopped.Set();
        Stopped?.Invoke(this, new AudioStreamStoppedEventArgs(e.Exception));
    }
}

[SupportedOSPlatform("windows")]
internal sealed class WasapiOutputStream : IAudioOutputStream
{
    private readonly MMDevice _device;
    private readonly WasapiOut _output;
    private readonly WaveFormat _waveFormat;
    private bool _started;
    private bool _disposed;

    public WasapiOutputStream(MMDevice device, int latencyMilliseconds)
    {
        _device = device;
        LatencyMilliseconds = Math.Max(3, latencyMilliseconds);
        try
        {
            _output = WasapiAudioBackend.WithoutSynchronizationContext(
                () => new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: true, LatencyMilliseconds));

            // Before Init this is the device's shared-mode mix format. Render in exactly that format
            // (as 32-bit float) so Windows has nothing to convert.
            var mix = _output.OutputWaveFormat;
            _waveFormat = Formats.IsFloat32(mix) ? mix : new WaveFormatExtensible(mix.SampleRate, 32, mix.Channels);
            Format = new AudioFormat(mix.SampleRate, mix.Channels);
        }
        catch
        {
            _output?.Dispose();
            device.Dispose();
            throw;
        }

        _output.PlaybackStopped += OnPlaybackStopped;
    }

    public AudioFormat Format { get; }

    public int LatencyMilliseconds { get; }

    public event EventHandler<AudioStreamStoppedEventArgs>? Stopped;

    public void Start(AudioRenderCallback callback)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(callback);
        if (_started)
        {
            throw new InvalidOperationException("The output stream has already been started.");
        }

        _output.Init(new RenderProvider(_waveFormat, callback));
        _output.Play();
        _started = true;
    }

    public void Stop() => _output.Stop();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _output.Stop();
        _output.PlaybackStopped -= OnPlaybackStopped;
        _output.Dispose();
        _device.Dispose();
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e) =>
        Stopped?.Invoke(this, new AudioStreamStoppedEventArgs(e.Exception));

    /// <summary>Pulls float samples from the engine straight into NAudio's render buffer.</summary>
    private sealed class RenderProvider(WaveFormat format, AudioRenderCallback callback) : IWaveProvider
    {
        public WaveFormat WaveFormat => format;

        public int Read(byte[] buffer, int offset, int count)
        {
            AudioThread.Register();
            int samples = count / sizeof(float);
            callback(MemoryMarshal.Cast<byte, float>(buffer.AsSpan(offset, samples * sizeof(float))));

            // Returning less than requested (let alone 0) would end playback.
            return count;
        }
    }
}

internal static class Formats
{
    private static readonly Guid IeeeFloatSubType = new("00000003-0000-0010-8000-00aa00389b71");
    private static readonly Guid PcmSubType = new("00000001-0000-0010-8000-00aa00389b71");

    public static bool IsFloat32(WaveFormat format) =>
        format.BitsPerSample == 32 &&
        (format.Encoding == WaveFormatEncoding.IeeeFloat ||
         (format is WaveFormatExtensible extensible && extensible.SubFormat == IeeeFloatSubType));

    public static SampleEncoding GetEncoding(WaveFormat format)
    {
        if (IsFloat32(format))
        {
            return SampleEncoding.Float32;
        }

        bool pcm = format.Encoding == WaveFormatEncoding.Pcm ||
                   (format is WaveFormatExtensible extensible && extensible.SubFormat == PcmSubType);
        if (pcm)
        {
            switch (format.BitsPerSample)
            {
                case 16: return SampleEncoding.Pcm16;
                case 24: return SampleEncoding.Pcm24;
                case 32: return SampleEncoding.Pcm32;
            }
        }

        throw new NotSupportedException($"The device uses an unsupported sample format ({format}).");
    }
}

/// <summary>Raises the priority of the current audio thread once.</summary>
internal static partial class AudioThread
{
    [ThreadStatic]
    private static bool t_registered;

    public static void Register()
    {
        if (t_registered)
        {
            return;
        }

        t_registered = true;
        try
        {
            // Multimedia Class Scheduler Service: the same priority boost pro audio applications get.
            int taskIndex = 0;
            if (AvSetMmThreadCharacteristics("Pro Audio", ref taskIndex) == IntPtr.Zero)
            {
                Thread.CurrentThread.Priority = ThreadPriority.Highest;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Thread.CurrentThread.Priority = ThreadPriority.Highest;
        }
    }

    [LibraryImport("avrt.dll", EntryPoint = "AvSetMmThreadCharacteristicsW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr AvSetMmThreadCharacteristics(string taskName, ref int taskIndex);
}
