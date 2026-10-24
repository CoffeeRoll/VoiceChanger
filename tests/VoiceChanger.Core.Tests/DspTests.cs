using VoiceChanger.Core.Dsp;
using static VoiceChanger.Core.Tests.SignalHelpers;

namespace VoiceChanger.Core.Tests;

public class BiquadTests
{
    [Theory]
    [InlineData(6.0)]
    [InlineData(-9.0)]
    public void Peaking_filter_has_requested_gain_at_centre(double gainDb)
    {
        var c = BiquadCoefficients.Design(BiquadType.Peaking, 48000, 1000, 1.4, gainDb);
        double measured = 20 * Math.Log10(c.MagnitudeAt(1000, 48000));
        Assert.Equal(gainDb, measured, 2);

        // Far away from the band the response is flat.
        Assert.Equal(0, 20 * Math.Log10(c.MagnitudeAt(50, 48000)), 0);
    }

    [Fact]
    public void Processing_a_sine_matches_the_designed_magnitude()
    {
        var filter = new BiquadFilter(2)
        {
            Coefficients = BiquadCoefficients.Design(BiquadType.Peaking, 48000, 1000, 1.4, 6),
        };
        var signal = Sine(1000, 48000, 48000, channels: 2, amplitude: 0.1);
        filter.Process(signal);

        var left = Channel(signal, 2, 0).AsSpan(24000);
        var right = Channel(signal, 2, 1).AsSpan(24000);
        double expected = 0.1 / Math.Sqrt(2) * Math.Pow(10, 6 / 20.0);
        Assert.Equal(expected, Rms(left), 3);
        Assert.Equal(expected, Rms(right), 3);
    }

    [Fact]
    public void Low_and_high_pass_attenuate_outside_the_pass_band()
    {
        var hp = BiquadCoefficients.Design(BiquadType.HighPass, 48000, 300, 0.7071);
        var lp = BiquadCoefficients.Design(BiquadType.LowPass, 48000, 3000, 0.7071);
        Assert.True(hp.MagnitudeAt(50, 48000) < 0.05);
        Assert.Equal(1, hp.MagnitudeAt(5000, 48000), 2);
        Assert.True(lp.MagnitudeAt(15000, 48000) < 0.05);
        Assert.Equal(1, lp.MagnitudeAt(200, 48000), 2);
    }

    [Fact]
    public void Shelves_reach_their_gain_at_the_extremes()
    {
        var low = BiquadCoefficients.Design(BiquadType.LowShelf, 48000, 200, 0.7071, 6);
        var high = BiquadCoefficients.Design(BiquadType.HighShelf, 48000, 4000, 0.7071, -6);
        Assert.Equal(6, 20 * Math.Log10(low.MagnitudeAt(20, 48000)), 1);
        Assert.Equal(0, 20 * Math.Log10(low.MagnitudeAt(10000, 48000)), 1);
        Assert.Equal(-6, 20 * Math.Log10(high.MagnitudeAt(20000, 48000)), 1);
        Assert.Equal(0, 20 * Math.Log10(high.MagnitudeAt(100, 48000)), 1);
    }
}

public class FftTests
{
    [Fact]
    public void Forward_then_inverse_round_trips()
    {
        var random = new Random(1);
        var re = Enumerable.Range(0, 256).Select(_ => (float)random.NextDouble() - 0.5f).ToArray();
        var im = new float[256];
        var original = (float[])re.Clone();

        var fft = new Fft(256);
        fft.Forward(re, im);
        fft.Inverse(re, im);

        for (int i = 0; i < 256; i++)
        {
            Assert.Equal(original[i], re[i] / 256, 4);
            Assert.Equal(0, im[i] / 256, 4);
        }
    }

    [Fact]
    public void Sine_lands_in_the_expected_bin()
    {
        var re = Sine(1000, 16000, 64, amplitude: 1);
        var im = new float[64];
        new Fft(64).Forward(re, im);

        // 1000 Hz at 16 kHz with 64 points is exactly bin 4.
        double magnitude4 = Math.Sqrt(re[4] * re[4] + im[4] * im[4]);
        Assert.Equal(32, magnitude4, 2);
        Assert.True(Math.Sqrt(re[10] * re[10] + im[10] * im[10]) < 1e-3);
    }

