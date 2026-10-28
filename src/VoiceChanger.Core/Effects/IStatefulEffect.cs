namespace VoiceChanger.Core.Effects;

/// <summary>
/// An effect whose settings are more than its parameters, like a plug-in with its own saved data.
/// The settings store the state next to the parameter values, and on load the state wins.
/// </summary>
public interface IStatefulEffect
{
    /// <summary>The effect's complete state, or <c>null</c> when it can't be saved right now. UI thread.</summary>
    byte[]? SaveState();

    /// <summary>Restores a state from <see cref="SaveState"/>. Returns whether the effect accepted it. UI thread.</summary>
    bool LoadState(byte[] state);
}
