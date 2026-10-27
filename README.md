# VoiceChanger

A real-time voice changer for Windows written in C#. Pick a microphone, choose and configure effects,
pick an output, and hear the result live with low delay.

![VoiceChanger main window](docs/screenshot.png)

## Features

- **Live processing with low delay.** Audio goes from the microphone to the output in roughly 40–70 ms,
  depending on the latency setting and the effects in use.
- **Any input and output device.** Microphones, line inputs, speakers, headphones and virtual audio
  cables. If the two devices use different sample rates, the app converts between them, and it
  compensates for the small clock difference between two separate devices, so the delay stays the same
  and you don't get periodic crackles.
- **Effect chain.** Add, remove and reorder effects. Each effect has its own on/off switch, a preset
  list and a reset button. A master switch bypasses all of them at once. Switching makes no clicks.
- **Equalizer:** ten bands from 31 Hz to 16 kHz, adjustable bandwidth, low cut and high cut filters,
  output gain and a live frequency response curve. Presets: Bass boost, Treble boost, Voice clarity,
  Warm, Telephone, AM radio, Megaphone, Muffled.
- **Pitch shifter:** up to two octaves up or down. The **Octave** buttons (−2, −1, 0, +1, +2) jump
  straight to whole octaves, and the **Pitch** slider adds single semitones; its label reads e.g.
  "+1 octave +3 semitones". There is also fine tuning in cents, an adjustable window size and a
  dry/wet mix. It has two algorithms:
  - *Low latency (granular)*: shifts the sound in the time domain and aligns each grain to the waveform
    to reduce warble. Delay is about half the window size.
  - *High quality (phase vocoder)*: a phase-locked phase vocoder followed by resampling. It sounds
    smoother for large shifts and adds more delay.

  Both move the formants (the resonances that make a voice recognisable) along with the pitch, like
  playing a recording faster or slower: up sounds like a chipmunk, down like a slowed-down tape.

  **Preserve formants** keeps the character of your voice when you shift it, so an octave up sounds
  like you singing higher rather than a chipmunk, and an octave down like a deeper person rather than
  a monster. With it on, **Formant shift** moves the formants on their own: negative values sound
  like a bigger person, positive values like a smaller one, without changing the pitch. The
  **Voice method** toggle picks how the formants are kept (see [Voice methods](#voice-methods)):
  *Pulse* is clearest going down, *Spectral* smoothest going up.

  Presets: Higher voice, Lower voice, Chipmunk, Deep, Monster, Harmony, Detune, Natural octave up,
  Natural octave down, Bigger voice, Smaller voice. *Lower voice*, *Deep* and the *Natural* and
  *Bigger/Smaller voice* presets preserve formants (*Natural octave up* and *Smaller voice* with the
  spectral method); *Monster* deliberately doesn't.
- **Auto Tune:** detects the pitch of your voice and pulls it to the nearest note of a key and scale.
  See [Auto Tune](#auto-tune) below.
- **Vocoder:** your voice shapes another sound: a robot that follows your pitch, a monotone robot, a
  choir, a whisper, or (in the node editor) any instrument or music plugged into its *Carrier*
  socket. See [Vocoder](#vocoder) below.
- **Node editor** for anything beyond one microphone and one chain: several inputs at once, a
  **mixer** to combine them, a **synchronizer** to line them up in time, effects anywhere in between,
  and several outputs. See [Node editor](#node-editor) below.
- Input and output **level meters**, input gain and output volume.
- **Remembers your settings**: devices, effects, every effect setting, the node graph and the latency
  preset.

## How to use it

1. **Input:** choose your microphone (step 1).
2. **Effects:** adjust the effects in the middle column (step 2). Use **Add effect** to add more, the
   arrows to change the order, and ✕ to remove one. Effects run from top to bottom.
3. **Output:** choose where the changed voice goes (step 3).
4. **On/off:** press **Start** (or Ctrl+Enter) to begin processing. Use **Effects on/off** at the top
   (step 4) to compare your normal voice with the changed one, or the switch on each effect card to turn
   that effect on or off.

Tips:

- **Wear headphones.** Your speakers feed back into the microphone.
- **To use the voice in Discord, games, Zoom or OBS**, install a virtual audio cable such as
  [VB-CABLE](https://vb-audio.com/Cable/). Select *CABLE Input* as the **output** in VoiceChanger, then
  select *CABLE Output* as the **microphone** in the other app.
- **Collapse effects to make room.** Click an effect's name (or the arrow next to it) to fold the
  card down to a one-line summary of its settings, e.g. "+1 octave · formants kept" or, for
  Auto Tune while you sing, "D4 → D4 · A minor · instant retune". **Collapse all** / **Expand all**
  in the Effects header folds or opens every card. Collapsed cards stay collapsed the next time you
  start the app.
- **Double-click any effect slider** to reset it to its default value.
- **If you hear crackles or dropouts**, choose a safer latency setting under Output. The status bar
  counts dropouts while audio is running.
- Settings are saved to `%APPDATA%\VoiceChanger\settings.json`.

## Node editor

![Node editor](docs/node-editor.png)

Switch from **Effect chain** to **Node editor** at the top of the window to build the signal path out
of nodes and wires, like the shader editor in Blender. The first time you open it, your current chain
becomes the starting graph: *Audio Input → your effects → Mixer → Audio Output*.

Nodes:

| Node | What it does |
| --- | --- |
| Audio Input | Audio from a microphone, line input or any other recording device. Add one per device to use several at once. Has its own gain. |
| Tone Generator | A sine, triangle, sawtooth or square wave, or white noise. Handy for testing a patch, or as a drone under your voice. |
| Pitch Shifter, Auto Tune, Equalizer, Vocoder | The effects, with the same settings and presets as in the chain. The switch in the node's header turns it on or off. The Vocoder node has a second socket, *Carrier*, for the sound your voice shapes. |
| Mixer | Combines 2 to 8 signals. Each input has a volume fader, a mute button and a level meter, and there is a master volume. |
| Synchronizer | Lines up 2 to 8 signals in time, so they play together: each input comes out of its own output, delayed as needed. See [Lining up sources](#lining-up-sources-synchronizer). |
| Audio Output | Plays the signal on a device. Add one per device to play on several at once, e.g. your headphones and a virtual cable. Has its own volume. |

Working with nodes:

- **Connect:** drag from a socket on the right of a node (an output) to a socket on the left of
  another (an input), or the other way round. An output can feed any number of inputs; an input takes
  one wire, so a new wire replaces the old one. Wires that would form a loop are refused.
- **Disconnect or re-route:** drag a wire away from the input it is plugged into, and drop it on
  another input or on empty space.
- **Add nodes:** **Add node** in the toolbar, right-click the canvas, or press **Shift+A**.
- **Select** a node by clicking it. The inspector on the right shows all its settings, including the
  full effect editors, and lets you give the node a name (e.g. "Guitar").
- **Move** a node by dragging it. Drag the background (or use the middle mouse button) to move
  around, and scroll to zoom. **Fit** (or **Home**) shows the whole graph.
- **Delete** removes the selected node, **Ctrl+D** duplicates it. Right-click a node for the same.
- **Effects on/off**, **Start/Stop**, the latency setting and **Mono** work the same as in the chain.
  Editing the graph while audio is running takes effect immediately with a short fade; adding,
  removing or changing a *device* briefly restarts the audio.

How the devices are kept in sync: the first Audio Output node's device is the master clock. Each time it
needs audio, the app pulls the latest audio from every input device, runs the whole graph and hands
the result to the other output devices. Every other device runs on its own clock, so each one gets its
own sample rate conversion and drift compensation, the same way the chain connects its two devices.

### Lining up sources (Synchronizer)

Signals from different places don't arrive at the same time. A voice through the pitch shifter comes
out about 35 ms after a backing track that went straight to the mixer, and a Bluetooth headset
records its sound far later than a wired microphone. Put a **Synchronizer** in front of the mixer:
wire each source into one of its inputs and each of its outputs into the mixer.

- **Match effect delays** (on by default) delays the signals that went through fewer or faster
  effects, so everything that left its source at the same moment comes out together. The app knows
  every effect's delay, so this is automatic, and it follows along when you switch effects on or off.
- **Input timing** moves one input later or earlier by up to half a second, for delays the app can't
  know, such as a device's own recording delay. Earlier works by delaying all the others, since
  nothing can play before it is heard.
- **Listen for a clap** works out those device delays for you. Press it while the audio is running,
  then clap (or make any sharp sound) once or twice where every source can hear it. After four
  seconds the synchronizer compares the inputs, sets each one's timing so the claps line up, and
  tells you how late each input was. For music, play it out loud so the microphones pick it up too.
- The inspector shows how long each input waits; the inputs' timings are shown next to their sockets.
  Timing changes fade over 10 ms, so they never click.

Limitations:

- Without a synchronizer, signals that went through effects with different delays are not re-aligned
  when they meet in a mixer. For example, a dry voice mixed with the same voice through the phase
  vocoder is offset by about 20 ms.
- An Audio Input node that isn't connected to anything still opens its device, so its level meter keeps
  working.

## Vocoder

Add it with **Add effect → Vocoder**, or as a node. Your voice is split into bands (8 to 32, from
100 Hz to 8 kHz), and another sound, the *carrier*, is played through the same bands at the loudness
your voice has in each. The carrier is:

- whatever is plugged into the node's **Carrier** socket in the node editor: an instrument from an
  Audio Input, music from a virtual cable, or a Tone Generator. Anything rich in harmonics works best;
  or
- otherwise the built-in **Synth**: a sawtooth on *your pitch* (a talking robot, which keeps your
  intonation; *Transpose* moves it), on a fixed *Note*, a major *Chord* (a choir), or *Whisper*
  (noise).

| Setting | What it does |
| --- | --- |
| Synth, Note, Transpose | The built-in carrier, when nothing is plugged into the Carrier socket. |
| Bands | More bands make words clearer; fewer sound more robotic. |
| Response | How quickly the sound falls quiet after your voice does: short is crisp, long is smooth. |
| Formant shift | Moves the resonances of your voice on the carrier: negative sounds bigger, positive smaller. |
| Consonants | Lets the hiss of s, t and f through, so words stay clear. |
| Mix, Output | Blend with your own voice, and the vocoder's volume. |

Presets: Robot, Deep robot, Monotone, Choir, Whisper, Classic (16 bands). The vocoder adds no delay.

## Plugins (planned)

Third-party VST3 effects aren't supported yet. [docs/vst-plugin-support.md](docs/vst-plugin-support.md)
describes what it would take: a native bridge to the VST3 SDK, plugins as regular effects and nodes,
plugin windows, scanning in a separate process, saved plugin state, and crash isolation.

## Auto Tune

![Auto Tune card](docs/auto-tune.png)

Add it with **Add effect → Auto Tune**. While audio is running the card shows:

- **You sing / Corrected to:** the note you are singing, its frequency and how many cents it is off,
  and the note it is being pulled to, with the size of the correction in between.
- **Tuner:** how far your voice is from the target note, from −50 cents (flat) to +50 cents (sharp).
  The green zone is within ±10 cents.
- **Pitch graph:** the last five seconds of your sung pitch (cyan) and the corrected pitch (purple),
  drawn on note lines. The notes of the selected scale are labelled, and the key's root note is
  highlighted.
- **Allowed notes:** the twelve notes, with the ones your voice can be pulled to lit up. The target
  note is filled in, and the note you are closest to is outlined. **Click a note to allow it or leave
  it out**: the scale becomes a *Custom* scale with exactly the notes you picked (starting from the
  selected scale), shown as e.g. "Custom: C D E G A". Choose another scale to go back; choosing
  *Custom* again brings your notes back.

Settings:

| Setting | What it does |
| --- | --- |
| Key and Scale | The notes your voice is pulled to. *Chromatic* (default) allows all twelve notes and works with any song. Also available: major, minor, harmonic minor, major/minor pentatonic, blues, Dorian and Mixolydian, in any key, and *Custom*: the notes you click on the note strip (the key doesn't apply to it). |
| Retune speed | How fast the pitch moves to the note. *Instant* (0 ms) gives the hard, robotic sound and flattens vibrato. 20–80 ms sounds like a polished vocal. 150 ms and above corrects gently and keeps slides and vibrato. |
| Correction | How much of the distance to the note is corrected. 100 % lands exactly on the note. |
| Transpose | Moves the corrected notes up or down by up to an octave. Your voice keeps its character (like the pitch shifter with *Preserve formants*), so an octave down sounds like a deeper voice, not a slowed-down recording. |
| Voice method | How your voice is shifted: *Pulse* or *Spectral*, the same choice as in the pitch shifter (see [Voice methods](#voice-methods)). Pulse corrects every pulse of your voice right on time and suits transposing down; Spectral suits transposing up. |
| Tuning (A4) | Reference pitch. Leave it at 440 Hz unless the music is tuned differently (for example 432 Hz). |

The style presets (*Pop*, *Hard tune*, *Natural*, *Subtle*) only change the correction settings and
keep your key and scale.

Notes:

- Auto Tune is made for a single voice; it can't correct chords or music. Breaths, consonants and
  silence have no pitch, so it leaves them uncorrected.
- It adds about 35 ms of delay with the pulse method and about 40 ms with the spectral one. With
  *Pulse* each pulse of your voice is corrected from its own measured period, so corrections land
  exactly on time and a hard tune comes out exactly on the note. *Spectral* corrects every 5 ms or
  so from a pitch detector lined up with the sound it is shifting; a hard tune is just as exact, but
  it flattens fast vibrato a little less completely.
- It usually works best as the first effect, so it hears your natural voice.

## Voice methods

Every effect that shifts a voice while keeping its character (the pitch shifter with *Preserve
formants*, and Auto Tune) offers the same two methods, on a **Voice method** toggle that is also
there when you use those effects as nodes. Both need a single voice; chords and music don't come
out cleanly.

| Method | How it works | Best for | Delay |
| --- | --- | --- | --- |
| **Pulse** (default) | Splits your voice into the vocal tract and the pulses that drive it (linear prediction), lays the pulses down again closer together or further apart, and plays them through your vocal tract again. Breath noise isn't dragged down into the voice, and between pulses the vocal tract rings on just as in a real lower voice. | Going down: a lower voice stays clear, with the upper harmonics of your voice intact. | about 35 ms |
| **Spectral** | A phase vocoder rebuilds the harmonics at the new pitch and keeps them under your voice's own spectral envelope ("true envelope"). | Going up: a higher voice comes out smooth, where pulses laid close together would overlap. | about 40 ms |

Going down with *Spectral* has to rebuild your upper formants from the weak, breathy top of your
voice, which can sound thin or hissy; going up a long way with *Pulse* can sound a little buzzy.
Switch freely while you listen: the change is immediate.

## Getting the app

### Download a build

Every push runs the **Build** workflow in GitHub Actions. To download the app, open the latest
successful run and get the `VoiceChanger-win-x64` artifact. It contains a self-contained
`VoiceChanger.exe` that runs without installing .NET.

### Build from source

Requirements: Windows 10 or 11 and the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```powershell
git clone <this repository>
cd VoiceChanger
dotnet run --project src/VoiceChanger.App -c Release
```

To create a single-file executable:

```powershell
dotnet publish src/VoiceChanger.App -c Release -r win-x64 --self-contained `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

To run the tests:

```powershell
dotnet test
```

The tests also run on Linux and macOS. Audio input and output currently use WASAPI, so they only
work on Windows. On other systems the window opens, but the device lists are empty.

## How it works

```
 capture thread                                              render thread
┌──────────────┐  ┌───────────┐  ┌──────────────┐  ┌──────────────┐  ┌─────────────┐  ┌──────────────┐
│ input device │→ │ to mono / │→ │ effect chain │→ │ resampler +  │→ │ lock-free   │→ │ output device│
│   (WASAPI)   │  │ input gain│  │ (EQ, pitch…) │  │ drift control│  │ ring buffer │  │   (WASAPI)   │
└──────────────┘  └───────────┘  └──────────────┘  └──────────────┘  └─────────────┘  └──────────────┘
```

- Effects run on the capture thread in blocks of about 10 ms and never take locks. Parameter changes
  from the UI are picked up at the start of the next block.
- The input and output devices run on separate clocks. A small ring buffer sits between them. The
  render side estimates how full it is, counting audio already captured but not yet delivered, and nudges
  the resampler ratio by a few hundred parts per million. That keeps the buffer at about one block plus
  a safety margin. As a result the delay stays low and steady and no samples are dropped.
- In the node editor the graph runs on the render thread of the main output instead. Each other input
  and output device is connected to it through the same kind of buffer, resampler and drift control.
  The graph is compiled into an immutable plan (only nodes that lead to an output, in dependency order,
  with a buffer per output socket). Edits publish a new plan that the audio thread picks up on its
  next block. With every block the plan also adds up the delay of the effects on each path, and tells
  each node how late each of its inputs is: that is how the synchronizer matches effect delays.
- Devices are opened in WASAPI shared mode with event-driven buffers. The audio threads run at the
  "Pro Audio" scheduling priority of the Windows Multimedia Class Scheduler Service (MMCSS).

### Project layout

| Project | Contents |
| --- | --- |
| `src/VoiceChanger.Core` | Platform-independent DSP and engine: biquad filters, FFT, resampler, ring buffer, the effects, the effect chain, the audio processor/engine, the node graph and its multi-device engine, and the settings. |
| `src/VoiceChanger.Audio.Wasapi` | Windows audio backend built on [NAudio](https://github.com/naudio/NAudio) (WASAPI). |
| `src/VoiceChanger.App` | The desktop app, built with [Avalonia UI](https://avaloniaui.net/) and CommunityToolkit.Mvvm. |
| `tests/VoiceChanger.Core.Tests` | Checks the DSP output: filter responses, pitch shifting and pitch detection accuracy, formant preservation on synthetic vowels, auto-tune correction, resampling, drift compensation with simulated device clocks, and settings. |
| `tests/VoiceChanger.App.Tests` | Headless UI tests that load the real window with fake audio devices, drive the node editor with the mouse, and render screenshots. |

### Adding an effect

1. Derive from `AudioEffect`. Declare parameters with `AddParameter(...)` and presets with
   `AddPreset(...)`, then implement `ProcessCore`. It receives interleaved 32-bit float samples.
2. Register the effect in `EffectRegistry.CreateDefault()`.

The UI builds a generic editor from the parameters (sliders, choices, switches), so the new effect
appears under **Add effect**, and as a node under **Add node**, without any UI code. Settings are saved
and restored automatically. An effect that needs a second signal (like the vocoder's carrier) also
implements `ISidechainEffect`; its node then gets a second input socket.

```csharp
public sealed class GainEffect : AudioEffect
{
    private readonly EffectParameter _gain;
    private float _linear = 1;

    public GainEffect() : base("gain", "Gain") =>
        _gain = AddParameter(EffectParameter.Continuous("gain", "Gain", -24, 24, 0, "dB"));

    protected override void OnPrepare() { }
    protected override void OnReset() { }
    protected override void OnParametersChanged() => _linear = MathF.Pow(10, _gain.Value / 20);

    protected override void ProcessCore(Span<float> samples)
    {
        foreach (ref float s in samples) s *= _linear;
    }
}
```

Another audio API (for example PulseAudio or Core Audio) can be added by implementing
`IAudioBackend` and returning it from `AudioBackendFactory`.
