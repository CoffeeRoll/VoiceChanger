using System.Globalization;
using VoiceChanger.Core.Audio;
using VoiceChanger.Core.Dsp;
using VoiceChanger.Core.Effects;

namespace VoiceChanger.Core.Graph;

/// <summary>
/// What listening to the inputs of a <see cref="SyncNode"/> found: how much later each input arrived
/// than the first one that was heard.
/// </summary>
/// <param name="LagMilliseconds">Per input: how late it is, or <c>null</c> when it had no sound in common with the first.</param>
public sealed record SyncMeasurement(IReadOnlyList<double?> LagMilliseconds);

/// <summary>
/// Lines up several signals in time: every input comes out of its own output, delayed so that they
/// all play together.
/// <para>
/// Sources fall out of step for two reasons. Effects delay what goes through them (a voice through
/// the pitch shifter arrives about 35 ms after a backing track that went straight on): the graph
/// knows those delays, so they are matched automatically. Devices delay what they record by
/// different amounts (a Bluetooth headset far more than a wired microphone): that is set per input
/// as an offset, or measured by listening to a sound every source picks up, like a clap
/// (<see cref="StartListening"/>).
/// </para>
/// <para>
/// A signal can only be delayed, never played early, so moving one input earlier delays all the
/// others instead. Delays change with a short cross-fade, so they never click.
/// </para>
/// </summary>
public sealed class SyncNode : AudioNode
{
    public const string Type = "sync";
    public const int MinInputs = 2;
    public const int MaxInputs = 8;
    public const float MaxOffsetMilliseconds = 500;
    public const double ListenSeconds = 4;

    private const double MaxDelaySeconds = 1.5;
    private const double FadeSeconds = 0.01;
    private const int EnvelopeRate = 4000;
    private const double MinimumMatch = 0.3;

    private readonly NodePort[] _allInputs = new NodePort[MaxInputs];
    private readonly NodePort[] _allOutputs = new NodePort[MaxInputs];
    private readonly DelayLine[] _lines = new DelayLine[MaxInputs];
    private readonly int[] _wanted = new int[MaxInputs];
    private readonly int[] _delays = new int[MaxInputs];
    private int _capacity;
    private int _fadeFrames;
    private int _extraLatency;
    private Recording? _recording;

    public SyncNode()
        : base(Type, "Synchronizer", NodeCategory.Mixing)
    {
        InputCount = AddParameter(EffectParameter.Integer(
            "inputs", "Inputs", MinInputs, MaxInputs, MinInputs,
            description: "How many signals to line up."));
        MatchEffectDelays = AddParameter(EffectParameter.Toggle(
            "match", "Match effect delays", true,
            description: "Delays the signals that went through fewer or faster effects, so everything that left its source together comes out together."));

        var offsets = new EffectParameter[MaxInputs];
        for (int i = 0; i < MaxInputs; i++)
        {
            int number = i + 1;
            int index = i;
            offsets[i] = AddParameter(EffectParameter.Continuous(
                $"offset{number}", $"Input {number} timing", -MaxOffsetMilliseconds, MaxOffsetMilliseconds, 0, "ms", decimals: 1,
                formatter: FormatOffset,
                description: $"Moves input {number} later (positive) or earlier (negative) than the others, for devices that record late. Earlier works by delaying the others.")
                .AvailableWhen(() => index < InputCount.IntValue));
            _allInputs[i] = CreatePort($"in{number}", $"Input {number}", PortDirection.Input, i);
            _allOutputs[i] = CreatePort($"out{number}", $"Output {number}", PortDirection.Output, i);
            InputMeters[i] = new PeakMeter();
        }

        Offsets = offsets;
        UpdatePorts();
        InputCount.ValueChanged += (_, _) => UpdatePorts();
    }

    /// <summary>Number of signals (the ports beyond it disappear, together with their connections).</summary>
    public EffectParameter InputCount { get; }

    public EffectParameter MatchEffectDelays { get; }

    /// <summary>Per input, in milliseconds: positive plays it later than the others, negative earlier.</summary>
    public IReadOnlyList<EffectParameter> Offsets { get; }

