using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using VoiceChanger.Core.Dsp;
using VoiceChanger.Core.Effects;

namespace VoiceChanger.App.ViewModels;

/// <summary>One of the twelve notes in the scale strip.</summary>
public sealed partial class NoteChipViewModel(string name) : ObservableObject
{
    public string Name { get; } = name;

    [ObservableProperty]
    public partial bool IsInScale { get; set; }

    /// <summary>The note the voice is being corrected to.</summary>
    [ObservableProperty]
    public partial bool IsTarget { get; set; }

    /// <summary>The note closest to what is being sung.</summary>
    [ObservableProperty]
    public partial bool IsInput { get; set; }
}

/// <summary>Auto-tune card: live pitch readouts, tuner, pitch history and the settings.</summary>
public sealed partial class AutoTuneViewModel : EffectViewModel
{
    /// <summary>About five seconds of history at the 30 Hz refresh rate.</summary>
    public const int HistoryLength = 150;

    private const int StaleTicks = 6;

    private readonly AutoTuneEffect _autoTune;
    private readonly double[] _inputHistory = new double[HistoryLength];
    private readonly double[] _outputHistory = new double[HistoryLength];
    private long _lastSequence = -1;
    private int _ticksWithoutAudio;

    public AutoTuneViewModel(AutoTuneEffect autoTune, string description)
        : base(autoTune, description)
    {
        _autoTune = autoTune;
        Notes = MusicalScale.NoteNames.Select(n => new NoteChipViewModel(n)).ToList();
        UpdateScale();
        Clear("Press Start to see the pitch of your voice.");
    }

    public IReadOnlyList<NoteChipViewModel> Notes { get; }

    /// <summary>Scale and retune speed; while singing also the live note, e.g. "A3 → A3 · A minor · instant retune".</summary>
    public override string Summary
    {
        get
        {
            var parts = new List<string>();
            if (HasPitch)
            {
                parts.Add($"{InputNote} → {OutputNote}");
            }

            parts.Add(RootPitchClass < 0 ? "Chromatic" : ScaleName);
            parts.Add(_autoTune.RetuneSpeed.Value <= 0 ? "instant retune" : _autoTune.RetuneSpeed.FormattedValue + " retune");
            if (_autoTune.Transpose.IntValue != 0)
            {
                parts.Add(_autoTune.Transpose.FormattedValue);
            }

            return string.Join(" · ", parts);
        }
    }

    /// <summary>Pitch classes of the selected key and scale, for the history grid.</summary>
    [ObservableProperty]
    public partial int ScaleMask { get; private set; }

    /// <summary>Pitch class of the key (0 = C), or −1 for the chromatic scale which has no key.</summary>
    [ObservableProperty]
    public partial int RootPitchClass { get; private set; }

    [ObservableProperty]
    public partial string ScaleName { get; private set; } = "";

    [ObservableProperty]
    public partial bool HasPitch { get; private set; }

    [ObservableProperty]
    public partial string InputNote { get; private set; } = "–";

    [ObservableProperty]
    public partial string InputDetail { get; private set; } = "";

    [ObservableProperty]
    public partial string OutputNote { get; private set; } = "–";

    [ObservableProperty]
    public partial string OutputDetail { get; private set; } = "";

    [ObservableProperty]
    public partial string CorrectionText { get; private set; } = "";

    /// <summary>Distance of the sung pitch from the target note, in cents (−50 … +50).</summary>
    [ObservableProperty]
    public partial double TunerCents { get; private set; }

    /// <summary>Recent sung pitches as fractional MIDI notes (NaN where nothing was sung), oldest first.</summary>
    [ObservableProperty]
    public partial double[] InputHistory { get; private set; } = [];

    /// <summary>Recent corrected pitches as fractional MIDI notes (NaN where nothing was sung), oldest first.</summary>
    [ObservableProperty]
    public partial double[] OutputHistory { get; private set; } = [];

