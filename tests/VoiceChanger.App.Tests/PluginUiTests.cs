using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.LogicalTree;
using VoiceChanger.App.ViewModels;
using VoiceChanger.App.Views;
using VoiceChanger.Core.Effects;
using VoiceChanger.Core.Graph;
using VoiceChanger.Core.Settings;
using VoiceChanger.Core.Tests;
using VoiceChanger.Plugins;
using VoiceChanger.Plugins.Tests;

namespace VoiceChanger.App.Tests;

public sealed class PluginUiTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "VoiceChangerUiTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private SettingsStore Store() => new(Path.Combine(_directory, "settings.json"));

    [AvaloniaFact]
    public void Plugins_are_listed_by_vendor_in_both_add_menus()
    {
        var registry = EffectRegistry.CreateDefault();
        registry.Register(Plugin("Fake Delay", "Fake Audio"));
        registry.Register(Plugin("Fake Chorus", "Fake Audio"));
        registry.Register(Plugin("Other Reverb", "Other Vendor"));
        var vm = new MainWindowViewModel(new FakeAudioBackend(), Store(), registry);

        var plugins = vm.AddEffectMenu[^1];
        Assert.Equal(AddMenuEntry.PluginsName, plugins.Name);
        Assert.Equal(["Fake Audio", "Other Vendor"], plugins.Items!.Select(i => i.Name));
        Assert.Equal(["Fake Chorus", "Fake Delay"], plugins.Items![0].Items!.Select(i => i.Name));
        Assert.Equal(["Pitch Shifter", "Auto Tune", "Equalizer", "Vocoder"], vm.AddEffectMenu.SkipLast(1).Select(e => e.Name));

        plugins.Items![1].Items![0].Command!.Execute(null);
        Assert.Equal("Other Reverb", vm.Effects[^1].Name);

        // In the node menu the plug-ins follow the effects.
        var names = vm.NodeEditor.AddNodeMenu.Select(e => e.Name).ToList();
        Assert.Equal(names.IndexOf("Vocoder") + 1, names.IndexOf(AddMenuEntry.PluginsName));
        Assert.Equal(names.IndexOf("Mixer") - 1, names.IndexOf(AddMenuEntry.PluginsName));
        vm.NodeEditor.AddNodeMenu.Single(e => e.Name == AddMenuEntry.PluginsName).Items![0].Items![1].Command!.Execute(null);
        Assert.Equal("Fake Delay", vm.NodeEditor.SelectedNode?.Title);

        // The menus' item theme turns entries into menu items with submenus.
        Assert.True(Avalonia.Application.Current!.TryGetResource("AddMenuItemTheme", null, out var theme));
        var menu = new Menu { ItemsSource = vm.AddEffectMenu, ItemContainerTheme = (Avalonia.Styling.ControlTheme)theme! };
        var window = new Window { Content = menu };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var items = menu.GetLogicalChildren().OfType<MenuItem>().ToList();
        Assert.Equal(vm.AddEffectMenu.Select(e => e.Name), items.Select(i => i.Header as string));
        Assert.Equal(2, items[^1].ItemCount);
        Assert.Equal(vm.AddEffectMenu[0].Description, ToolTip.GetTip(items[0]));
        window.Close();
    }

    [AvaloniaFact]
    public void Plugins_found_later_join_the_menus()
    {
        var registry = EffectRegistry.CreateDefault();
        var vm = new MainWindowViewModel(new FakeAudioBackend(), Store(), registry);
        Assert.DoesNotContain(vm.AddEffectMenu, e => e.Name == AddMenuEntry.PluginsName);

        var plugin = Plugin("Late Delay", "Late Audio");
        registry.Register(plugin);
        vm.AddPlugins([plugin]);

        Assert.Contains(vm.AddEffectOptions, o => o.Name == "Late Delay");
        Assert.Contains(vm.AddEffectMenu, e => e.Name == AddMenuEntry.PluginsName);
        Assert.Contains(vm.NodeEditor.AddNodeOptions, o => o.Name == "Late Delay");
    }

    [AvaloniaFact]
    public void A_missing_plugin_keeps_its_card_and_its_settings()
    {
        var store = Store();
        var saved = new EffectSettings
        {
            Type = "vst3:00112233445566778899AABBCCDDEEFF",
            Name = "Gone Reverb",
            State = Convert.ToBase64String([1, 2, 3]),
            Parameters = new() { ["4"] = 0.5f },
            Collapsed = true,
        };
        store.Save(new AppSettings { Effects = [saved] });

        var vm = new MainWindowViewModel(new FakeAudioBackend(), store);
        var card = Assert.Single(vm.Effects);
        Assert.Equal("Gone Reverb (missing)", card.Name);
        Assert.Equal(MissingEffect.Explanation, card.Description);
        Assert.True(card.IsCollapsed);

        vm.SaveSettings();
        var again = Assert.Single(store.Load().Effects!);
        Assert.Equal(saved.Type, again.Type);
        Assert.Equal(saved.Name, again.Name);
        Assert.Equal(saved.State, again.State);
        Assert.Equal(0.5f, again.Parameters["4"]);
    }

    [AvaloniaFact]
    public void A_plugin_gets_generic_controls_and_keeps_its_settings()
    {
        var info = TestPlugin.Require();
        var registry = EffectRegistry.CreateDefault();
        registry.Register(PluginCatalog.Describe(info));
        var store = Store();
        var vm = new MainWindowViewModel(new FakeAudioBackend(), store, registry);

        vm.AddEffectOptions.Single(o => o.Name == TestPlugin.ClassName).Command.Execute(null);
        var card = Assert.IsType<PluginEffectViewModel>(vm.Effects[^1]);
        Assert.Equal(["Gain", "Latency", "Sidechain Mix", "Hold"], card.Parameters.Select(p => p.Name));
        Assert.Equal(["Default", "Loud", "Crushed"], card.Presets.Select(p => p.Name));
        Assert.Equal("Runs inside VoiceChanger", card.HostingText);
        Assert.Equal(OperatingSystem.IsWindows(), card.HasWindow);
        Assert.False(card.HasProblem);

        // The card shows the plug-in's controls.
        var window = new MainWindow { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.Single(window.GetLogicalDescendants().OfType<PluginEffectEditor>());
        window.Close();

        // A program sets the plug-in's values, and the card shows its name.
        card.SelectedPreset = card.Presets[1];
        Assert.Equal(TestPlugin.GainValue(6), card.Parameters[0].Parameter.Value, 4);
        Assert.Equal("Loud", card.Summary);
        Assert.True(card.Parameters[0].IsSlider);
        Assert.True(card.Parameters[1].IsChoice);
        Assert.True(card.Parameters[2].IsToggle);
        card.Parameters[0].Position = TestPlugin.GainValue(-6);
        card.Parameters[1].SelectedIndex = 1;
        vm.SaveSettings();

        var restored = new MainWindowViewModel(new FakeAudioBackend(), store, registry);
        var effect = Assert.IsAssignableFrom<PluginEffect>(restored.Effects[^1].Effect);
        Assert.Equal(TestPlugin.GainValue(-6), effect.GetParameter("0").Value, 4);
        Assert.Equal(1, effect.GetParameter("1").Value);
    }

    [Fact]
    public void The_app_scans_a_plugin_in_a_run_of_its_own()
    {
        var info = TestPlugin.Require();
        string app = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "VoiceChanger.exe" : "VoiceChanger");

        var found = PluginScanner.ScanInSeparateProcess(info.Path, app, []);

        var plugin = Assert.Single(found);
        Assert.Equal(info.ClassId, plugin.ClassId);
        Assert.Equal(info.Path, plugin.Path);

        var error = Assert.Throws<PluginException>(() => PluginScanner.ScanInSeparateProcess(Path.Combine(_directory, "None.vst3"), app, []));
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }

    private static EffectDescriptor Plugin(string name, string vendor) =>
        new("vst3:" + Guid.NewGuid().ToString("N").ToUpperInvariant(), name, $"VST3 plug-in by {vendor}.", () => new PassEffect(name))
        {
            Group = vendor,
        };

    private sealed class PassEffect(string name) : AudioEffect("vst3:fake", name)
    {
        protected override void OnPrepare()
        {
        }

        protected override void OnParametersChanged()
        {
        }

        protected override void OnReset()
        {
        }

        protected override void ProcessCore(Span<float> interleaved)
        {
        }
    }
}
