using CommunityToolkit.Mvvm.ComponentModel;
using VoiceChanger.Core.Effects;

namespace VoiceChanger.App.ViewModels;

/// <summary>Graphic equalizer card: band sliders, filters and a live response curve.</summary>
public sealed partial class EqualizerViewModel : EffectViewModel
{
    private const int CurvePoints = 160;
    private const double MinFrequency = 20;
    private const double MaxFrequency = 20000;

    private readonly EqualizerEffect _equalizer;

    public EqualizerViewModel(EqualizerEffect equalizer, string description)
        : base(equalizer, description)
    {
        _equalizer = equalizer;
        Bands = equalizer.Bands.Select(Find).ToList();
        Filters = [Find(equalizer.LowCut), Find(equalizer.HighCut), Find(equalizer.Q), Find(equalizer.OutputGain)];

        ResponseFrequencies = new double[CurvePoints];
        for (int i = 0; i < CurvePoints; i++)
        {
            ResponseFrequencies[i] = MinFrequency * Math.Pow(MaxFrequency / MinFrequency, i / (CurvePoints - 1.0));
        }

        UpdateResponse();
    }

    public IReadOnlyList<ParameterViewModel> Bands { get; }

    public IReadOnlyList<ParameterViewModel> Filters { get; }

    public double[] ResponseFrequencies { get; }

    /// <summary>Magnitude response in dB at <see cref="ResponseFrequencies"/>.</summary>
    [ObservableProperty]
    public partial double[] Response { get; private set; } = [];

    protected override void OnParameterChanged(object? sender, EventArgs e)
    {
        base.OnParameterChanged(sender, e);
        UpdateResponse();
    }

    private void UpdateResponse()
    {
        double sampleRate = _equalizer.IsPrepared ? _equalizer.SampleRate : 48000;
        var response = new double[CurvePoints];
        _equalizer.GetResponse(ResponseFrequencies, response, sampleRate);
        Response = response;
    }

    private ParameterViewModel Find(EffectParameter parameter) => Parameters.First(p => p.Parameter == parameter);
}
