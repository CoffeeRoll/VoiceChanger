namespace VoiceChanger.Core.Dsp;

/// <summary>Converts interleaved audio between channel layouts.</summary>
public static class ChannelMapper
{
    /// <summary>
    /// Maps <paramref name="frames"/> frames from <paramref name="sourceChannels"/> to <paramref name="destinationChannels"/>.
    /// <list type="bullet">
    /// <item>Any layout to mono: the channels are averaged.</item>
    /// <item>Mono to multi-channel: the signal is copied to the first two (front left/right) channels.</item>
    /// <item>Otherwise: matching channels are copied and the remaining destination channels are silent.</item>
    /// </list>
    /// </summary>
    public static void Map(ReadOnlySpan<float> source, int sourceChannels, Span<float> destination, int destinationChannels, int frames)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sourceChannels, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(destinationChannels, 1);
        if (source.Length < frames * sourceChannels || destination.Length < frames * destinationChannels)
        {
            throw new ArgumentException("Buffers are too small for the requested frame count.");
        }

        if (sourceChannels == destinationChannels)
        {
            source[..(frames * sourceChannels)].CopyTo(destination);
            return;
        }

        if (destinationChannels == 1)
        {
            float scale = 1f / sourceChannels;
            for (int f = 0, s = 0; f < frames; f++)
            {
                float sum = 0;
                for (int ch = 0; ch < sourceChannels; ch++)
                {
                    sum += source[s++];
                }

                destination[f] = sum * scale;
            }

            return;
        }

        if (sourceChannels == 1)
        {
            for (int f = 0, d = 0; f < frames; f++, d += destinationChannels)
            {
                float sample = source[f];
                destination[d] = sample;
                destination[d + 1] = sample;
                destination.Slice(d + 2, destinationChannels - 2).Clear();
            }

            return;
        }

        int copy = Math.Min(sourceChannels, destinationChannels);
        for (int f = 0; f < frames; f++)
        {
            int s = f * sourceChannels;
            int d = f * destinationChannels;
            source.Slice(s, copy).CopyTo(destination.Slice(d, copy));
            destination.Slice(d + copy, destinationChannels - copy).Clear();
        }
    }
}
