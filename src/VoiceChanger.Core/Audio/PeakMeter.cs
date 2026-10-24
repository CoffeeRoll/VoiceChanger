namespace VoiceChanger.Core.Audio;

/// <summary>Collects the absolute peak of audio blocks on the audio thread for display on the UI thread.</summary>
public sealed class PeakMeter
{
    private float _peak;

    public void Update(ReadOnlySpan<float> samples)
    {
        float peak = 0;
        foreach (float sample in samples)
        {
            float magnitude = Math.Abs(sample);
            if (magnitude > peak)
            {
                peak = magnitude;
            }
        }

        if (peak > Volatile.Read(ref _peak))
        {
            Volatile.Write(ref _peak, peak);
        }
    }

    /// <summary>Returns the highest peak since the previous call and starts a new measurement.</summary>
    public float ReadAndReset() => Interlocked.Exchange(ref _peak, 0f);
}
