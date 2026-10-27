using System.Globalization;
using VoiceChanger.Core.Dsp;

namespace VoiceChanger.Core.Effects;

/// <summary>What the auto-tune heard and what it is producing, for display.</summary>
/// <param name="Sequence">Increases with every processed block; stops changing while the effect is not running.</param>
/// <param name="IsVoiced">Whether a pitched sound (a sung or spoken vowel) is present.</param>
/// <param name="InputFrequency">Detected pitch in Hz.</param>
/// <param name="InputNote">Detected pitch as a fractional MIDI note (69 = A4).</param>
/// <param name="TargetNote">The note being corrected to, including the transpose.</param>
/// <param name="OutputNote">The pitch currently produced, as a fractional MIDI note.</param>
/// <param name="ReferenceA4">Tuning reference in Hz.</param>
public readonly record struct AutoTuneReading(
    long Sequence,
    bool IsVoiced,
    double InputFrequency,
    double InputNote,
    int TargetNote,
    double OutputNote,
    double ReferenceA4);

/// <summary>
/// Pitch correction: bends the voice to the nearest note of a key and scale. A fast retune speed
/// gives the hard, robotic "auto-tune" sound; a slower one corrects gently and keeps natural vibrato.
/// <para>
/// It runs on the voice methods of the pitch shifter (<see cref="VoiceShifterSet"/>), so
/// transposing keeps the character (formants) of the voice. The pulse method
/// (<see cref="PsolaPitchShifter"/>) measures the period of every glottal pulse it moves and
/// corrects each pulse from its own period, so corrections land exactly on time and a hard tune
/// comes out exactly on the note. The spectral method (<see cref="SpectralVoiceShifter"/>) corrects
/// every few milliseconds from a pitch detector lined up with what it is shifting.
/// </para>
/// </summary>
public sealed class AutoTuneEffect : AudioEffect
{
    public const string Type = "auto-tune";

    /// <summary>C major: the custom scale starts from these notes until others are picked.</summary>
    public const int DefaultCustomNotes = 0b1010_1011_0101;

    private const double HoldSeconds = 0.12;
    private const double ReleaseSeconds = 0.05;
    private const double Hysteresis = 0.2;
    private const int DisplayPeriods = 4;

    private readonly double[] _recentPeriods = new double[DisplayPeriods];
    private VoiceShifterSet? _voices;

    // Settings, refreshed in OnParametersChanged.
    private int _mask = 0xFFF;
    private double _retuneSeconds;
    private double _amount = 1;
    private int _transpose;
    private double _reference = 440;

    // Audio thread state, updated for every grain.
    private int? _heldTarget;
    private double _inputNote;
    private double _tuning;
    private double _applied;
    private double _unvoicedSeconds = HoldSeconds;
    private double _lastGrainTime = double.NaN;
    private bool _voiced;
    private int _recentWrite;
    private int _recentCount;
    private long _sequence;

    // Seqlock-protected reading for the UI thread.
    private int _readingVersion;
    private AutoTuneReading _reading = new(0, false, 0, double.NaN, 0, double.NaN, 440);

