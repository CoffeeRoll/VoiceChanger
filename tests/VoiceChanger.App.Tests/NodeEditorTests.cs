using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VoiceChanger.App.Controls;
using VoiceChanger.App.ViewModels;
using VoiceChanger.App.Views;
using VoiceChanger.Core.Dsp;
using VoiceChanger.Core.Effects;
using VoiceChanger.Core.Graph;
using VoiceChanger.Core.Settings;
using VoiceChanger.Core.Tests;

namespace VoiceChanger.App.Tests;

public sealed class NodeEditorTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "VoiceChangerNodeTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private SettingsStore Store() => new(Path.Combine(_directory, "settings.json"));

    /// <summary>Shows the window in node editor mode, laid out and rendered.</summary>
    private static (MainWindow Window, MainWindowViewModel ViewModel, NodeEditorViewModel Editor) ShowNodes(MainWindowViewModel viewModel)
    {
        var window = new MainWindow { DataContext = viewModel, Width = 1400, Height = 860 };
        window.Show();
        viewModel.Mode = EditorMode.Nodes;
        Render(window);
        return (window, viewModel, viewModel.NodeEditor);
    }

    private static void Render(Window window)
    {
        for (int i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.CaptureRenderedFrame();
        }
    }

    private static NodeViewModel Node<T>(NodeEditorViewModel editor, int index = 0)
        where T : AudioNode => editor.Nodes.Where(n => n.Node is T).ElementAt(index);

    /// <summary>Centre of the socket of <paramref name="port"/> in window coordinates.</summary>
    private static Point SocketCenter(Window window, PortViewModel port)
    {
        var socket = window.GetVisualDescendants().OfType<NodeSocket>().Single(s => s.Port == port);
        return socket.TranslatePoint(new Point(socket.Bounds.Width / 2, socket.Bounds.Height / 2), window)!.Value;
    }

    private static Point CenterOf(Window window, Control control) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;

    private static void Drag(Window window, Point from, Point to, MouseButton button = MouseButton.Left)
    {
        window.MouseMove(from, RawInputModifiers.None);
        window.MouseDown(from, button, RawInputModifiers.None);
        var middle = from + (to - from) / 2;
        window.MouseMove(middle, button == MouseButton.Left ? RawInputModifiers.LeftMouseButton : RawInputModifiers.MiddleMouseButton);
        window.MouseMove(to, button == MouseButton.Left ? RawInputModifiers.LeftMouseButton : RawInputModifiers.MiddleMouseButton);
        window.MouseUp(to, button, RawInputModifiers.None);
        Render(window);
    }

    private static NodeEditorView Canvas(Window window) => window.GetVisualDescendants().OfType<NodeEditorView>().Single();

    /// <summary>A point on the empty canvas (well below the default graph).</summary>
    private static Point EmptySpot(Window window)
    {
        var canvas = Canvas(window);
        return canvas.TranslatePoint(new Point(canvas.Bounds.Width * 0.5, canvas.Bounds.Height * 0.85), window)!.Value;
    }

    [AvaloniaFact]
    public void Opening_the_node_editor_turns_the_chain_into_a_graph()
    {
        var (window, vm, editor) = ShowNodes(new MainWindowViewModel(new FakeAudioBackend(), Store()));

        Assert.Equal(
            ["input", "effect:pitch-shift", "effect:equalizer", "mixer", "output"],
            editor.Graph.Nodes.Select(n => n.TypeId));
        Assert.Equal(4, editor.Wires.Count);
        Assert.Equal("mic", ((DeviceNode)editor.Graph.Nodes[0]).DeviceId);
        Assert.Equal("speakers", ((DeviceNode)editor.Graph.Nodes[^1]).DeviceId);
        Assert.Equal("+4 semitones", Node<EffectNode>(editor).Summary);

        // The chain's effects were copied: changing one does not change the other.
        var chainPitch = (PitchShiftEffect)vm.Effects.OfType<PitchShiftViewModel>().Single().Effect;
        Assert.NotSame(chainPitch, ((EffectNode)editor.Graph.Nodes[1]).Effect);

        Assert.Equal(5, window.GetVisualDescendants().OfType<NodeView>().Count(v => v.IsEffectivelyVisible));
        Assert.False(window.GetVisualDescendants().OfType<ItemsControl>().Single(c => c.Name == "EffectList").IsEffectivelyVisible);

        // Every wire ends at its sockets.
        foreach (var wire in editor.Wires)
        {
            Assert.NotEqual(default, wire.From.Anchor);
            Assert.True(wire.To.Anchor.X > wire.From.Anchor.X, $"{wire.Connection} goes from {wire.From.Anchor} to {wire.To.Anchor}");
        }

        // All nodes fit in the view.
        var canvas = Canvas(window);
        foreach (var node in window.GetVisualDescendants().OfType<NodeView>())
        {
            var topLeft = node.TranslatePoint(default, canvas)!.Value;
            Assert.InRange(topLeft.X, 0, canvas.Bounds.Width - 50);
            Assert.InRange(topLeft.Y, 0, canvas.Bounds.Height - 50);
        }

        window.Close();
        vm.Dispose();
    }

    [AvaloniaFact]
    public void Dragging_from_an_output_to_an_input_connects_them()
    {
        var (window, vm, editor) = ShowNodes(new MainWindowViewModel(new FakeAudioBackend(), Store()));
        var mixer = Node<MixerNode>(editor);
        var tone = editor.AddNode(NodeRegistry.Default.Find(ToneNode.Type)!, new Point(mixer.X - 300, mixer.Y + 260));
        Render(window);

        Drag(window, SocketCenter(window, tone.Outputs[0]), SocketCenter(window, mixer.Inputs[1]));

        var wire = editor.Graph.ConnectionTo(mixer.Inputs[1].Port);
        Assert.NotNull(wire);
        Assert.Same(tone.Node, wire.From.Node);
        Assert.True(mixer.Inputs[1].IsConnected);
        Assert.True(tone.Outputs[0].IsConnected);
        Assert.Equal(5, editor.Wires.Count);

        // Wires can also be drawn from an empty input, and snap to a socket dropped a little off target.
        var output = Node<OutputDeviceNode>(editor);
        editor.Disconnect(output.Inputs[0]);
        Render(window);
        Drag(window, SocketCenter(window, output.Inputs[0]), SocketCenter(window, tone.Outputs[0]) + new Point(8, 6));
        Assert.Same(tone.Node, editor.Graph.ConnectionTo(output.Inputs[0].Port)!.From.Node);

        window.Close();
        vm.Dispose();
    }

    [AvaloniaFact]
    public void Dragging_a_wire_off_an_input_moves_or_removes_it()
    {
        var (window, vm, editor) = ShowNodes(new MainWindowViewModel(new FakeAudioBackend(), Store()));
        var mixer = Node<MixerNode>(editor);
        var eq = editor.Graph.Nodes.OfType<EffectNode>().Single(n => n.Effect is EqualizerEffect);

        // Pick the wire up from input 1 and drop it on input 2.
        Drag(window, SocketCenter(window, mixer.Inputs[0]), SocketCenter(window, mixer.Inputs[1]));
        Assert.Null(editor.Graph.ConnectionTo(mixer.Inputs[0].Port));
        Assert.Same(eq, editor.Graph.ConnectionTo(mixer.Inputs[1].Port)!.From.Node);

        // Dropping it on empty space disconnects it.
        Drag(window, SocketCenter(window, mixer.Inputs[1]), EmptySpot(window));
        Assert.Null(editor.Graph.ConnectionTo(mixer.Inputs[1].Port));
        Assert.Equal(3, editor.Wires.Count);
        Assert.False(mixer.Inputs[1].IsConnected);

        window.Close();
        vm.Dispose();
    }

    [AvaloniaFact]
    public void Loops_are_refused_with_an_explanation()
    {
        var (window, vm, editor) = ShowNodes(new MainWindowViewModel(new FakeAudioBackend(), Store()));
        var mixer = Node<MixerNode>(editor);
        var pitch = Node<EffectNode>(editor);
        var wiresBefore = editor.Graph.Connections.ToList();

        Drag(window, SocketCenter(window, mixer.Outputs[0]), SocketCenter(window, pitch.Inputs[0]));

        Assert.Equal(wiresBefore, editor.Graph.Connections);
        Assert.Contains("loop", editor.Hint);
        var hint = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "NodeHint");
        Assert.True(hint.IsEffectivelyVisible);

        // The next successful action clears the hint.
        editor.AddNode(NodeRegistry.Default.Find(ToneNode.Type)!);
        Assert.Null(editor.Hint);

        window.Close();
        vm.Dispose();
    }

    [AvaloniaFact]
    public void Nodes_move_and_the_canvas_pans_and_zooms()
    {
        var (window, vm, editor) = ShowNodes(new MainWindowViewModel(new FakeAudioBackend(), Store()));
        var mixer = Node<MixerNode>(editor);
        var title = window.GetVisualDescendants().OfType<NodeView>().Single(v => v.DataContext == mixer)
            .GetVisualDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("nodeTitle"));
        double x = mixer.X, y = mixer.Y, zoom = editor.Zoom;

        // Dragging a node moves it (in canvas units) and selects it.
        var start = CenterOf(window, title);
        Drag(window, start, start + new Point(60, 40));
        Assert.Equal(x + Math.Round(60 / zoom), mixer.X, 1.0);
        Assert.Equal(y + Math.Round(40 / zoom), mixer.Y, 1.0);
        Assert.Same(mixer, editor.SelectedNode);

        // The wires follow.
        var socket = SocketCenter(window, mixer.Inputs[0]);
        var anchor = Canvas(window).GetVisualDescendants().OfType<Panel>().Single(p => p.Name == "World");
        Assert.Equal(socket, anchor.TranslatePoint(mixer.Inputs[0].Anchor, window)!.Value);

        // Dragging the background pans; a click on it clears the selection.
        double panX = editor.PanX, panY = editor.PanY;
        var empty = EmptySpot(window);
        Drag(window, empty, empty + new Point(-100, -30));
        Assert.Equal(panX - 100, editor.PanX, 0.5);
        Assert.Equal(panY - 30, editor.PanY, 0.5);
        window.MouseDown(empty, MouseButton.Left, RawInputModifiers.None);
        window.MouseUp(empty, MouseButton.Left, RawInputModifiers.None);
        Assert.Null(editor.SelectedNode);

        // Scrolling zooms around the mouse: the point under it stays put.
        var pointer = SocketCenter(window, mixer.Outputs[0]);
        window.MouseMove(pointer, RawInputModifiers.None);
        window.MouseWheel(pointer, new Vector(0, 2), RawInputModifiers.None);
        Render(window);
        Assert.True(editor.Zoom > zoom * 1.2, $"zoom {editor.Zoom}");
        var moved = SocketCenter(window, mixer.Outputs[0]) - pointer;
        Assert.True(Math.Abs(moved.X) < 1.5 && Math.Abs(moved.Y) < 1.5, $"moved by {moved}");

        editor.FitViewCommand.Execute(null);
        Assert.Equal(zoom, editor.Zoom, 0.05);

        window.Close();
        vm.Dispose();
    }

    [AvaloniaFact]
    public void Nodes_are_added_selected_duplicated_and_deleted()
    {
        var (window, vm, editor) = ShowNodes(new MainWindowViewModel(new FakeAudioBackend(), Store()));

        vm.NodeEditor.AddNodeOptions.Single(o => o.Name == "Audio Input").Command.Execute(null);
        Render(window);
        var added = Assert.IsType<DeviceNodeViewModel>(editor.SelectedNode);
        Assert.Equal("mic", added.SelectedDevice?.Id);
        Assert.Equal(6, window.GetVisualDescendants().OfType<NodeView>().Count());

        // The inspector shows the selected node; renaming it changes its title.
        var inspector = window.GetVisualDescendants().OfType<NodeInspector>().Single();
        var name = inspector.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "NodeNameBox");
        name.Text = "Guitar";
        Assert.Equal("Guitar", added.Title);
        Assert.Equal("Guitar", editor.Graph.Nodes[^1].Label);

        editor.DuplicateSelectedCommand.Execute(null);
        var copy = editor.SelectedNode!;
        Assert.NotSame(added, copy);
        Assert.Equal("Guitar", copy.Title);
        Assert.Equal(added.X + 30, copy.X);

        // The Delete key removes the selected node.
        Canvas(window).Focus();
        window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
        window.KeyRelease(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
        Assert.DoesNotContain(copy, editor.Nodes);
        Assert.Null(editor.SelectedNode);

        // Right-clicking a node selects it (and offers its own menu, not the canvas's "add" menu).
        var mixerView = window.GetVisualDescendants().OfType<NodeView>().Single(v => v.DataContext is MixerNodeViewModel);
        var mixerTitle = CenterOf(window, mixerView.GetVisualDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("nodeTitle")));
        window.MouseDown(mixerTitle, MouseButton.Right, RawInputModifiers.None);
        window.MouseUp(mixerTitle, MouseButton.Right, RawInputModifiers.None);
        Assert.Same(mixerView.DataContext, editor.SelectedNode);
        Assert.Equal(6, editor.Nodes.Count);

        // The inspector's delete button removes the selected node.
        editor.SelectedNode = added;
        Render(window);
        inspector.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "DeleteNodeButton").Command!.Execute(null);
        Assert.Equal(5, editor.Nodes.Count);

        window.Close();
        vm.Dispose();
    }

    [AvaloniaFact]
    public void Effect_nodes_use_the_full_effect_editors()
    {
        var (window, vm, editor) = ShowNodes(new MainWindowViewModel(new FakeAudioBackend(), Store()));
        var autoTune = Assert.IsType<EffectNodeViewModel>(
            editor.AddNode(NodeRegistry.Default.Find(EffectNode.TypePrefix + AutoTuneEffect.Type)!));
        Render(window);

        // The inspector shows the auto tune's note strip: clicking notes picks a custom scale.
        var inspector = window.GetVisualDescendants().OfType<NodeInspector>().Single();
        var chips = inspector.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("chip")).ToList();
        Assert.Equal(12, chips.Count);
        chips.Single(c => (string?)c.Content == "C#").Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal((int)ScaleType.Custom, ((AutoTuneEffect)((EffectNode)autoTune.Node).Effect).Scale.Value);
        Assert.Equal("custom scale (11 notes) · 20 ms retune", autoTune.Summary);

        // Presets that lower the voice keep it natural in the graph too.
        var pitch = editor.Nodes.OfType<EffectNodeViewModel>().First(n => n.Effect is PitchShiftViewModel);
        pitch.Effect.SelectedPreset = pitch.Effect.Presets.Single(p => p.Name == "Deep");
        var effect = (PitchShiftEffect)((EffectNode)pitch.Node).Effect;
        Assert.Equal(PitchShiftEffect.VoicePulseMode, effect.EffectiveMode);
        Assert.Equal("-7 semitones · formants -3.0 semitones (pulse)", pitch.Summary);

        window.Close();
        vm.Dispose();
    }

    [AvaloniaFact]
    public void The_synchronizer_lines_up_two_microphones_by_listening_for_a_clap()
    {
        var backend = new FakeAudioBackend();
        var (window, vm, editor) = ShowNodes(new MainWindowViewModel(backend, Store()));
        var graph = editor.Graph;
        var registry = NodeRegistry.Default;
        foreach (var node in editor.Nodes.Where(n => n.Node is EffectNode or MixerNode).ToList())
        {
            editor.RemoveNode(node);
        }

        // Two microphones, the second delivering everything 30 ms late, lined up and mixed.
        var mic = graph.Nodes.OfType<InputDeviceNode>().Single();
        var speakers = graph.Nodes.OfType<OutputDeviceNode>().Single();
        var line = (InputDeviceNode)editor.AddNode(registry.Find(InputDeviceNode.Type)!).Node;
        line.DeviceId = "line";
        var syncViewModel = Assert.IsType<SyncNodeViewModel>(editor.AddNode(registry.Find(SyncNode.Type)!));
        var sync = (SyncNode)syncViewModel.Node;
        var mixer = (MixerNode)editor.AddNode(registry.Find(MixerNode.Type)!).Node;
        graph.Connect(mic.Outputs[0], sync.Inputs[0]);
        graph.Connect(line.Outputs[0], sync.Inputs[1]);
        graph.Connect(sync.Outputs[0], mixer.Inputs[0]);
        graph.Connect(sync.Outputs[1], mixer.Inputs[1]);
        graph.Connect(mixer.Outputs[0], speakers.Inputs[0]);
        editor.SelectedNode = syncViewModel;
        Render(window);

        var inspector = window.GetVisualDescendants().OfType<NodeInspector>().Single();
        var listen = inspector.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ListenButton");
        Assert.Equal(2, syncViewModel.Channels.Count);
        Assert.Equal("2 inputs · effect delays matched", syncViewModel.Summary);

        // Listening needs sound coming in.
        listen.Command!.Execute(null);
        Assert.False(syncViewModel.IsListening);
        Assert.Contains("Start", syncViewModel.ListenStatus);

        vm.Start();
        var main = backend.Outputs.Single(o => o.DeviceId == "speakers" && o.IsStarted);
        var micStream = backend.Inputs.Single(i => i.DeviceId == "mic");
        var lineStream = backend.Inputs.Single(i => i.DeviceId == "line");
        listen.Command.Execute(null);
        Assert.True(syncViewModel.IsListening);
        Assert.Equal("Stop listening", syncViewModel.ListenButtonText);

        var random = new Random(2);
        var claps = new float[(int)(5 * 48000)];
        foreach (double at in new[] { 0.6, 1.45, 2.3, 3.1, 3.7 })
        {
            for (int n = 0; n < 960; n++)
            {
                claps[(int)(at * 48000) + n] = (float)(0.6 * Math.Exp(-n / 200.0) * (random.NextDouble() * 2 - 1));
            }
        }

        var late = new float[claps.Length];
        claps.AsSpan(0, claps.Length - 1440).CopyTo(late.AsSpan(1440));
        for (int offset = 0; offset + 480 <= claps.Length; offset += 480)
        {
            micStream.Push(claps.AsSpan(offset, 480));
            lineStream.Push(late.AsSpan(offset, 480));
            main.Pull(480);
            if (offset % 1440 == 0)
            {
                vm.UpdateStatus();
            }
        }

        for (int i = 0; i < 200 && syncViewModel.IsListening; i++)
        {
            Thread.Sleep(10);
            vm.UpdateStatus();
        }

        Assert.False(syncViewModel.IsListening);
        Assert.Contains("input 2 was", syncViewModel.ListenStatus);
        Assert.Contains("ms late", syncViewModel.ListenStatus);
        Assert.Equal(-30, sync.Offsets[1].Value, 1.0);
        Assert.Equal("2 inputs · effect delays matched · 1 input moved", syncViewModel.Summary);

        // The first microphone now waits for the second.
        for (int block = 0; block < 6; block++)
        {
            micStream.Push(claps.AsSpan(0, 480));
            lineStream.Push(late.AsSpan(0, 480));
            main.Pull(480);
        }

        vm.UpdateStatus();
        Assert.StartsWith("waits 30", syncViewModel.Channels[0].Delay);
        Assert.Equal("no delay", syncViewModel.Channels[1].Delay);
        Assert.EndsWith("ms earlier", editor.Find(sync)!.Inputs[1].Detail);

        window.Close();
        vm.Dispose();
    }

    [AvaloniaFact]
    public void The_vocoder_node_has_a_carrier_socket()
    {
        var (window, vm, editor) = ShowNodes(new MainWindowViewModel(new FakeAudioBackend(), Store()));
        var vocoder = Assert.IsType<EffectNodeViewModel>(
            editor.AddNode(NodeRegistry.Default.Find(EffectNode.TypePrefix + VocoderEffect.Type)!));
        var tone = editor.AddNode(NodeRegistry.Default.Find(ToneNode.Type)!);
        Render(window);

        Assert.Equal(["Voice", "Carrier"], vocoder.Inputs.Select(p => p.Name));
        editor.Graph.Connect(tone.Node.Outputs[0], vocoder.Node.Inputs[1]);
        Render(window);
        Assert.True(vocoder.Inputs[1].IsConnected);
        var view = window.GetVisualDescendants().OfType<NodeView>().Single(v => v.DataContext == vocoder);
        Assert.Equal(3, view.GetVisualDescendants().OfType<NodeSocket>().Count());

        // Its settings are in the inspector like any effect's.
        editor.SelectedNode = vocoder;
        Render(window);
        var inspector = window.GetVisualDescendants().OfType<NodeInspector>().Single();
        Assert.Contains(inspector.GetVisualDescendants().OfType<ParameterEditor>(), e => (e.DataContext as ParameterViewModel)?.Name == "Synth");

        window.Close();
        vm.Dispose();
    }

    [AvaloniaFact]
    public void The_mixer_inspector_adds_inputs_and_mutes_them()
    {
        var (window, vm, editor) = ShowNodes(new MainWindowViewModel(new FakeAudioBackend(), Store()));
        var mixer = Assert.IsType<MixerNodeViewModel>(Node<MixerNode>(editor));
        editor.SelectedNode = mixer;
        Render(window);

        var inspector = window.GetVisualDescendants().OfType<NodeInspector>().Single();
        inspector.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "AddMixerInputButton").Command!.Execute(null);
        Render(window);

        Assert.Equal(3, mixer.Inputs.Count);
        Assert.Equal(3, mixer.Channels.Count);
        var view = window.GetVisualDescendants().OfType<NodeView>().Single(v => v.DataContext == mixer);
        Assert.Equal(4, view.GetVisualDescendants().OfType<NodeSocket>().Count());

        mixer.Channels[2].Mute.IsOn = true;
        Assert.Equal("Muted", mixer.Inputs[2].Detail);
        mixer.Channels[0].Gain.Parameter.Value = -6;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("-6.0 dB", mixer.Inputs[0].Detail);
        Assert.True(mixer.Channels[0].IsConnected);
        Assert.False(mixer.Channels[1].IsConnected);

        window.Close();
        vm.Dispose();
    }

    [AvaloniaFact]
    public void Two_microphones_play_through_the_mixer()
    {
        var backend = new FakeAudioBackend();
        var (window, vm, editor) = ShowNodes(new MainWindowViewModel(backend, Store()));
        var mixer = Assert.IsType<MixerNodeViewModel>(Node<MixerNode>(editor));
        var line = Assert.IsType<DeviceNodeViewModel>(editor.AddNode(NodeRegistry.Default.Find(InputDeviceNode.Type)!, new Point(0, 300)));
        line.SelectedDevice = line.Devices.Single(d => d.Id == "line");
        Assert.True(editor.TryConnect(line.Outputs[0], mixer.Inputs[1]));

        vm.Start();
        Assert.True(vm.IsRunning);
        Assert.Equal(["mic", "line"], backend.Inputs.Select(i => i.DeviceId));

        double phase = 0;
        var speakers = backend.Outputs.Single(o => o.IsStarted);
        for (int block = 0; block < 60; block++)
        {
            var samples = new float[480];
            for (int i = 0; i < samples.Length; i++, phase++)
            {
                samples[i] = (float)(0.3 * Math.Sin(2 * Math.PI * 300 * phase / 48000));
            }

            backend.Inputs[0].Push(samples);
            backend.Inputs[1].Push(samples);
            speakers.Pull(480);
            if (block % 3 == 0)
            {
                vm.UpdateStatus();
            }
        }

        Assert.StartsWith("Running · 2 inputs → 1 output · 48 kHz", vm.StatusText);
        Assert.True(mixer.Channels[1].LevelDb > -20, $"input 2 at {mixer.Channels[1].LevelDb} dB");
        Assert.True(line.LevelDb > -20);
        Assert.True(Node<OutputDeviceNode>(editor).LevelDb > -20);

        // Adding a second output while playing opens it without stopping.
        var cable = Assert.IsType<DeviceNodeViewModel>(editor.AddNode(NodeRegistry.Default.Find(OutputDeviceNode.Type)!));
        cable.SelectedDevice = cable.Devices.Single(d => d.Id == "cable");
        Assert.True(editor.TryConnect(mixer.Outputs[0], cable.Inputs[0]));
        Assert.True(vm.IsRunning);
        Assert.Contains(backend.Outputs, o => o.DeviceId == "cable" && o.IsStarted);
        vm.UpdateStatus();
        Assert.Contains("2 inputs → 2 outputs", vm.StatusText);

        vm.Stop();
        Assert.All(backend.Inputs, i => Assert.False(i.IsStarted));
        Assert.Equal(-60, mixer.LevelDb);

        window.Close();
        vm.Dispose();
    }

    [AvaloniaFact]
    public void Switching_editors_while_playing_moves_the_audio_over()
    {
        var backend = new FakeAudioBackend();
        var window = new MainWindow { DataContext = new MainWindowViewModel(backend, Store()) };
        var vm = (MainWindowViewModel)window.DataContext!;
        window.Show();
        vm.Start();
        var chainInput = backend.Inputs.Single();

        window.GetVisualDescendants().OfType<RadioButton>().Single(b => b.Name == "NodeModeButton").IsChecked = true;
        Assert.Equal(EditorMode.Nodes, vm.Mode);
        Assert.True(vm.IsRunning);
        Assert.True(chainInput.IsDisposed);
        Assert.True(backend.Inputs[^1].IsStarted);

        // Without an output device the graph cannot play.
        var output = (DeviceNode)vm.NodeEditor.Graph.Nodes.Single(n => n is OutputDeviceNode);
        vm.NodeEditor.RemoveNode(vm.NodeEditor.Find(output)!);
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.IsRunning);
        Assert.Contains("Audio Output", vm.ErrorMessage);

        vm.IsChainMode = true;
        vm.Start();
        Assert.True(vm.IsRunning);
        Assert.True(backend.Inputs[^1].IsStarted);

        window.Close();
        vm.Dispose();
    }

    [AvaloniaFact]
    public void The_graph_and_the_editor_mode_are_remembered()
    {
        var store = Store();
        var first = new MainWindowViewModel(new FakeAudioBackend(), store);
        first.Mode = EditorMode.Nodes;
        var tone = first.NodeEditor.AddNode(NodeRegistry.Default.Find(ToneNode.Type)!, new Point(100, 400));
        tone.Label = "Drone";
        first.NodeEditor.TryConnect(tone.Outputs[0], first.NodeEditor.Nodes.Single(n => n.Node is MixerNode).Inputs[1]);
        first.Dispose();

        var second = new MainWindowViewModel(new FakeAudioBackend(), store);
        Assert.Equal(EditorMode.Nodes, second.Mode);
        Assert.Equal(6, second.NodeEditor.Nodes.Count);
        var restored = second.NodeEditor.Nodes.Single(n => n.Node is ToneNode);
        Assert.Equal("Drone", restored.Title);
        Assert.Equal((-10, 360), (restored.X, restored.Y));
        Assert.Equal(5, second.NodeEditor.Wires.Count);
        Assert.Contains("\"mode\": \"Nodes\"", File.ReadAllText(store.FilePath));

        // The chain is still there for the chain editor.
        Assert.Equal(2, second.Effects.Count);
        second.Dispose();
    }

    [AvaloniaFact]
    public void Captures_a_screenshot_of_the_node_editor()
    {
        var backend = new FakeAudioBackend();
        var vm = new MainWindowViewModel(backend, Store());
        vm.Mode = EditorMode.Nodes;
        var editor = vm.NodeEditor;
        var registry = NodeRegistry.Default;

        // Two microphones (one auto-tuned) and a quiet drone into the mixer, played on two devices.
        var graph = editor.Graph;
        var mic = (InputDeviceNode)graph.Nodes[0];
        mic.Label = "Vocals";
        var pitch = graph.Nodes.OfType<EffectNode>().First();
        var eq = graph.Nodes.OfType<EffectNode>().Last();
        var mixerNode = graph.Nodes.OfType<MixerNode>().Single();
        var speakers = graph.Nodes.OfType<OutputDeviceNode>().Single();
        mixerNode.InputCount.Value = 3;
        graph.Disconnect(graph.ConnectionTo(mixerNode.Inputs[0])!);
        graph.Disconnect(graph.ConnectionTo(speakers.Inputs[0])!);
        graph.Connect(pitch.Outputs[0], mixerNode.Inputs[0]);
        graph.Connect(mixerNode.Outputs[0], eq.Inputs[0]);
        graph.Connect(eq.Outputs[0], speakers.Inputs[0]);

        var line = (InputDeviceNode)editor.AddNode(registry.Find(InputDeviceNode.Type)!).Node;
        line.DeviceId = "line";
        line.Label = "Backing singer";
        var autoTune = (EffectNode)editor.AddNode(registry.Find(EffectNode.TypePrefix + AutoTuneEffect.Type)!).Node;
        var tone = (ToneNode)editor.AddNode(registry.Find(ToneNode.Type)!).Node;
        tone.Label = "Drone";
        tone.Frequency.Value = 110;
        tone.Level.Value = -30;
        var cable = (OutputDeviceNode)editor.AddNode(registry.Find(OutputDeviceNode.Type)!).Node;
        cable.DeviceId = "cable";
        cable.Label = "To Discord";
        graph.Connect(line.Outputs[0], autoTune.Inputs[0]);
        graph.Connect(autoTune.Outputs[0], mixerNode.Inputs[1]);
        graph.Connect(tone.Outputs[0], mixerNode.Inputs[2]);
        graph.Connect(mixerNode.Outputs[0], cable.Inputs[0]);
        mixerNode.Gains[2].Value = -6;

        // A tidy layout: sources on the left, the mixer in the middle, outputs on the right.
        (AudioNode Node, double X, double Y)[] layout =
        [
            (mic, 0, 0), (pitch, 280, 0), (line, 0, 190), (autoTune, 280, 190), (tone, 280, 380),
            (mixerNode, 580, 150), (eq, 860, 60), (speakers, 1140, 60), (cable, 1140, 280),
        ];
        foreach (var (node, x, y) in layout)
        {
            var nodeViewModel = editor.Find(node)!;
            nodeViewModel.X = x;
            nodeViewModel.Y = y;
        }

        var window = new MainWindow { DataContext = vm, Width = 1440, Height = 860 };
        window.Show();
        Render(window);
        editor.FitViewCommand.Execute(null);
        editor.SelectedNode = editor.Find(mixerNode);

        vm.Start();
        Assert.True(vm.IsRunning);
        var main = backend.Outputs.Single(o => o.DeviceId == "speakers" && o.IsStarted);
        double phase = 0;
        for (int block = 0; block < 90; block++)
        {
            var voice = new float[480];
            var backing = new float[480];
            for (int i = 0; i < voice.Length; i++, phase++)
            {
                double t = phase / 48000;
                voice[i] = (float)(0.25 * Math.Sin(2 * Math.PI * 220 * t) + 0.1 * Math.Sin(2 * Math.PI * 440 * t));
                backing[i] = (float)(0.2 * Math.Sin(2 * Math.PI * 330 * t) + 0.08 * Math.Sin(2 * Math.PI * 660 * t));
            }

            backend.Inputs.Single(i => i.DeviceId == "mic").Push(voice);
            backend.Inputs.Single(i => i.DeviceId == "line").Push(backing);
            main.Pull(480);
            backend.Outputs.Single(o => o.DeviceId == "cable" && o.IsStarted).Pull(480);
            if (block % 3 == 0)
            {
                vm.UpdateStatus();
            }
        }

        Render(window);
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        string directory = Path.Combine(FindRepositoryRoot(), "artifacts", "screenshots");
        Directory.CreateDirectory(directory);
        frame.Save(Path.Combine(directory, "node-editor.png"), PngBitmapEncoderOptions.Default);

        window.Close();
        vm.Dispose();
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "VoiceChanger.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? Path.GetTempPath();
    }
}
