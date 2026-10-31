namespace VoiceChanger.Core.Graph;

/// <summary>
/// Splits a stereo signal into a left and a right track: each channel comes out of an output of its
/// own, on both channels of the wire, so it plays in the middle and goes through effects as a signal
/// of its own (two microphones on the two inputs of one interface, say).
/// <para>
/// In mono (<see cref="Audio.AudioEngineOptions.MonoProcessing"/>), every signal is one channel from the
/// start, mixed down when it is recorded: both outputs then carry that.
/// </para>
/// </summary>
public sealed class StereoSplitNode : AudioNode
{
    public const string Type = "split-stereo";

    public StereoSplitNode()
        : base(Type, "Split Stereo", NodeCategory.Mixing)
    {
        SetPorts(
            [CreatePort("in", "Stereo", PortDirection.Input, 0)],
            [CreatePort("left", "Left", PortDirection.Output, 0), CreatePort("right", "Right", PortDirection.Output, 1)]);
    }

    protected override void ProcessCore(NodeBlock block)
    {
        var input = block.Input(0);
        var left = block.Output(0);
        var right = block.Output(1);
        int channels = block.Channels;
        if (channels == 1)
        {
            input.CopyTo(left);
            input.CopyTo(right);
        }
        else
        {
            for (int i = 0; i < input.Length; i += channels)
            {
                left.Slice(i, channels).Fill(input[i]);
                right.Slice(i, channels).Fill(input[i + 1]);
            }
        }

        Meter.Update(left);
        Meter.Update(right);
    }
}