    public AutoTuneEffect()
        : base(Type, "Auto Tune")
    {
        Key = AddParameter(EffectParameter.Choice(
            "key", "Key", MusicalScale.NoteNames,
            description: "Root note of the song's key."));
        Scale = AddParameter(EffectParameter.Choice(
            "scale", "Scale", MusicalScale.ScaleNames,
            description: "Notes the voice is pulled to. Chromatic allows all twelve notes and works with any song. Custom uses the notes you pick."));

        // Chromatic and custom scales don't depend on a key.
        Key.AvailableWhen(() => (ScaleType)Scale.IntValue is not (ScaleType.Chromatic or ScaleType.Custom));
        CustomNotes = AddParameter(EffectParameter.Integer(
            "notes", "Custom notes", 1, 0xFFF, DefaultCustomNotes,
            formatter: v => MusicalScale.FormatNotes((int)MathF.Round(v)),
            description: "The notes of the custom scale (one bit per note, C first).")
            .AvailableWhen(() => (ScaleType)Scale.IntValue == ScaleType.Custom));
        RetuneSpeed = AddParameter(EffectParameter.Continuous(
            "retune", "Retune speed", 0, 400, 20, "ms", decimals: 0,
            formatter: v => v <= 0 ? "Instant" : v.ToString("0", CultureInfo.CurrentCulture) + " ms",
            description: "How quickly the pitch is pulled to the note. Instant gives the robotic hard-tune sound; slower keeps natural slides and vibrato."));
        Amount = AddParameter(EffectParameter.Continuous(
            "amount", "Correction", 0, 100, 100, "%", decimals: 0,
            description: "How much of the distance to the note is corrected."));
        Transpose = AddParameter(EffectParameter.Integer(
            "transpose", "Transpose", -12, 12, 0, "st",
            formatter: v => MusicalScale.FormatInterval((int)MathF.Round(v)),
            description: "Moves the corrected notes up or down. Your voice keeps its character, like with the pitch shifter's Preserve formants."));
        VoiceMethod = AddParameter(VoiceShifterSet.CreateParameter());
        Reference = AddParameter(EffectParameter.Integer(
            "reference", "Tuning (A4)", 430, 450, 440, "Hz",
            formatter: v => v.ToString("0", CultureInfo.CurrentCulture) + " Hz",
            description: "Frequency of the A above middle C. Leave at 440 Hz unless the music is tuned differently."));

        // Styles only touch the correction behaviour and keep the chosen key and scale.
        AddPartialPreset("Pop", ("retune", 20), ("amount", 100), ("transpose", 0));
        AddPartialPreset("Hard tune", ("retune", 0), ("amount", 100), ("transpose", 0));
        AddPartialPreset("Natural", ("retune", 80), ("amount", 100), ("transpose", 0));
        AddPartialPreset("Subtle", ("retune", 150), ("amount", 70), ("transpose", 0));
    }

    public EffectParameter Key { get; }

    public EffectParameter Scale { get; }

    /// <summary>Notes of the custom scale as a bit mask (bit 0 = C). Used while <see cref="Scale"/> is <see cref="ScaleType.Custom"/>.</summary>
    public EffectParameter CustomNotes { get; }

    public EffectParameter RetuneSpeed { get; }

    public EffectParameter Amount { get; }

    public EffectParameter Transpose { get; }

    /// <summary>How the voice is shifted (a <see cref="VoiceShiftMethod"/>).</summary>
    public EffectParameter VoiceMethod { get; }

    public EffectParameter Reference { get; }

    /// <summary>Pitch classes of the selected key and scale, or the custom notes (bit 0 = C).</summary>
    public int ScaleMask => (ScaleType)Scale.IntValue == ScaleType.Custom
        ? CustomNotes.IntValue & 0xFFF
        : MusicalScale.ScaleMask((ScaleType)Scale.IntValue, Key.IntValue);

    /// <summary>
    /// Adds a note to the allowed notes or removes it. A preset scale becomes a custom scale with the
    /// same notes first, so picking notes always starts from what is selected. The last note can't be
    /// removed. Returns whether anything changed.
    /// </summary>
    public bool ToggleNote(int pitchClass)
    {
        int mask = ScaleMask ^ (1 << MusicalScale.PitchClass(pitchClass));
        if (mask == 0)
        {
            return false;
        }

        CustomNotes.Value = mask;
        Scale.Value = (int)ScaleType.Custom;
        return true;
    }

    public override int LatencyFrames => _voices?[(VoiceShiftMethod)VoiceMethod.IntValue].LatencyFrames ?? 0;

    /// <summary>The most recent analysis. Safe to call from any thread.</summary>
    public AutoTuneReading Reading
    {
        get
        {
            var spinner = default(SpinWait);
            while (true)
            {
                int before = Volatile.Read(ref _readingVersion);
                if ((before & 1) == 0)
                {
                    var reading = _reading;
                    Interlocked.MemoryBarrier();
                    if (Volatile.Read(ref _readingVersion) == before)
                    {
                        return reading;
                    }
                }

                spinner.SpinOnce();
            }
        }
    }

