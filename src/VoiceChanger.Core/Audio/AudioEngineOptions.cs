namespace VoiceChanger.Core.Audio;

/// <summary>Trade-off between delay and robustness against crackles.</summary>
public enum LatencyPreset
{
    UltraLow,
    Low,
    Balanced,
    Safe,
}

/// <summary>Settings used when the audio engine opens the devices.</summary>
public sealed record AudioEngineOptions
{
    /// <summary>Capture buffer requested from the input device.</summary>
    public int CaptureBufferMilliseconds { get; init; } = 10;

    /// <summary>Playback buffer requested from the output device.</summary>
    public int OutputLatencyMilliseconds { get; init; } = 20;

    /// <summary>Extra audio kept between input and output to absorb scheduling jitter.</summary>
    public int SafetyBufferMilliseconds { get; init; } = 5;

    /// <summary>Mix the input down to one channel before processing (recommended for voice; halves CPU use).</summary>
    public bool MonoProcessing { get; init; } = true;

    public static AudioEngineOptions FromPreset(LatencyPreset preset, bool monoProcessing = true) => preset switch
    {
        LatencyPreset.UltraLow => new() { CaptureBufferMilliseconds = 10, OutputLatencyMilliseconds = 10, SafetyBufferMilliseconds = 2, MonoProcessing = monoProcessing },
        LatencyPreset.Low => new() { CaptureBufferMilliseconds = 10, OutputLatencyMilliseconds = 20, SafetyBufferMilliseconds = 5, MonoProcessing = monoProcessing },
        LatencyPreset.Balanced => new() { CaptureBufferMilliseconds = 20, OutputLatencyMilliseconds = 40, SafetyBufferMilliseconds = 10, MonoProcessing = monoProcessing },
        LatencyPreset.Safe => new() { CaptureBufferMilliseconds = 30, OutputLatencyMilliseconds = 80, SafetyBufferMilliseconds = 20, MonoProcessing = monoProcessing },
        _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, null),
    };
}

/// <summary>Snapshot of the engine's state for display.</summary>
public readonly record struct AudioEngineStatus(
    bool IsRunning,
    AudioFormat InputFormat,
    AudioFormat OutputFormat,
    int ProcessingChannels,
    float InputPeak,
    float OutputPeak,
    double BufferedMilliseconds,
    double EstimatedLatencyMilliseconds,
    int Underruns,
    double DriftCorrectionPpm);