    [Fact]
    public void Rejects_sizes_that_are_not_powers_of_two() =>
        Assert.Throws<ArgumentException>(() => new Fft(100));
}

public class FloatRingBufferTests
{
    [Fact]
    public void Wraps_around_and_preserves_order()
    {
        var ring = new FloatRingBuffer(8);
        Assert.Equal(6, ring.Write([1, 2, 3, 4, 5, 6]));
        var read = new float[4];
        Assert.Equal(4, ring.Read(read));
        Assert.Equal([1f, 2, 3, 4], read);

        Assert.Equal(6, ring.Write([7, 8, 9, 10, 11, 12]));
        Assert.Equal(8, ring.Count);
        Assert.Equal(0, ring.Write([13]));

        var all = new float[8];
        Assert.Equal(8, ring.Read(all));
        Assert.Equal([5f, 6, 7, 8, 9, 10, 11, 12], all);
        Assert.Equal(0, ring.Count);
    }

    [Fact]
    public void Peek_and_skip_do_not_disturb_the_data()
    {
        var ring = new FloatRingBuffer(16);
        ring.Write([1, 2, 3, 4, 5]);
        var peek = new float[2];
        Assert.Equal(2, ring.Peek(2, peek));
        Assert.Equal([3f, 4], peek);
        Assert.Equal(5, ring.Count);

        Assert.Equal(3, ring.Skip(3));
        var rest = new float[5];
        Assert.Equal(2, ring.Read(rest));
        Assert.Equal(4, rest[0]);
        Assert.Equal(0, ring.Skip(10));
    }

    [Fact]
    public async Task Concurrent_producer_and_consumer_see_every_sample_in_order()
    {
        var ring = new FloatRingBuffer(1000);
        const int total = 2_000_000;

        var producer = Task.Run(() =>
        {
            var chunk = new float[97];
            int next = 0;
            while (next < total)
            {
                int count = Math.Min(chunk.Length, total - next);
                for (int i = 0; i < count; i++)
                {
                    chunk[i] = next + i;
                }

                int written = ring.Write(chunk.AsSpan(0, count));
                next += written;
                if (written == 0)
                {
                    Thread.Yield();
                }
            }
        }, TestContext.Current.CancellationToken);

        var consumer = Task.Run(() =>
        {
            var chunk = new float[61];
            int expected = 0;
            while (expected < total)
            {
                int read = ring.Read(chunk);
                for (int i = 0; i < read; i++)
                {
                    if (chunk[i] != expected++)
                    {
                        return false;
                    }
                }

                if (read == 0)
                {
                    Thread.Yield();
                }
            }

            return true;
        }, TestContext.Current.CancellationToken);

        await producer.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.True(await consumer.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
    }
}

public class ResamplerTests
{
    [Theory]
    [InlineData(44100, 48000)]
    [InlineData(48000, 44100)]
    [InlineData(16000, 48000)]
    [InlineData(48000, 48000)]
    public void Preserves_frequency_and_level(int inputRate, int outputRate)
    {
        var resampler = new Resampler(inputRate, outputRate, 1);
        var input = Sine(1000, inputRate, inputRate * 2, amplitude: 0.5);
        var output = new List<float>();
        var block = new float[resampler.MaxOutputFrames(480)];

        for (int offset = 0; offset < input.Length; offset += 480)
        {
            int frames = Math.Min(480, input.Length - offset);
            int produced = resampler.Process(input.AsSpan(offset, frames), block);
            output.AddRange(block.AsSpan(0, produced));
        }

        var result = output.ToArray();
        // Everything except the filter's look-ahead comes out.
        double expectedFrames = (input.Length - resampler.LatencyFrames) * (double)outputRate / inputRate;
        Assert.InRange(result.Length, expectedFrames - 2, expectedFrames + 2);

        var steady = result.AsSpan(outputRate / 2);
        Assert.Equal(1000, DominantFrequency(steady, outputRate), 0);
        Assert.Equal(0.5 / Math.Sqrt(2), Rms(steady), 2);
    }

