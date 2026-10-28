namespace VoiceChanger.Core.Effects;

/// <summary>
/// Stands in for a plug-in that isn't installed (any more). It passes audio through unchanged and
/// keeps everything that was saved for the plug-in, so saving again loses nothing and the plug-in
/// comes back as it was once it is installed again.
/// </summary>
public class MissingEffect : AudioEffect, IStatefulEffect
{
    private readonly byte[]? _state;

    protected MissingEffect(string typeId, string? savedName, IReadOnlyDictionary<string, float>? savedParameters, byte[]? state)
        : base(typeId, $"{(string.IsNullOrWhiteSpace(savedName) ? typeId : savedName)} (missing)")
    {
        SavedName = savedName;
        SavedParameters = new Dictionary<string, float>(savedParameters ?? new Dictionary<string, float>(), StringComparer.OrdinalIgnoreCase);
        _state = state;
    }

    /// <summary>The plug-in's name when it was saved.</summary>
    public string? SavedName { get; }

    /// <summary>The parameter values that were saved, kept for the next save.</summary>
    public IReadOnlyDictionary<string, float> SavedParameters { get; }

    /// <summary>
    /// Whether a saved effect of this type gets a stand-in when it can't be created: plug-ins do (their
    /// type ids have a prefix such as <c>vst3:</c>), the built-in effects don't.
    /// </summary>
    public static bool CanStandIn(string typeId) => typeId.Contains(':', StringComparison.Ordinal);

    /// <summary>
    /// A stand-in for an effect of type <paramref name="typeId"/>. With
    /// <paramref name="hasSidechain"/> it keeps a side-chain input, so wires into it survive.
    /// </summary>
    public static MissingEffect Create(
        string typeId,
        string? savedName,
        IReadOnlyDictionary<string, float>? savedParameters,
        byte[]? state,
        bool hasSidechain = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeId);
        return hasSidechain
            ? new MissingSidechainEffect(typeId, savedName, savedParameters, state)
            : new MissingEffect(typeId, savedName, savedParameters, state);
    }

    public byte[]? SaveState() => _state;

    /// <summary>A stand-in can't take a new state: it only keeps the one it was created with.</summary>
    public bool LoadState(byte[] state) => false;

    protected override void OnPrepare()
    {
    }

    protected override void OnParametersChanged()
    {
    }

    protected override void OnReset()
    {
    }

    protected override void ProcessCore(Span<float> interleaved)
    {
    }

    private sealed class MissingSidechainEffect(string typeId, string? savedName, IReadOnlyDictionary<string, float>? savedParameters, byte[]? state)
        : MissingEffect(typeId, savedName, savedParameters, state), ISidechainEffect
    {
        public string InputName => "Audio";

        public string SidechainName => "Side-chain";

        public void SetSidechain(float[]? samples)
        {
        }
    }
}
