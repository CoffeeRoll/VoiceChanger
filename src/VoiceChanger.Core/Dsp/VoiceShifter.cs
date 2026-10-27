namespace VoiceChanger.Core.Dsp;

/// <summary>
/// Chooses the pitch ratio of a <see cref="IVoiceShifter"/> as the voice goes: for every pulse of
/// the voice (<see cref="VoiceShiftMethod.Pulse"/>) or every analysis frame
/// (<see cref="VoiceShiftMethod.Spectral"/>).
/// </summary>
/// <param name="period">Period of the voice at that point, in samples, or 0 for unvoiced audio.</param>
/// <param name="time">Position in the stream, in samples.</param>
/// <returns>Output pitch divided by input pitch from there on.</returns>
public delegate double PitchCorrection(double period, double time);

/// <summary>
/// The ways a voice can be shifted while keeping its character (its formants). Every effect that
/// shifts a voice offers the same choice.
/// </summary>
public enum VoiceShiftMethod
{
    /// <summary>
    /// Moves each pulse of the voice (<see cref="PsolaPitchShifter"/>). Clearest going down, follows
    /// the voice pulse by pulse, and leaves breath as it is.
    /// </summary>
    Pulse,

    /// <summary>
    /// Rebuilds the spectrum at the new pitch under the voice's own envelope
    /// (<see cref="SpectralVoiceShifter"/>). Smoothest going up, with a little more delay.
    /// </summary>
    Spectral,
}

/// <summary>A pitch shifter for voices that keeps the formants in place, or moves them on their own.</summary>
public interface IVoiceShifter
{
    /// <summary>Output pitch divided by input pitch (0.25 to 4).</summary>
    double Ratio { get; set; }

    /// <summary>
    /// When set, chooses the ratio as the voice goes instead of <see cref="Ratio"/>, from the period
    /// of the voice at that point. Runs on the audio thread.
    /// </summary>
    PitchCorrection? Correction { get; set; }

    /// <summary>How far the formants move, as a frequency ratio (1 = they stay put; 0.5 to 2).</summary>
    double FormantRatio { get; set; }

    /// <summary>Delay through the shifter, in frames.</summary>
    int LatencyFrames { get; }

    /// <summary>Shifts interleaved audio in place.</summary>
    void Process(Span<float> interleaved);

    void Reset();
}

public static class VoiceShifter
{
    /// <summary>Makes a shifter for one of the methods.</summary>
    public static IVoiceShifter Create(VoiceShiftMethod method, int channels, int sampleRate) => method switch
    {
        VoiceShiftMethod.Spectral => new SpectralVoiceShifter(channels, sampleRate),
        _ => new PsolaPitchShifter(channels, sampleRate),
    };
}