    /// <summary>Level arriving at each input.</summary>
    public PeakMeter[] InputMeters { get; } = new PeakMeter[MaxInputs];

    /// <summary>
    /// How much later the latest output is than the latest input: zero when only effect delays are
    /// matched, more when offsets hold some inputs back.
    /// </summary>
    public override int LatencyFrames => Volatile.Read(ref _extraLatency);

    /// <summary>Whether <see cref="StartListening"/> is still collecting sound.</summary>
    public bool IsListening => Volatile.Read(ref _recording) is { IsComplete: false };

    /// <summary>How much of the listening time has passed, from 0 to 1 (0 when not listening).</summary>
    public double ListenProgress => Volatile.Read(ref _recording) is { } recording
        ? (double)Volatile.Read(ref recording.Position) / recording.Length
        : 0;

    internal static string FormatOffset(float value) => value == 0
        ? "In time"
        : Math.Abs(value).ToString("0.0", CultureInfo.CurrentCulture) + (value > 0 ? " ms later" : " ms earlier");

    /// <summary>The delay currently applied to an input, in milliseconds.</summary>
    public double DelayMilliseconds(int input) =>
        IsPrepared ? Volatile.Read(ref _delays[input]) * 1000.0 / SampleRate : 0;

    /// <summary>
    /// Starts listening to every connected output for <see cref="ListenSeconds"/>: make a sharp
    /// sound that every source picks up (a clap), then call <see cref="TakeMeasurement"/>. Any
    /// thread.
    /// </summary>
    public void StartListening()
    {
        if (!IsPrepared)
        {
            return;
        }

        int decimation = Math.Max(1, (int)Math.Round((double)SampleRate / EnvelopeRate));
        int length = (int)(ListenSeconds * SampleRate / decimation);
        Volatile.Write(ref _recording, new Recording(MaxInputs, length, decimation, (double)SampleRate / decimation));
    }

    public void CancelListening() => Volatile.Write(ref _recording, null);

    /// <summary>
    /// Once listening has finished, works out how late each input was (which takes a moment: call it
    /// off the UI thread) and stops listening. Returns <c>null</c> while still listening, or when
    /// not listening at all.
    /// </summary>
    public SyncMeasurement? TakeMeasurement()
    {
        var recording = Volatile.Read(ref _recording);
        if (recording is not { IsComplete: true })
        {
            return null;
        }

        Interlocked.CompareExchange(ref _recording, null, recording);
        return Measure(recording.Envelopes, recording.Heard, recording.EnvelopeRate);
    }

    /// <summary>Moves the inputs a measurement found late or early so that they line up. Returns how many moved.</summary>
    public int ApplyMeasurement(SyncMeasurement measurement)
    {
        ArgumentNullException.ThrowIfNull(measurement);
        int moved = 0;
        for (int i = 0; i < Math.Min(measurement.LagMilliseconds.Count, InputCount.IntValue); i++)
        {
            if (measurement.LagMilliseconds[i] is double lag && Math.Abs(lag) >= 0.05)
            {
                Offsets[i].Value = (float)(Offsets[i].Value - lag);
                moved++;
            }
        }

        return moved;
    }

