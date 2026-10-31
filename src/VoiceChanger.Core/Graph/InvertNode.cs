namespace VoiceChanger.Core.Graph;

/// <summary>
/// Turns the signal upside down (inverts its polarity). It sounds the same on its own; mixed with
/// the signal it came from, the two cancel out. That fixes a microphone wired the wrong way round,
/// or takes out what two signals have in common.
/// </summary>
public sealed class InvertNode : AudioNode
{
    public const string Type = "invert";

    public InvertNode()
        : base(Type, "Invert", NodeCategory.Effect)
    {
        SetPorts([CreatePort("in", "Audio", PortDirection.Input, 0)], [CreatePort("out", "Audio", PortDirection.Output, 0)]);
    }

    protected override void ProcessCore(NodeBlock block)
    {
        var input = block.Input(0);
        var output = block.Output(0);
        for (int i = 0; i < input.Length; i++)
        {
            output[i] = -input[i];
        }

        Meter.Update(output);
    }
}
