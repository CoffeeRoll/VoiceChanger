using VoiceChanger.Core.Dsp;
using static VoiceChanger.Core.Tests.SignalHelpers;

namespace VoiceChanger.Core.Tests;

public class PsolaRobustnessTests
{
    /// <summary>Number of times the output went quiet for 50 ms or more while there was sound going in.</summary>
    private static int CountDropouts(float[] input, float[] output, int rate, int latency)
    {
        int block = rate / 100;
        int run = 0, dropouts = 0;
        for (int offset = latency; offset + block <= output.Length; offset += block)
        {
            bool sound = Rms(input.AsSpan(offset - latency, block)) > 0.02;
            bool silent = Rms(output.AsSpan(offset, block)) < 0.002;
            run = sound && silent ? run + 1 : 0;
            if (run == 5)
            {
                dropouts++;
            }
        }

        return dropouts;
    }

    [Theory]
    [InlineData(44100, 2, 2.0)]
    [InlineData(44100, 2, 0.5)]
    [InlineData(48000, 3, 2.0)]
    public void Never_goes_quiet_on_messy_input(int rate, int seed, double ratio)
    {
        // Voices starting right after a long period could once put a mark before the previous one;
        // the marks then stopped being used up and the output stalled for seconds (from 52 s into
        // the first of these sessions).
        var input = RealWorldSession.Generate(rate, 1, seed);
        var shifter = new PsolaPitchShifter(1, rate) { Ratio = ratio };
        var output = (float[])input.Clone();
        ProcessInBlocks(output, 1, rate / 100, block => shifter.Process(block.Span));

        Assert.Equal(0, CountDropouts(input, output, rate, shifter.LatencyFrames));
        Assert.Equal(0, shifter.Resyncs);
        Assert.All(output, s => Assert.True(float.IsFinite(s)));
    }
}