    [Fact]
    public void Same_rate_conversion_is_a_pure_delay()
    {
        var resampler = new Resampler(48000, 48000, 2);
        var random = new Random(3);
        var input = Enumerable.Range(0, 2000).Select(_ => (float)(random.NextDouble() - 0.5)).ToArray();
        var output = new float[resampler.MaxOutputFrames(1000) * 2];
        int produced = resampler.Process(input, output);

        int delay = resampler.LatencyFrames;
        Assert.Equal(1000 - delay, produced);
        for (int i = 0; i < produced * 2; i++)
        {
            Assert.Equal(input[i], output[i], 4);
        }
    }

    [Fact]
    public void Step_adjustment_changes_the_output_rate_slightly()
    {
        var normal = new Resampler(48000, 48000, 1);
        var faster = new Resampler(48000, 48000, 1) { StepAdjustment = 1.001 };
        var input = Sine(440, 48000, 48000);
        var buffer = new float[normal.MaxOutputFrames(48000)];

        int normalFrames = normal.Process(input, buffer);
        int fasterFrames = faster.Process(input, buffer);

        Assert.InRange(normalFrames - fasterFrames, 46, 50);
    }
}

public class ChannelMapperTests
{
    [Fact]
    public void Stereo_to_mono_averages()
    {
        var mono = new float[2];
        ChannelMapper.Map([1f, 0f, 0.5f, 0.5f], 2, mono, 1, 2);
        Assert.Equal([0.5f, 0.5f], mono);
    }

    [Fact]
    public void Mono_to_surround_feeds_front_left_and_right()
    {
        var surround = new float[12];
        ChannelMapper.Map([0.25f, -0.5f], 1, surround, 6, 2);
        Assert.Equal([0.25f, 0.25f, 0, 0, 0, 0, -0.5f, -0.5f, 0, 0, 0, 0], surround);
    }

    [Fact]
    public void Multi_channel_copies_matching_channels()
    {
        var stereo = new float[4];
        ChannelMapper.Map([1f, 2, 3, 4, 5, 6, 7, 8], 4, stereo, 2, 2);
        Assert.Equal([1f, 2, 5, 6], stereo);

        var quad = new float[8];
        ChannelMapper.Map([1f, 2, 3, 4], 2, quad, 4, 2);
        Assert.Equal([1f, 2, 0, 0, 3, 4, 0, 0], quad);
    }
}

public class SampleConverterTests
{
    [Fact]
    public void Converts_pcm16()
    {
        byte[] bytes = [0x00, 0x40, 0x00, 0xC0, 0xFF, 0x7F, 0x00, 0x80];
        var result = new float[4];
        Assert.Equal(4, Audio.SampleConverter.ToFloat(bytes, Audio.SampleEncoding.Pcm16, result));
        Assert.Equal([0.5f, -0.5f, 32767 / 32768f, -1f], result);
    }

    [Fact]
    public void Converts_pcm24_with_sign_extension()
    {
        byte[] bytes = [0x00, 0x00, 0x40, 0x00, 0x00, 0xC0, 0x01, 0x00, 0x00];
        var result = new float[3];
        Assert.Equal(3, Audio.SampleConverter.ToFloat(bytes, Audio.SampleEncoding.Pcm24, result));
        Assert.Equal(0.5f, result[0]);
        Assert.Equal(-0.5f, result[1]);
        Assert.Equal(1f / 8388608f, result[2], 9);
    }

    [Fact]
    public void Converts_pcm32_and_float()
    {
        var pcm = BitConverter.GetBytes(int.MinValue / 4).Concat(BitConverter.GetBytes(int.MaxValue)).ToArray();
        var result = new float[2];
        Audio.SampleConverter.ToFloat(pcm, Audio.SampleEncoding.Pcm32, result);
        Assert.Equal(-0.25f, result[0]);
        Assert.Equal(1f, result[1], 6);

        var floats = BitConverter.GetBytes(0.125f).Concat(BitConverter.GetBytes(-0.75f)).ToArray();
        Audio.SampleConverter.ToFloat(floats, Audio.SampleEncoding.Float32, result);
        Assert.Equal([0.125f, -0.75f], result);
    }

    [Fact]
    public void Ignores_partial_samples()
    {
        var result = new float[4];
        Assert.Equal(1, Audio.SampleConverter.ToFloat(new byte[5], Audio.SampleEncoding.Pcm24, result));
    }
}
