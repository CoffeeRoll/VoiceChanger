namespace VoiceChanger.Core.Effects;

/// <summary>
/// An effect that can use a second signal besides the one it processes, like the carrier of a
/// vocoder. In the node editor its node gets a second input for it; in the effect chain, and while
/// nothing is plugged in, it does without.
/// </summary>
public interface ISidechainEffect
{
    /// <summary>Name of the input with the signal that is processed, e.g. "Voice".</summary>
    string InputName { get; }

    /// <summary>Name of the second input, e.g. "Carrier".</summary>
    string SidechainName { get; }

    /// <summary>
    /// The second signal for the next <see cref="AudioEffect.Process"/> call: interleaved with the
    /// effect's channel count and at least as long as that block, or <c>null</c> when nothing
    /// provides one. Audio thread.
    /// </summary>
    void SetSidechain(float[]? samples);
}
