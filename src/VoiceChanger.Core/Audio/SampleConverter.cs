using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace VoiceChanger.Core.Audio;

/// <summary>Raw sample encodings delivered by audio devices.</summary>
public enum SampleEncoding
{
    Float32,
    Pcm16,
    Pcm24,
    Pcm32,
}

/// <summary>Converts little-endian device samples to 32-bit float in the range -1..1.</summary>
public static class SampleConverter
{
    public static int BytesPerSample(SampleEncoding encoding) => encoding switch
    {
        SampleEncoding.Pcm16 => 2,
        SampleEncoding.Pcm24 => 3,
        _ => 4,
    };

    /// <summary>Converts as many whole samples as fit and returns the number of samples written.</summary>
    public static int ToFloat(ReadOnlySpan<byte> source, SampleEncoding encoding, Span<float> destination)
    {
        int size = BytesPerSample(encoding);
        int count = Math.Min(source.Length / size, destination.Length);

        switch (encoding)
        {
            case SampleEncoding.Float32:
                MemoryMarshal.Cast<byte, float>(source[..(count * 4)]).CopyTo(destination);
                break;
            case SampleEncoding.Pcm16:
                for (int i = 0; i < count; i++)
                {
                    destination[i] = BinaryPrimitives.ReadInt16LittleEndian(source.Slice(i * 2, 2)) / 32768f;
                }

                break;
            case SampleEncoding.Pcm24:
                for (int i = 0, b = 0; i < count; i++, b += 3)
                {
                    // Assemble in the top 24 bits so the sign extends naturally.
                    int value = (source[b] << 8) | (source[b + 1] << 16) | (source[b + 2] << 24);
                    destination[i] = value / 2147483648f;
                }

                break;
            case SampleEncoding.Pcm32:
                for (int i = 0; i < count; i++)
                {
                    destination[i] = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(i * 4, 4)) / 2147483648f;
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(encoding), encoding, null);
        }

        return count;
    }
}
