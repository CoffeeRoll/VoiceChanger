using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoiceChanger.Core.Effects;

namespace VoiceChanger.App.ViewModels;

/// <summary>One of the octave buttons (−2 … +2).</summary>
public sealed partial class OctaveOptionViewModel(int octaves, IRelayCommand select) : ObservableObject
{
    public int Octaves { get; } = octaves;

    public string Label { get; } = octaves == 0 ? "0" : octaves.ToString("+0;-0", System.Globalization.CultureInfo.InvariantCulture);

    public string Description { get; } = octaves switch
    {
        0 => "Original pitch",
        > 0 => $"{octaves} octave{(octaves == 1 ? "" : "s")} up",
        _ => $"{-octaves} octave{(octaves == -1 ? "" : "s")} down",
    };

    public IRelayCommand SelectCommand { get; } = select;

    /// <summary>Whether the pitch is currently exactly this many octaves.</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

/// <summary>Pitch shifter card: octave buttons plus every parameter.</summary>
public sealed class PitchShiftViewModel : EffectViewModel
{
    private readonly PitchShiftEffect _pitchShift;

    public PitchShiftViewModel(PitchShiftEffect pitchShift, string description)
        : base(pitchShift, description)
    {
        _pitchShift = pitchShift;
        Octaves = Enumerable.Range(-2, 5)
            .Select(octaves => new OctaveOptionViewModel(octaves, new RelayCommand(() => SelectOctave(octaves))))
            .ToList();
        UpdateOctaves();
    }

    public IReadOnlyList<OctaveOptionViewModel> Octaves { get; }

    /// <summary>Sets the pitch to exactly this many octaves up (positive) or down (negative).</summary>
    public void SelectOctave(int octaves) => _pitchShift.Semitones.Value = 12 * octaves;

    protected override void OnParameterChanged(object? sender, EventArgs e)
    {
        base.OnParameterChanged(sender, e);
        UpdateOctaves();
    }

    private void UpdateOctaves()
    {
        int semitones = _pitchShift.Semitones.IntValue;
        foreach (var option in Octaves)
        {
            option.IsSelected = semitones == 12 * option.Octaves;
        }
    }
}
