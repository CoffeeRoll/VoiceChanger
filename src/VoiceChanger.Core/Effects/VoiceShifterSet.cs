using VoiceChanger.Core.Dsp;

namespace VoiceChanger.Core.Effects;

/// <summary>
/// The voice method setting that every effect shifting a voice offers (and so every node made from
/// one), with the shifters behind it: one per method, made up front so that switching never
/// allocates on the audio thread.
/// </summary>
public sealed class VoiceShifterSet
{
    public const string ParameterId = "method";

    private readonly IVoiceShifter[] _shifters;

    public VoiceShifterSet(int channels, int sampleRate, PitchCorrection? correction = null)
    {
        _shifters = Enum.GetValues<VoiceShiftMethod>()
            .Select(method => VoiceShifter.Create(method, channels, sampleRate))
            .ToArray();
        foreach (var shifter in _shifters)
        {
            shifter.Correction = correction;
        }
    }

    /// <summary>Names of the methods, in <see cref="VoiceShiftMethod"/> order.</summary>
    public static IReadOnlyList<string> MethodNames { get; } = ["Pulse", "Spectral"];

    public VoiceShiftMethod Method { get; private set; }

    /// <summary>The shifter of the selected method.</summary>
    public IVoiceShifter Current => _shifters[(int)Method];

    public IVoiceShifter this[VoiceShiftMethod method] => _shifters[(int)method];

    public double Ratio
    {
        set
        {
            foreach (var shifter in _shifters)
            {
                shifter.Ratio = value;
            }
        }
    }

    public double FormantRatio
    {
        set
        {
            foreach (var shifter in _shifters)
            {
                shifter.FormantRatio = value;
            }
        }
    }

    /// <summary>The setting, for an effect to add to its parameters.</summary>
    public static EffectParameter CreateParameter() => EffectParameter.Choice(
        ParameterId, "Voice method", MethodNames,
        description: "Pulse moves each pulse of your voice: clearest when going down. Spectral rebuilds your voice at the new pitch: smoothest when going up, with a little more delay.")
        .AsButtons();

    /// <summary>Switches to another method, which starts from silence. Returns whether it changed.</summary>
    public bool Select(VoiceShiftMethod method)
    {
        if (method == Method)
        {
            return false;
        }

        Method = method;
        Current.Reset();
        return true;
    }

    public void Reset()
    {
        foreach (var shifter in _shifters)
        {
            shifter.Reset();
        }
    }
}
