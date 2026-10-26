using VoiceChanger.Core.Effects;

namespace VoiceChanger.Core.Graph;

/// <summary>Runs one <see cref="AudioEffect"/>; switching it on or off cross-fades like in the effect chain.</summary>
public sealed class EffectNode : AudioNode
{
    /// <summary>Effect nodes are saved as this prefix followed by the effect's type id.</summary>
    public const string TypePrefix = "effect:";

    public EffectNode(AudioEffect effect)
        : base(TypePrefix + (effect ?? throw new ArgumentNullException(nameof(effect))).TypeId, effect.Name, NodeCategory.Effect)
    {
        Effect = effect;
        Chain.Add(effect);
        SetPorts(
            [CreatePort("in", "Audio", PortDirection.Input, 0)],
            [CreatePort("out", "Audio", PortDirection.Output, 0)]);
    }

    public AudioEffect Effect { get; }

    public override IReadOnlyList<EffectParameter> Parameters => Effect.Parameters;

    public override int LatencyFrames => Chain.LatencyFrames;

    /// <summary>A chain of just this effect: it provides the click-free bypass. Its master switch is the graph's.</summary>
    internal EffectChain Chain { get; } = new();

    protected override void OnPrepare() => Chain.Prepare(SampleRate, Channels);

    protected override void ProcessCore(NodeBlock block)
    {
        var output = block.Output(0);
        block.Input(0).CopyTo(output);
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
