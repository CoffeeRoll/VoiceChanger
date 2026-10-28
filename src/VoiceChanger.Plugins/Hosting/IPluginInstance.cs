using VoiceChanger.Plugins.Native;

namespace VoiceChanger.Plugins.Hosting;

/// <summary>One plug-in parameter as the instance describes it.</summary>
internal sealed record PluginParameterInfo(
    uint Id,
    string Title,
    string Units,
    int StepCount,
    double DefaultValue,
    ParamFlags Flags,
    double Value,
    IReadOnlyList<string>? Choices);

/// <summary>What a loaded plug-in offers: its inputs, parameters, programs and window.</summary>
internal sealed record PluginDescription(
    string InputName,
    string SidechainName,
    bool HasSidechain,
    IReadOnlyList<PluginParameterInfo> Parameters,
    IReadOnlyList<string> Programs,
    uint? ProgramParameter,
    bool HasEditor);

/// <summary>What a plug-in instance tells its effect.</summary>
internal interface IPluginEvents
{
    /// <summary>A parameter changed in the plug-in's window (UI thread).</summary>
    void Edited(uint id, double value);

    /// <summary>The plug-in asks to be restarted (<see cref="RestartFlags"/>; any thread).</summary>
    void RestartRequested(RestartFlags flags);

    /// <summary>The plug-in's window was closed (UI thread).</summary>
    void EditorClosed();

    /// <summary>The process hosting the plug-in ended or stopped answering (any thread).</summary>
    void Died(string reason);
}

/// <summary>
/// A loaded plug-in: in this process (<see cref="LocalPlugin"/>) or in a host process
/// (<see cref="RemotePlugin"/>). Everything but <see cref="Process"/> and <see cref="Reset"/> runs on
/// the UI thread; those two run on the audio thread, never while the plug-in is set up again.
/// </summary>
internal interface IPluginInstance : IDisposable
{
    /// <summary>The audio and parameter changes <see cref="Process"/> works on.</summary>
    PluginBuffers Buffers { get; }

    PluginDescription Description { get; }

    IPluginEvents? Events { get; set; }

    /// <summary>Whether the instance can still be used (a host process may have ended).</summary>
    bool IsAlive { get; }

    bool IsEditorOpen { get; }

    /// <summary>Sets the plug-in up for processing. Returns null on success, or the reason it failed.</summary>
    string? Setup(int sampleRate, int channels, out int latency);

    void Suspend();

    double GetParam(uint id);

    void SetParam(uint id, double value);

    /// <summary>The plug-in's text for a value, or null.</summary>
    string? FormatParam(uint id, double value);

    byte[]? GetState();

    /// <summary>Returns null on success, or the reason it failed.</summary>
    string? SetState(byte[] state);

    /// <summary>Loads a .vstpreset file. Returns null on success, or the reason it failed.</summary>
    string? LoadPreset(string path);

    /// <summary>Saves a .vstpreset file. Returns null on success, or the reason it failed.</summary>
    string? SavePreset(string path);

    /// <summary>
    /// Opens the plug-in's window, owned by <paramref name="owner"/> (a native window handle, or 0).
    /// Returns null on success, or the reason it failed.
    /// </summary>
    string? OpenEditor(nint owner, string title);

    void CloseEditor();

    /// <summary>
    /// Processes the audio in <see cref="Buffers"/> with the first <paramref name="changeCount"/>
    /// parameter changes (zero frames only hands over the changes). Audio thread.
    /// </summary>
    int Process(int frames, bool withSidechain, int changeCount);

    /// <summary>Clears the plug-in's signal history. Audio thread.</summary>
    int Reset();
}
