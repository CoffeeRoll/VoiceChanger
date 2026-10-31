using VoiceChanger.Core.Effects;

namespace VoiceChanger.Plugins.Tests;

/// <summary>Signals and checks the plug-in tests share.</summary>
internal static class TestAudio
{
    public static float[] Sine(int frames, int channels)
    {
        var samples = new float[frames * channels];
        for (int frame = 0; frame < frames; frame++)
        {
            for (int channel = 0; channel < channels; channel++)
            {
                samples[(frame * channels) + channel] = 0.5f * MathF.Sin((frame * 0.05f) + channel);
            }
        }

        return samples;
    }

    public static float[] Run(AudioEffect effect, float[] input)
    {
        var buffer = (float[])input.Clone();
        effect.Process(buffer);
        return buffer;
    }

    public static void AssertClose(float[] expected, float[] actual, float tolerance)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.True(Math.Abs(expected[i] - actual[i]) <= tolerance, $"Sample {i}: expected {expected[i]}, got {actual[i]}.");
        }
    }
}
