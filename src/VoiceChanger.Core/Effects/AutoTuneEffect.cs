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
/// Pitch correction: detects the pitch of the voice and bends it to the nearest note of a key and
/// scale. A fast retune speed gives the hard, robotic "auto-tune" sound; a slower one corrects
/// gently and keeps natural vibrato.
/// </summary>
public sealed class AutoTuneEffect : AudioEffect
{
    public const string Type = "auto-tune";

    /// <summary>C major: the custom scale starts from these notes until others are picked.</summary>
    public const int DefaultCustomNotes = 0b1010_1011_0101;

    private const double ChunkSeconds = 0.005;

    // A short detection window and hop keep the analysis recent. The detector still reports a pitch
    // about 30 ms after it was sung, so the audio is delayed by the same amount; otherwise fast
    // corrections arrive late and vibrato is only partly removed (measured: 40 cents of vibrato is
    // reduced to about 2 cents with an instant retune).
    private const double DetectionWindowSeconds = 0.025;
    private const double DetectionHopSeconds = 0.005;
    private const double AlignmentDelaySeconds = 0.016;
    private const double HoldSeconds = 0.12;
    private const double ReleaseSeconds = 0.05;
    private const double Hysteresis = 0.2;

    private PitchDetector? _detector;
    private SplicingPitchShifter? _shifter;
    private float[] _mono = [];
    private int _chunkFrames;

    // Settings, refreshed in OnParametersChanged.
    private int _mask = 0xFFF;
    private double _retuneSeconds;
    private double _amount = 1;
    private int _transpose;
    private double _reference = 440;

    // Audio thread state.
    private int? _heldTarget;
    private double _inputNote;
    private double _tuning;
    private double _applied;
    private double _unvoicedSeconds = HoldSeconds;
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
            description: "Moves the corrected notes up or down."));
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

    public override int LatencyFrames => _shifter?.LatencyFrames ?? 0;

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

    protected override void OnPrepare()
    {
        _detector = new PitchDetector(SampleRate, windowSeconds: DetectionWindowSeconds, hopSeconds: DetectionHopSeconds);
        _shifter = new SplicingPitchShifter(Channels, SampleRate, AlignmentDelaySeconds);
        _mono = new float[EffectChain.MaxBlockFrames];
        _chunkFrames = Math.Max(32, (int)(ChunkSeconds * SampleRate));
    }

    protected override void OnParametersChanged()
    {
        int mask = ScaleMask;
        if (mask != _mask)
        {
            _heldTarget = null;
            _mask = mask;
        }

        _retuneSeconds = RetuneSpeed.Value / 1000.0;
        _amount = Amount.Value / 100.0;
        _transpose = Transpose.IntValue;
        _reference = Reference.Value;
    }

    protected override void OnReset()
    {
        _detector?.Reset();
        _shifter?.Reset();
        _heldTarget = null;
        _tuning = 0;
        _applied = _transpose;
        _unvoicedSeconds = HoldSeconds;
    }

    protected override void ProcessCore(Span<float> interleaved)
    {
        int channels = Channels;
        int frames = interleaved.Length / channels;
        if (_mono.Length < _chunkFrames)
        {
            _mono = new float[_chunkFrames];
        }

        for (int offset = 0; offset < frames; offset += _chunkFrames)
        {
            int count = Math.Min(_chunkFrames, frames - offset);
            var block = interleaved.Slice(offset * channels, count * channels);

            // Analyse the incoming (uncorrected) voice.
            if (channels == 1)
            {
                _detector!.Process(block);
            }
            else
            {
                float scale = 1f / channels;
                for (int frame = 0; frame < count; frame++)
                {
                    float sum = 0;
                    for (int ch = 0; ch < channels; ch++)
                    {
                        sum += block[frame * channels + ch];
                    }

                    _mono[frame] = sum * scale;
                }

                _detector!.Process(_mono.AsSpan(0, count));
            }

            UpdateCorrection((double)count / SampleRate);
            _shifter!.Ratio = Math.Pow(2, _applied / 12);
            _shifter.Process(block);
        }

        Publish();
    }

    private void UpdateCorrection(double seconds)
    {
        bool voiced = _detector!.IsVoiced;
        if (voiced)
        {
            _inputNote = MusicalScale.FrequencyToNote(_detector.Frequency, _reference);
            int target = MusicalScale.Nearest(_inputNote, _mask, _heldTarget, Hysteresis);
            _heldTarget = target;
            _tuning = target - _inputNote;
            _unvoicedSeconds = 0;
        }
        else
        {
            // Bridge short detection gaps; after that, let go of the correction gently.
            _unvoicedSeconds += seconds;
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
    }

    private void Publish()
    {
        bool voiced = _detector!.IsVoiced;
        var reading = new AutoTuneReading(
            Sequence: ++_sequence,
            IsVoiced: voiced,
            InputFrequency: voiced ? _detector.Frequency : 0,
            InputNote: voiced ? _inputNote : double.NaN,
            TargetNote: voiced && _heldTarget is int target ? target + _transpose : 0,
            OutputNote: voiced ? _inputNote + _applied : double.NaN,
            ReferenceA4: _reference);

        Interlocked.Increment(ref _readingVersion);
        _reading = reading;
        Interlocked.Increment(ref _readingVersion);
    }
}
