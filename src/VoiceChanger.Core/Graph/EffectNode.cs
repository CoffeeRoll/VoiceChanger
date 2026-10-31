using VoiceChanger.Core.Effects;

namespace VoiceChanger.Core.Graph;

/// <summary>
/// Runs one <see cref="AudioEffect"/>; switching it on or off cross-fades like in the effect chain.
/// An effect that can use a second signal (<see cref="ISidechainEffect"/>, like the vocoder's
/// carrier) gets a second input for it.
/// </summary>
public sealed class EffectNode : AudioNode
{
    /// <summary>Effect nodes are saved as this prefix followed by the effect's type id.</summary>
    public const string TypePrefix = "effect:";

    /// <summary>Id of the second input of an effect that has a side-chain.</summary>
    public const string SidechainPort = "sidechain";

    private float[] _sidechain = [];

    public EffectNode(AudioEffect effect)
        : base(TypePrefix + (effect ?? throw new ArgumentNullException(nameof(effect))).TypeId, effect.Name, NodeCategory.Effect)
    {
        Effect = effect;
        Chain.Add(effect);
        NodePort[] inputs = effect is ISidechainEffect sidechain
            ?
            [
                CreatePort("in", sidechain.InputName, PortDirection.Input, 0),
                CreatePort(SidechainPort, sidechain.SidechainName, PortDirection.Input, 1),
            ]
            : [CreatePort("in", "Audio", PortDirection.Input, 0)];
        SetPorts(inputs, [CreatePort("out", "Audio", PortDirection.Output, 0)]);
    }

    public AudioEffect Effect { get; }

    public override IReadOnlyList<EffectParameter> Parameters => Effect.Parameters;

    public override int LatencyFrames => Chain.LatencyFrames;

    /// <summary>A chain of just this effect: it provides the click-free bypass. Its master switch is the graph's.</summary>
    internal EffectChain Chain { get; } = new();

    protected override void OnPrepare()
    {
        Chain.Prepare(SampleRate, Channels);
        _sidechain = Effect is ISidechainEffect ? new float[GraphPlan.BlockFrames * Channels] : [];
    }

    protected override void ProcessCore(NodeBlock block)
    {
        var output = block.Output(0);
        block.Input(0).CopyTo(output);
        if (Effect is ISidechainEffect sidechain)
        {
            // Blocks never exceed the chain's block size, so the effect gets the whole block in one go.
            bool connected = block.InputCount > 1 && block.IsConnected(1);
            if (connected)
            {
                block.Input(1).CopyTo(_sidechain);
            }

            sidechain.SetSidechain(connected ? _sidechain : null);
        }

        Chain.Process(output);
        foreach (float sample in output)
        {
            if (!float.IsFinite(sample))
            {
                // Never pass garbage on (should not happen); start the effect afresh instead.
                output.Clear();
                Chain.ResetEffects();
                break;
            }
        }

        Meter.Update(output);
    }
}
