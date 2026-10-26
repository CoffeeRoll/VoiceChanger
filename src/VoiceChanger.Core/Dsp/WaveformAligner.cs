namespace VoiceChanger.Core.Dsp;

/// <summary>
/// Helpers for reading power-of-two circular delay lines, and for choosing where a new read tap
/// should start so that its waveform lines up with a tap that is already playing (which makes the
/// cross-fade between them inaudible).
/// </summary>
internal sealed class WaveformAligner
{
    private readonly float[] _reference;
    private readonly float[] _candidates;

    public WaveformAligner(int maxSearch, int maxLength)
    {
        _reference = new float[Math.Max(1, maxLength)];
        _candidates = new float[Math.Max(1, maxSearch) + Math.Max(1, maxLength) + 1];
    }

    /// <summary>
    /// Returns the offset in [0, <paramref name="search"/>] for which the <paramref name="length"/>
    /// frames before delay <c>baseDelay + offset</c> best match (normalised cross-correlation) the
    /// frames before <paramref name="referenceDelay"/>. Returns 0 for silence or tiny ranges.
    /// </summary>
    public int FindOffset(float[] line, int mask, int write, double referenceDelay, double baseDelay, int search, int length)
    {
        length = Math.Min(length, _reference.Length);
        search = Math.Min(search, _candidates.Length - length - 1);
        if (search < 2 || length < 8)
        {
            return 0;
        }

        int referenceStart = write - (int)Math.Round(referenceDelay);
        int candidateStart = write - (int)Math.Round(baseDelay);
        double referenceEnergy = 0;
        for (int j = 0; j < length; j++)
        {
            float value = line[(referenceStart - j) & mask];
            _reference[j] = value;
            referenceEnergy += value * value;
        }

        if (referenceEnergy < 1e-9)
        {
            return 0; // Silence: nothing to align with.
        }

        for (int j = 0; j <= search + length; j++)
        {
            _candidates[j] = line[(candidateStart - j) & mask];
        }

        double energy = 0;
        for (int j = 0; j < length; j++)
        {
            energy += _candidates[j] * _candidates[j];
        }

        int best = 0;
        double bestScore = double.NegativeInfinity;
        for (int offset = 0; offset <= search; offset++)
        {
            double correlation = 0;
            for (int j = 0; j < length; j++)
            {
                correlation += _reference[j] * _candidates[offset + j];
            }

            double score = correlation / Math.Sqrt(energy + 1e-9);
            if (score > bestScore)
            {
                bestScore = score;
                best = offset;
            }

            // Slide the energy window one frame further into the past.
            float leaving = _candidates[offset];
            float entering = _candidates[offset + length];
            energy = Math.Max(0, energy - leaving * leaving + entering * entering);
        }

        return best;
    }

    /// <summary>Reads a fractional delay behind <paramref name="write"/> with Catmull-Rom interpolation.</summary>
    public static float ReadCubic(float[] line, int mask, int write, double delay)
    {
        double position = write - delay;
        int index = (int)Math.Floor(position);
        float t = (float)(position - index);

        float xm1 = line[(index - 1) & mask];
        float x0 = line[index & mask];
        float x1 = line[(index + 1) & mask];
        float x2 = line[(index + 2) & mask];

        float c1 = 0.5f * (x1 - xm1);
        float c2 = xm1 - 2.5f * x0 + 2f * x1 - 0.5f * x2;
        float c3 = 0.5f * (x2 - xm1) + 1.5f * (x0 - x1);
        return ((c3 * t + c2) * t + c1) * t + x0;
    }
}