    protected override void OnPrepare() =>
        _voices = new VoiceShifterSet(Channels, SampleRate, Correct);

    protected override void OnParametersChanged()
    {
        int mask = ScaleMask;
        if (mask != _mask)
        {
            _heldTarget = null;
            _mask = mask;
        }

        if (_voices!.Select((VoiceShiftMethod)VoiceMethod.IntValue))
        {
            // The new shifter keeps its own time, from zero.
            _lastGrainTime = double.NaN;
        }

        _retuneSeconds = RetuneSpeed.Value / 1000.0;
        _amount = Amount.Value / 100.0;
        _transpose = Transpose.IntValue;
        _reference = Reference.Value;
    }

    protected override void OnReset()
    {
        _voices?.Reset();
        _heldTarget = null;
        _tuning = 0;
        _applied = _transpose;
        _unvoicedSeconds = HoldSeconds;
        _lastGrainTime = double.NaN;
        _voiced = false;
        _recentCount = 0;
    }

    protected override void ProcessCore(Span<float> interleaved)
    {
        _voices!.Current.Process(interleaved);
        Publish();
    }

    /// <summary>Chooses the pitch from here on: for one pulse of the voice (or a slice of unvoiced audio), or one frame.</summary>
    private double Correct(double period, double time)
    {
        double seconds = double.IsNaN(_lastGrainTime) ? 0 : Math.Max(0, time - _lastGrainTime) / SampleRate;
        _lastGrainTime = time;
        _voiced = period > 0;
        if (_voiced)
        {
            _inputNote = MusicalScale.FrequencyToNote(SampleRate / period, _reference);
            int target = MusicalScale.Nearest(_inputNote, _mask, _heldTarget, Hysteresis);
            _heldTarget = target;
            _tuning = target - _inputNote;
            _unvoicedSeconds = 0;
            _recentPeriods[_recentWrite] = period;
            _recentWrite = (_recentWrite + 1) % DisplayPeriods;
            _recentCount = Math.Min(_recentCount + 1, DisplayPeriods);
        }
        else
        {
            // Bridge short gaps (consonants); after that, let go of the correction gently.
            _unvoicedSeconds += seconds;
            _recentCount = 0;
            if (_unvoicedSeconds > HoldSeconds)
            {
                _tuning = 0;
                _heldTarget = null;
            }
        }

        double desired = _transpose + _amount * _tuning;
        double timeConstant = _unvoicedSeconds > HoldSeconds ? Math.Max(_retuneSeconds, ReleaseSeconds) : _retuneSeconds;
        double step = timeConstant <= 0 ? 1 : 1 - Math.Exp(-seconds / timeConstant);
        _applied += (desired - _applied) * step;
        return Math.Pow(2, _applied / 12);
    }

    private void Publish()
    {
        // The display shows the pitch averaged over the last few pulses.
        bool voiced = _voiced && _recentCount > 0;
        double period = 0;
        for (int i = 0; i < _recentCount; i++)
        {
            period += _recentPeriods[(_recentWrite - 1 - i + DisplayPeriods) % DisplayPeriods];
        }

        double frequency = voiced ? SampleRate * _recentCount / period : 0;
        var reading = new AutoTuneReading(
            Sequence: ++_sequence,
            IsVoiced: voiced,
            InputFrequency: frequency,
            InputNote: voiced ? MusicalScale.FrequencyToNote(frequency, _reference) : double.NaN,
            TargetNote: voiced && _heldTarget is int target ? target + _transpose : 0,
            OutputNote: voiced ? _inputNote + _applied : double.NaN,
            ReferenceA4: _reference);

        Interlocked.Increment(ref _readingVersion);
        _reading = reading;
        Interlocked.Increment(ref _readingVersion);
    }
}