    public override void Tick(bool isRunning)
    {
        if (!isRunning)
        {
            Clear("Press Start to see the pitch of your voice.");
            return;
        }

        var reading = _autoTune.Reading;
        _ticksWithoutAudio = reading.Sequence != _lastSequence ? 0 : _ticksWithoutAudio + 1;
        _lastSequence = reading.Sequence;
        bool processing = IsEnabled && _ticksWithoutAudio < StaleTicks;

        if (!processing || !reading.IsVoiced)
        {
            Push(double.NaN, double.NaN);
            ShowNoPitch(processing ? "Sing or speak to see your pitch." : "Turn the effect on to see your pitch.");
            if (IsCollapsed)
            {
                RefreshSummary();
            }

            return;
        }

        Push(reading.InputNote, reading.OutputNote);
        ShowPitch(reading);
        if (IsCollapsed)
        {
            RefreshSummary();
        }
    }

    protected override void OnParameterChanged(object? sender, EventArgs e)
    {
        base.OnParameterChanged(sender, e);
        UpdateScale();
    }

    private void ShowPitch(AutoTuneReading reading)
    {
        var culture = CultureInfo.CurrentCulture;
        int sungNote = (int)Math.Round(reading.InputNote);
        int scaleTarget = reading.TargetNote - _autoTune.Transpose.IntValue;
        double sungCents = (reading.InputNote - sungNote) * 100;
        double shift = reading.OutputNote - reading.InputNote;

        HasPitch = true;
        InputNote = MusicalScale.NoteName(sungNote);
        InputDetail = string.Create(culture, $"{reading.InputFrequency:0.0} Hz · {sungCents:+0;-0;0} cents");
        OutputNote = MusicalScale.NoteName(reading.TargetNote);
        OutputDetail = string.Create(culture, $"{MusicalScale.NoteToFrequency(reading.OutputNote, reading.ReferenceA4):0.0} Hz");
        CorrectionText = Math.Abs(shift) < 1
            ? string.Create(culture, $"{shift * 100:+0;-0;0} cents")
            : string.Create(culture, $"{shift:+0.0;-0.0} semitones");
        TunerCents = Math.Clamp((reading.InputNote - scaleTarget) * 100, -50, 50);

        for (int i = 0; i < Notes.Count; i++)
        {
            Notes[i].IsInput = i == MusicalScale.PitchClass(sungNote);
            Notes[i].IsTarget = i == MusicalScale.PitchClass(scaleTarget);
        }
    }

    private void ShowNoPitch(string message)
    {
        HasPitch = false;
        InputNote = "–";
        OutputNote = "–";
        InputDetail = message;
        OutputDetail = "";
        CorrectionText = "";
        TunerCents = 0;
        foreach (var note in Notes)
        {
            note.IsInput = false;
            note.IsTarget = false;
        }
    }

    private void Clear(string message)
    {
        Array.Fill(_inputHistory, double.NaN);
        Array.Fill(_outputHistory, double.NaN);
        InputHistory = (double[])_inputHistory.Clone();
        OutputHistory = (double[])_outputHistory.Clone();
        _lastSequence = -1;
        ShowNoPitch(message);
    }

    private void Push(double input, double output)
    {
        Array.Copy(_inputHistory, 1, _inputHistory, 0, HistoryLength - 1);
        Array.Copy(_outputHistory, 1, _outputHistory, 0, HistoryLength - 1);
        _inputHistory[^1] = input;
        _outputHistory[^1] = output;

        // New arrays so the graph sees a changed value and redraws.
        InputHistory = (double[])_inputHistory.Clone();
        OutputHistory = (double[])_outputHistory.Clone();
    }

    private void UpdateScale()
    {
        int mask = _autoTune.ScaleMask;
        bool chromatic = (ScaleType)_autoTune.Scale.IntValue == ScaleType.Chromatic;
        ScaleMask = mask;
        RootPitchClass = chromatic ? -1 : _autoTune.Key.IntValue;
        ScaleName = chromatic
            ? "Chromatic (all notes)"
            : $"{MusicalScale.NoteNames[_autoTune.Key.IntValue]} {MusicalScale.ScaleNames[_autoTune.Scale.IntValue].ToLowerInvariant()}";
        for (int i = 0; i < Notes.Count; i++)
        {
            Notes[i].IsInScale = MusicalScale.IsInScale(i, mask);
        }
    }
}