    /// <summary>
    /// How late each signal is compared to the first one that has sound, from envelopes of its
    /// sharp changes: the lag at which they correlate best. <c>null</c> for signals without sound
    /// or without a clear match.
    /// </summary>
    internal static SyncMeasurement Measure(float[][] envelopes, bool[] heard, double envelopeRate)
    {
        var lags = new double?[envelopes.Length];
        int reference = -1;
        for (int i = 0; i < envelopes.Length && reference < 0; i++)
        {
            if (heard[i] && Energy(envelopes[i]) > 0)
            {
                reference = i;
            }
        }

        if (reference < 0)
        {
            return new SyncMeasurement(lags);
        }

        lags[reference] = 0;
        int length = envelopes[reference].Length;
        int maxLag = Math.Min(length - 1, (int)(2 * MaxOffsetMilliseconds / 1000 * envelopeRate));
        int size = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)(length + maxLag + 1));
        var fft = new Fft(size);
        var refRe = new float[size];
        var refIm = new float[size];
        var re = new float[size];
        var im = new float[size];
        double referenceEnergy = Centre(envelopes[reference], refRe);
        fft.Forward(refRe, refIm);

        for (int i = 0; i < envelopes.Length; i++)
        {
            if (i == reference || !heard[i])
            {
                continue;
            }

            Array.Clear(re);
            Array.Clear(im);
            double energy = Centre(envelopes[i], re);
            if (energy <= 0 || referenceEnergy <= 0)
            {
                continue;
            }

            // Cross-correlation: conj(reference) × signal; lag l > 0 means the signal is l late.
            fft.Forward(re, im);
            for (int k = 0; k < size; k++)
            {
                float a = refRe[k], b = -refIm[k];
                (re[k], im[k]) = (a * re[k] - b * im[k], a * im[k] + b * re[k]);
            }

            fft.Inverse(re, im);
            double norm = size * Math.Sqrt(referenceEnergy * energy);
            int best = 0;
            double bestValue = double.NegativeInfinity;
            for (int lag = -maxLag; lag <= maxLag; lag++)
            {
                double value = re[(lag + size) % size] / norm;
                if (value > bestValue)
                {
                    bestValue = value;
                    best = lag;
                }
            }

            if (bestValue < MinimumMatch)
            {
                continue;
            }

            double before = re[(best - 1 + size) % size], peak = re[(best + size) % size], after = re[(best + 1) % size];
            double curvature = before - 2 * peak + after;
            double offset = curvature < 0 ? Math.Clamp(0.5 * (before - after) / curvature, -0.5, 0.5) : 0;
            lags[i] = (best + offset) * 1000 / envelopeRate;
        }

        return new SyncMeasurement(lags);
    }

    protected override void OnPrepare()
    {
        int frames = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)(MaxDelaySeconds * SampleRate + GraphPlan.BlockFrames));
        _capacity = frames - GraphPlan.BlockFrames - 1;
        _fadeFrames = Math.Max(1, (int)(FadeSeconds * SampleRate));
        for (int i = 0; i < MaxInputs; i++)
        {
            _lines[i] = new DelayLine(frames, Channels);
            _delays[i] = 0;
        }

        _extraLatency = 0;
        _recording = null;
    }

    protected override void ProcessCore(NodeBlock block)
    {
        int count = block.InputCount;
        double framesPerMs = SampleRate / 1000.0;
        bool match = MatchEffectDelays.BoolValue;

        // What each input should be delayed by: the inputs that are less late than the latest one
        // (when matching effect delays) plus its offset, less what every input would be delayed by.
        int least = int.MaxValue, latestInput = 0;
        for (int i = 0; i < count; i++)
        {
            if (!block.IsConnected(i))
            {
                continue;
            }

            _wanted[i] = (match ? -block.InputLatencyFrames(i) : 0) + (int)Math.Round(Offsets[i].Value * framesPerMs);
            least = Math.Min(least, _wanted[i]);
            latestInput = Math.Max(latestInput, block.InputLatencyFrames(i));
        }

        int latestOutput = 0;
        var recording = Volatile.Read(ref _recording);
        for (int i = 0; i < count; i++)
        {
            var output = block.Output(i);
            var line = _lines[i];
            if (!block.IsConnected(i))
            {
                line.Clear();
                output.Clear();
                Volatile.Write(ref _delays[i], 0);
                continue;
            }

            var input = block.Input(i);
            InputMeters[i].Update(input);
            line.Target = Math.Min(_wanted[i] - least, _capacity);
            line.Process(input, output, _fadeFrames);
            Volatile.Write(ref _delays[i], line.Target);
            latestOutput = Math.Max(latestOutput, block.InputLatencyFrames(i) + line.Target);
            Meter.Update(output);
            recording?.Write(i, output, block.Channels);
        }

        recording?.Advance(block.Frames);
        Volatile.Write(ref _extraLatency, Math.Max(0, latestOutput - latestInput));
    }

    private static double Energy(float[] values)
    {
        double sum = 0;
        foreach (float v in values)
        {
            sum += v * v;
        }

        return sum;
    }

    /// <summary>Copies the values without their mean into <paramref name="target"/> and returns their energy.</summary>
    private static double Centre(float[] values, float[] target)
    {
        double mean = values.Average(v => (double)v);
        double energy = 0;
        for (int n = 0; n < values.Length; n++)
        {
            target[n] = (float)(values[n] - mean);
            energy += target[n] * target[n];
        }

        return energy;
    }

    private void UpdatePorts()
    {
        int count = InputCount.IntValue;
        SetPorts(_allInputs[..count], _allOutputs[..count]);
    }

    /// <summary>A delay for one interleaved signal that cross-fades when its delay changes.</summary>
    private sealed class DelayLine(int frames, int channels)
    {
        private readonly float[] _buffer = new float[frames * channels];
        private readonly int _mask = frames - 1;
        private long _write;
        private int _delay;
        private int _from;
        private int _fade = -1;
        private bool _idle = true;

        public int Target { get; set; }

        /// <summary>Forgets what it holds (once: an idle line stays cleared).</summary>
        public void Clear()
        {
            if (!_idle)
            {
                Array.Clear(_buffer);
                _delay = Target = 0;
                _fade = -1;
                _idle = true;
            }
        }

        public void Process(ReadOnlySpan<float> input, Span<float> output, int fadeFrames)
        {
            _idle = false;
            int frames = input.Length / channels;
            if (_fade < 0 && Target != _delay)
            {
                _from = _delay;
                _delay = Target;
                _fade = 0;
            }

            for (int f = 0; f < frames; f++, _write++)
            {
                int write = (int)(_write & _mask);
                for (int ch = 0; ch < channels; ch++)
                {
                    _buffer[write * channels + ch] = input[f * channels + ch];
                }

                int now = (int)((_write - _delay) & _mask) * channels;
                if (_fade < 0)
                {
                    for (int ch = 0; ch < channels; ch++)
                    {
                        output[f * channels + ch] = _buffer[now + ch];
                    }

                    continue;
                }

                int before = (int)((_write - _from) & _mask) * channels;
                float mix = (_fade + 1f) / fadeFrames;
                for (int ch = 0; ch < channels; ch++)
                {
                    float old = _buffer[before + ch];
                    output[f * channels + ch] = old + (_buffer[now + ch] - old) * mix;
                }

                if (++_fade >= fadeFrames)
                {
                    _fade = -1;
                }
            }
        }
    }

    /// <summary>
    /// Envelopes of the sharp changes in every output (peaks of the first difference, about
    /// 4000 a second), for working out how far apart they are.
    /// </summary>
    private sealed class Recording(int inputs, int length, int decimation, double envelopeRate)
    {
        private readonly float[] _previous = new float[inputs];
        private readonly float[] _peak = new float[inputs];
        private int _phase;

        public float[][] Envelopes { get; } = Enumerable.Range(0, inputs).Select(_ => new float[length]).ToArray();

        public bool[] Heard { get; } = new bool[inputs];

        public int Length { get; } = length;

        public double EnvelopeRate { get; } = envelopeRate;

        public int Position;

        public bool IsComplete => Volatile.Read(ref Position) >= Length;

        /// <summary>Adds one input's block (before <see cref="Advance"/> is called for it). Audio thread.</summary>
        public void Write(int input, ReadOnlySpan<float> block, int channels)
        {
            if (IsComplete)
            {
                return;
            }

            Heard[input] = true;
            int position = Position;
            int phase = _phase;
            float previous = _previous[input], peak = _peak[input];
            var envelope = Envelopes[input];
            for (int f = 0; f + channels <= block.Length && position < Length; f += channels)
            {
                float mono = 0;
                for (int ch = 0; ch < channels; ch++)
                {
                    mono += block[f + ch];
                }

                peak = Math.Max(peak, Math.Abs(mono - previous));
                previous = mono;
                if (++phase >= decimation)
                {
                    envelope[position++] = peak;
                    peak = 0;
                    phase = 0;
                }
            }

            _previous[input] = previous;
            _peak[input] = peak;
        }

        /// <summary>Moves on by a block once every input has been written. Audio thread.</summary>
        public void Advance(int frames)
        {
            if (IsComplete)
            {
                return;
            }

            int total = _phase + frames;
            _phase = total % decimation;
            Volatile.Write(ref Position, Math.Min(Length, Position + total / decimation));
        }
    }
}
