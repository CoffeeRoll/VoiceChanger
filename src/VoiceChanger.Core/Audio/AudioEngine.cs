using VoiceChanger.Core.Effects;

namespace VoiceChanger.Core.Audio;

/// <summary>
/// Owns the input and output streams and wires them through an <see cref="AudioProcessor"/>.
/// Public members are meant to be called from one (UI) thread.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    private readonly object _gate = new();
    private IAudioInputStream? _input;
    private IAudioOutputStream? _output;
    private AudioProcessor? _processor;
    private AudioEngineOptions? _options;
    private float _inputGainDb;
    private float _outputGainDb;
    private bool _disposed;

    public AudioEngine(IAudioBackend backend, EffectChain? effects = null)
    {
        Backend = backend ?? throw new ArgumentNullException(nameof(backend));
        Effects = effects ?? new EffectChain();
    }

    public IAudioBackend Backend { get; }

    public EffectChain Effects { get; }

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _processor is not null;
            }
        }
    }

    /// <summary>Gain applied before the effects, in dB.</summary>
    public float InputGainDb
    {
        get => _inputGainDb;
        set
        {
            _inputGainDb = value;
            lock (_gate)
            {
                _processor?.InputGain = DbToGain(value);
            }
        }
    }

    /// <summary>Gain applied after the effects, in dB.</summary>
    public float OutputGainDb
    {
        get => _outputGainDb;
        set
        {
            _outputGainDb = value;
            lock (_gate)
            {
                _processor?.OutputGain = DbToGain(value);
            }
        }
    }

    /// <summary>
    /// Raised when a device fails while running (for example when it is unplugged). The engine has
    /// already stopped when this is raised. May be raised on any thread.
    /// </summary>
    public event EventHandler<AudioStreamStoppedEventArgs>? Faulted;

    /// <summary>Opens both devices and starts processing. A running session is stopped first.</summary>
    public void Start(string inputDeviceId, string outputDeviceId, AudioEngineOptions options)
    {
        ArgumentException.ThrowIfNullOrEmpty(inputDeviceId);
        ArgumentException.ThrowIfNullOrEmpty(outputDeviceId);
        ArgumentNullException.ThrowIfNull(options);
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            StopCore();

            IAudioInputStream? input = null;
            IAudioOutputStream? output = null;
            try
            {
                input = Backend.OpenInput(inputDeviceId, options.CaptureBufferMilliseconds);
                output = Backend.OpenOutput(outputDeviceId, options.OutputLatencyMilliseconds);

                int channels = options.MonoProcessing ? 1 : Math.Min(input.Format.Channels, 2);
                Effects.Prepare(input.Format.SampleRate, channels);

                var processor = new AudioProcessor(input.Format, output.Format, channels, Effects, options.SafetyBufferMilliseconds)
                {
                    InputGain = DbToGain(_inputGainDb),
                    OutputGain = DbToGain(_outputGainDb),
                };

                input.Stopped += OnStreamStopped;
                output.Stopped += OnStreamStopped;

                // Start the output first: it plays silence until the first input arrives.
                output.Start(processor.RenderOutput);
                input.Start(processor.ProcessInput);

                _input = input;
                _output = output;
                _processor = processor;
                _options = options;
            }
            catch
            {
                if (input is not null)
                {
                    input.Stopped -= OnStreamStopped;
                    input.Dispose();
                }

                if (output is not null)
                {
                    output.Stopped -= OnStreamStopped;
                    output.Dispose();
                }

                throw;
            }
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            StopCore();
        }
    }

    public AudioEngineStatus GetStatus()
    {
        lock (_gate)
        {
            if (_processor is null || _options is null || _output is null)
            {
                return default;
            }

            double latency = _options.CaptureBufferMilliseconds
                + _processor.BufferedMilliseconds
                + _output.LatencyMilliseconds
                + _processor.ProcessingLatencyMilliseconds;

            return new AudioEngineStatus(
                IsRunning: true,
                InputFormat: _processor.InputFormat,
                OutputFormat: _processor.OutputFormat,
                ProcessingChannels: _processor.ProcessingChannels,
                InputPeak: _processor.InputMeter.ReadAndReset(),
                OutputPeak: _processor.OutputMeter.ReadAndReset(),
                BufferedMilliseconds: _processor.BufferedMilliseconds,
                EstimatedLatencyMilliseconds: latency,
                Underruns: _processor.Underruns,
                DriftCorrectionPpm: _processor.DriftCorrectionPpm);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop();
        _disposed = true;
    }

    internal static float DbToGain(float db) => MathF.Pow(10, db / 20);

    private void StopCore()
    {
        var input = _input;
        var output = _output;
        _input = null;
        _output = null;
        _processor = null;
        _options = null;

        if (input is not null)
        {
            input.Stopped -= OnStreamStopped;
            input.Stop();
            input.Dispose();
        }

        if (output is not null)
        {
            output.Stopped -= OnStreamStopped;
            output.Stop();
            output.Dispose();
        }
    }

    private void OnStreamStopped(object? sender, AudioStreamStoppedEventArgs e)
    {
        if (e.Error is null)
        {
            return;
        }

        // The event can be raised on the device thread itself, which must not be joined from here.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            lock (_gate)
            {
                if (!ReferenceEquals(sender, _input) && !ReferenceEquals(sender, _output))
                {
                    return; // A stream from a previous session.
                }

                StopCore();
            }

            Faulted?.Invoke(this, e);
        });
    }
}
