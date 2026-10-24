using VoiceChanger.Core.Dsp;

namespace VoiceChanger.Core.Tests;

internal static class SignalHelpers
{
    public static float[] Sine(double frequency, int sampleRate, int frames, int channels = 1, double amplitude = 0.5)
    {
        var buffer = new float[frames * channels];
        for (int i = 0; i < frames; i++)
        {
            float value = (float)(amplitude * Math.Sin(2 * Math.PI * frequency * i / sampleRate));
            for (int ch = 0; ch < channels; ch++)
            {
                buffer[i * channels + ch] = value;
            }
        }

        return buffer;
    }

    public static float[] Channel(ReadOnlySpan<float> interleaved, int channels, int channel)
    {
        var result = new float[interleaved.Length / channels];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = interleaved[i * channels + channel];
        }

        return result;
    }

    public static double Rms(ReadOnlySpan<float> samples)
    {
        double sum = 0;
        foreach (float s in samples)
        {
            sum += s * s;
        }

        return Math.Sqrt(sum / Math.Max(1, samples.Length));
    }

    /// <summary>Frequency of the strongest spectral peak (Hann window, parabolic interpolation).</summary>
    public static double DominantFrequency(ReadOnlySpan<float> samples, int sampleRate)
    {
        int size = 1;
        while (size * 2 <= samples.Length && size < 65536)
        {
            size *= 2;
        }

        var real = new float[size];
        var imag = new float[size];
        for (int i = 0; i < size; i++)
        {
            double window = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / size);
            real[i] = (float)(samples[samples.Length - size + i] * window);
        }

        new Fft(size).Forward(real, imag);

        int best = 1;
        double bestMagnitude = 0;
        var magnitudes = new double[size / 2];
        for (int k = 1; k < size / 2; k++)
        {
            magnitudes[k] = Math.Sqrt(real[k] * real[k] + imag[k] * imag[k]);
            if (magnitudes[k] > bestMagnitude)
            {
                bestMagnitude = magnitudes[k];
                best = k;
            }
        }

        double offset = 0;
        if (best > 1 && best < size / 2 - 1)
        {
            double a = Math.Log(magnitudes[best - 1] + 1e-12);
            double b = Math.Log(magnitudes[best] + 1e-12);
            double c = Math.Log(magnitudes[best + 1] + 1e-12);
            offset = 0.5 * (a - c) / (a - 2 * b + c);
        }

        return (best + offset) * sampleRate / size;
    }

    /// <summary>Processes a long buffer in blocks of <paramref name="blockFrames"/>, like an audio callback would.</summary>
    public static void ProcessInBlocks(float[] buffer, int channels, int blockFrames, Action<Memory<float>> process)
    {
        int blockSamples = blockFrames * channels;
        for (int offset = 0; offset < buffer.Length; offset += blockSamples)
        {
            process(buffer.AsMemory(offset, Math.Min(blockSamples, buffer.Length - offset)));
        }
    }
}
