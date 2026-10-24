using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NAudio.CoreAudioApi;
using VoiceChanger.Core.Audio;

namespace VoiceChanger.Audio.Wasapi;

/// <summary>
/// Audio backend for Windows built on the Windows Audio Session API (WASAPI) in shared mode,
/// with event driven buffers for low latency.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WasapiAudioBackend : IAudioBackend
{
    public string Name => "Windows Audio (WASAPI)";

    public bool IsSupported => true;

    public IReadOnlyList<AudioDeviceInfo> GetDevices(AudioDeviceKind kind)
    {
        var flow = kind == AudioDeviceKind.Input ? DataFlow.Capture : DataFlow.Render;
        using var enumerator = new MMDeviceEnumerator();

        string? defaultId = null;
        try
        {
            if (enumerator.HasDefaultAudioEndpoint(flow, Role.Console))
            {
                using var defaultDevice = enumerator.GetDefaultAudioEndpoint(flow, Role.Console);
                defaultId = defaultDevice.ID;
            }
        }
        catch (COMException)
        {
            // No default device; every device is listed without the flag.
        }

        var devices = new List<AudioDeviceInfo>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            using (device)
            {
                try
                {
                    devices.Add(new AudioDeviceInfo(device.ID, device.FriendlyName, kind, device.ID == defaultId));
                }
                catch (COMException)
                {
                    // A device that disappears while we enumerate is simply skipped.
                }
            }
        }

        return devices
            .OrderByDescending(d => d.IsDefault)
            .ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public IAudioInputStream OpenInput(string deviceId, int bufferMilliseconds) =>
        new WasapiInputStream(OpenDevice(deviceId), bufferMilliseconds);

    public IAudioOutputStream OpenOutput(string deviceId, int latencyMilliseconds) =>
        new WasapiOutputStream(OpenDevice(deviceId), latencyMilliseconds);

    private static MMDevice OpenDevice(string deviceId)
    {
        using var enumerator = new MMDeviceEnumerator();
        MMDevice device;
        try
        {
            device = enumerator.GetDevice(deviceId);
        }
        catch (COMException ex)
        {
            throw new InvalidOperationException("The selected audio device is no longer available. Refresh the device list and pick another one.", ex);
        }

        if (device.State != DeviceState.Active)
        {
            device.Dispose();
            throw new InvalidOperationException("The selected audio device is disabled or unplugged.");
        }

        return device;
    }

    /// <summary>
    /// NAudio posts its "stopped" events to the synchronization context it was created on. Creating
    /// the streams without one makes the events arrive on the audio thread instead, so stopping
    /// never has to wait for the UI thread.
    /// </summary>
    internal static T WithoutSynchronizationContext<T>(Func<T> create)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            return create();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }
}
