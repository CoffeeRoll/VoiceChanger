namespace VoiceChanger.Core.Graph;

/// <summary>A gain that glides to new values over one block, so moving a fader never clicks.</summary>
internal struct SmoothedGain
{
    private float _current;
    private bool _initialized;

    public readonly float Current => _current;

    /// <summary>Jumps to <paramref name="gain"/> (used when nothing is playing yet).</summary>
    public void Reset(float gain)
    {
        _current = gain;
        _initialized = true;
    }

    /// <summary>Writes (or with <paramref name="accumulate"/> adds) <paramref name="source"/> × gain to <paramref name="destination"/>.</summary>
    public void Apply(ReadOnlySpan<float> source, Span<float> destination, float target, int channels, bool accumulate)
    {
        if (!_initialized)
        {
            Reset(target);
        }

        int frames = source.Length / channels;
        float start = _current;
        float step = frames > 0 ? (target - start) / frames : 0;
        _current = target;

        if (step == 0)
        {
            if (accumulate)
            {
                for (int i = 0; i < source.Length; i++)
                {
                    destination[i] += source[i] * target;
                }
            }
            else
            {
                for (int i = 0; i < source.Length; i++)
                {
                    destination[i] = source[i] * target;
                }
            }

            return;
        }

        for (int frame = 0, i = 0; frame < frames; frame++)
        {
            float gain = start + step * (frame + 1);
            for (int ch = 0; ch < channels; ch++, i++)
            {
                destination[i] = accumulate ? destination[i] + source[i] * gain : source[i] * gain;
            }
        }
    }

    public static float DbToGain(float db) => MathF.Pow(10, db / 20);
}
