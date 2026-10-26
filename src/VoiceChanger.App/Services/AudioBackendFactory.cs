using VoiceChanger.Audio.Wasapi;
using VoiceChanger.Core.Audio;

namespace VoiceChanger.App.Services;

/// <summary>Picks the audio API for the current operating system.</summary>
public static class AudioBackendFactory
{
    public static IAudioBackend Create() =>
        OperatingSystem.IsWindows() ? new WasapiAudioBackend() : new NullAudioBackend();
}
