using VoiceChanger.Core.Dsp;

namespace VoiceChanger.Core.Tests;

public class NoteTrackerTests
{
    private const double Rate = 48000;

    /// <summary>Feeds a voice pulse by pulse (each one period long) from a pitch curve, as a shifter would.</summary>
    private static List<(double Time, double Note)> Sing(NoteTracker tracker, Func<double, double> noteAt, double from, double seconds)
    {
        var pulses = new List<(double, double)>();
        double time = from * Rate;
        while (time < (from + seconds) * Rate)
        {
            double note = noteAt(time / Rate);
            double period = Rate / MusicalScale.NoteToFrequency(note);
            time += period;
            tracker.Observe(period, time);
            pulses.Add((time, note));
        }

        return pulses;
    }

    private static void Pause(NoteTracker tracker, double from, double seconds)
    {
        for (double time = from * Rate; time < (from + seconds) * Rate; time += 0.005 * Rate)
        {
            tracker.Observe(0, time);
        }
    }

    [Fact]
    public void Vibrato_that_swings_past_the_halfway_point_keeps_the_note()
    {
        // A3 sung 30 cents sharp with a ±35 cent vibrato: at its peaks it is closer to A#3.
        var tracker = new NoteTracker(Rate);
        var pulses = Sing(tracker, t => 57.3 + 0.35 * Math.Sin(2 * Math.PI * 5 * t), 0, 2);

        Assert.Contains(pulses, p => p.Note > 57.6);
        Assert.Equal(57, tracker.Current);
        Assert.All(pulses, p => Assert.Equal(57, tracker.NoteAt(p.Time)));
    }

    [Fact]
    public void A_move_to_another_note_counts_from_the_pulse_where_it_began()
    {
        var tracker = new NoteTracker(Rate);
        var pulses = Sing(tracker, t => t < 0.5 ? 57.1 : 57.9, 0, 0.6);

        // By the end, the tracker has moved, and the first pulse of the new note is already on it,
        // although it took a few pulses to be sure.
        Assert.Equal(58, tracker.Current);
        var first = pulses.First(p => p.Note > 57.5);
        var last = pulses.Last(p => p.Note < 57.5);
        Assert.Equal(57, tracker.NoteAt(last.Time));
        Assert.Equal(58, tracker.NoteAt(first.Time));
    }

    [Fact]
    public void One_badly_measured_pulse_does_not_move_the_note()
    {
        var tracker = new NoteTracker(Rate);
        Sing(tracker, _ => 57, 0, 0.3);

        // A pulse an octave off (a measuring slip), then the voice goes on as before.
        tracker.Observe(Rate / MusicalScale.NoteToFrequency(69), 0.3 * Rate + 100);
        var after = Sing(tracker, _ => 57, 0.31, 0.2);

        Assert.Equal(57, tracker.Current);
        Assert.All(after, p => Assert.Equal(57, tracker.NoteAt(p.Time)));
    }

    [Fact]
    public void A_slide_steps_through_the_notes_of_the_scale()
    {
        // From C4 up to G4 in C major: each note on the way, in turn, and none outside the scale.
        var tracker = new NoteTracker(Rate) { Mask = MusicalScale.ScaleMask(ScaleType.Major, 0) };
        var pulses = Sing(tracker, t => 60 + 7 * Math.Clamp((t - 0.2) / 0.6, 0, 1), 0, 1.2);

        var notes = new List<int>();
        foreach (var pulse in pulses)
        {
            if (tracker.NoteAt(pulse.Time) is int note && (notes.Count == 0 || notes[^1] != note))
            {
                notes.Add(note);
            }
        }

        Assert.Equal([60, 62, 64, 65, 67], notes);
    }

    [Fact]
    public void A_consonant_keeps_the_note_and_a_pause_lets_it_go()
    {
        var tracker = new NoteTracker(Rate);
        Sing(tracker, _ => 57.45, 0, 0.3);
        Assert.Equal(57, tracker.Current);

        // A short gap: the same note goes on, even though the voice now sits just closer to A#.
        Pause(tracker, 0.3, 0.06);
        Sing(tracker, _ => 57.55, 0.36, 0.05);
        Assert.Equal(57, tracker.Current);

        // A pause: the next phrase starts on whatever note is nearest.
        Pause(tracker, 0.41, 0.3);
        Assert.Null(tracker.Current);
        Sing(tracker, _ => 57.55, 0.71, 0.05);
        Assert.Equal(58, tracker.Current);
    }

    [Fact]
    public void Changing_the_scale_starts_afresh_with_its_notes()
    {
        var tracker = new NoteTracker(Rate);
        Sing(tracker, _ => 61, 0, 0.2);
        Assert.Equal(61, tracker.Current);

        tracker.Mask = MusicalScale.ScaleMask(ScaleType.Major, 0);
        Assert.Null(tracker.Current);
        Sing(tracker, _ => 61.2, 0.2, 0.2);
        Assert.Equal(62, tracker.Current);
    }
}
