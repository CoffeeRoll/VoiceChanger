namespace VoiceChanger.Core.Dsp;

/// <summary>
/// Decides which note of a scale a voice is singing, from the pitch of its pulses as they are
/// measured.
/// <para>
/// A note stays until the voice has clearly moved to another one. Evidence for a move builds up
/// while the voice is closer to another note than to the one held (by more than
/// <see cref="Margin"/>), at a rate that grows with how much closer: a quick scoop, vibrato that
/// swings past the halfway point, or one badly measured pulse never adds up to a change, while a
/// real move does within a few milliseconds. The change then counts from where the move began.
/// </para>
/// <para>
/// The pulse shifter measures each pulse well before it moves it (<see cref="IVoiceShifter.Observer"/>),
/// so by the time a pulse is moved, the tracker has already seen where the voice goes next: a note
/// change lands on the very pulse where the singer moved, with no detection delay. Fed only as pulses
/// are moved, changes come a few milliseconds late instead.
/// </para>
/// Audio thread only.
/// </summary>
public sealed class NoteTracker
{
    /// <summary>How much closer (in semitones) another note must be before it counts as evidence.</summary>
    public const double Margin = 0.1;

    /// <summary>Evidence (semitones × seconds) that makes the voice move to another note.</summary>
    public const double Evidence = 0.012;

    /// <summary>The most evidence a single measurement adds per second, so one wild pulse can't move the note.</summary>
    public const double MaxRate = 1.0;

    /// <summary>How long a gap without voice (a consonant) is bridged before the note is let go.</summary>
    public const double HoldSeconds = 0.12;

    private const int Capacity = 64;

    private readonly double _sampleRate;
    private readonly double[] _starts = new double[Capacity];
    private readonly int[] _notes = new int[Capacity];
    private int _first;
    private int _count;
    private int? _held;
    private double _evidence;
    private double _moveStart = double.NaN;
    private double _lastTime = double.NaN;
    private double _unvoicedSince = double.NaN;
    private double _queried = double.NegativeInfinity;
    private int _mask = 0xFFF;

    public NoteTracker(double sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sampleRate, 0);
        _sampleRate = sampleRate;
    }

    /// <summary>The notes allowed, as pitch classes (bit 0 = C). Changing them forgets the held note.</summary>
    public int Mask
    {
        get => _mask;
        set
        {
            value &= 0xFFF;
            if (value == 0 || value == _mask)
            {
                return;
            }

            _mask = value;
            Forget();
        }
    }

    /// <summary>Frequency of A4, in Hz.</summary>
    public double Reference { get; set; } = 440;

    /// <summary>The note the voice is on at the latest measurement, or <c>null</c> when there is no voice.</summary>
    public int? Current => _held;

    /// <summary>The pitch of the latest voiced measurement, as a fractional MIDI note (NaN before any).</summary>
    public double LastNote { get; private set; } = double.NaN;

    public void Reset()
    {
        Forget();
        _lastTime = double.NaN;
        _queried = double.NegativeInfinity;
        LastNote = double.NaN;
    }

    /// <summary>
    /// Takes in one measurement: the period of the voice (in samples, 0 when there is no voice) at
    /// a position in the stream (in samples). Measurements may come a little out of order (a shifter
    /// marking the pulses back to where a voice began).
    /// </summary>
    public void Observe(double period, double time)
    {
        double seconds = double.IsNaN(_lastTime) || time <= _lastTime
            ? (period > 0 ? period : 0) / _sampleRate
            : (time - _lastTime) / _sampleRate;
        _lastTime = double.IsNaN(_lastTime) ? time : Math.Max(_lastTime, time);

        if (period <= 0)
        {
            if (double.IsNaN(_unvoicedSince))
            {
                _unvoicedSince = time;
            }

            if (_held is not null && time - _unvoicedSince >= HoldSeconds * _sampleRate)
            {
                // A pause, not a consonant: the next voice starts on whatever note it sings.
                _held = null;
                _evidence = 0;
                _moveStart = double.NaN;
                Begin(_unvoicedSince + HoldSeconds * _sampleRate, null);
            }

            return;
        }

        _unvoicedSince = double.NaN;
        double note = MusicalScale.FrequencyToNote(_sampleRate / period, Reference);
        LastNote = note;
        int nearest = MusicalScale.Nearest(note, Mask);
        if (_held is not int held)
        {
            _held = nearest;
            Begin(time, nearest);
            return;
        }

        if (nearest == held)
        {
            _evidence = 0;
            _moveStart = double.NaN;
            return;
        }

        double rate = Math.Min(MaxRate, Math.Abs(note - held) - Math.Abs(note - nearest) - Margin);
        if (rate <= 0)
        {
            return;
        }

        if (double.IsNaN(_moveStart))
        {
            _moveStart = time;
        }

        _evidence += rate * seconds;
        if (_evidence >= Evidence)
        {
            _held = nearest;
            Begin(_moveStart, nearest);
            _evidence = 0;
            _moveStart = double.NaN;
        }
    }

    /// <summary>
    /// The note the voice is on at a position in the stream (in samples), or <c>null</c> when there
    /// is no voice there. Positions asked for should not go backwards: what lies behind the last one
    /// is forgotten.
    /// </summary>
    public int? NoteAt(double time)
    {
        _queried = Math.Max(_queried, time);

        // Segments that a later one has replaced by now can go.
        while (_count > 1 && _starts[(_first + 1) % Capacity] <= _queried)
        {
            _first = (_first + 1) % Capacity;
            _count--;
        }

        for (int i = _count - 1; i >= 0; i--)
        {
            int index = (_first + i) % Capacity;
            if (_starts[index] <= time)
            {
                return _notes[index] == int.MinValue ? null : _notes[index];
            }
        }

        return null;
    }

    private void Forget()
    {
        _held = null;
        _evidence = 0;
        _moveStart = double.NaN;
        _unvoicedSince = double.NaN;
        _first = 0;
        _count = 0;
    }

    /// <summary>Starts a stretch on a note (or without voice) from a position on.</summary>
    private void Begin(double start, int? note)
    {
        // What has been played can't change any more, and stretches stay in order.
        if (start <= _queried)
        {
            start = Math.BitIncrement(_queried);
        }

        // A stretch that starts no later than the last one replaces it.
        while (_count > 0 && _starts[(_first + _count - 1) % Capacity] >= start)
        {
            _count--;
        }

        if (_count == Capacity)
        {
            _first = (_first + 1) % Capacity;
            _count--;
        }

        int index = (_first + _count) % Capacity;
        _starts[index] = start;
        _notes[index] = note ?? int.MinValue;
        _count++;
    }
}
