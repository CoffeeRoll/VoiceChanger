using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using VoiceChanger.App.Services;
using VoiceChanger.App.ViewModels;
using VoiceChanger.App.Views;
using VoiceChanger.Core.Effects;
using VoiceChanger.Core.Settings;
using VoiceChanger.Core.Tests;
using VoiceChanger.Plugins;
using VoiceChanger.Plugins.Tests;

namespace VoiceChanger.App.Tests;

public sealed class PluginManagerTests : IDisposable
{
    private const string FakeId = "ABCDEF0123456789ABCDEF0123456789";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "VoiceChangerPluginManager", Guid.NewGuid().ToString("N"));

    private string CachePath => Path.Combine(_directory, "plugins.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task Folders_blocking_and_retrying_reach_the_menus_and_are_remembered()
    {
        string folder = Path.Combine(_directory, "MyPlugins");
        CreateBundle(folder, "Fake.vst3", FakeId, "Fake Delay");
        CreateBundle(folder, "Odd.vst3", null, null);
        bool oddScans = false;
        var service = Service(path => oddScans
            ? [new PluginInfo { Path = path, ClassId = "11111111111111111111111111111111", Name = "Odd Reverb", Vendor = "Odd", Subcategories = "Fx" }]
            : throw new PluginException("It crashed while loading."));
        var vm = new MainWindowViewModel(new FakeAudioBackend(), new SettingsStore(Path.Combine(_directory, "settings.json")), plugins: service);
        var manager = vm.PluginManager!;

        // A new folder is scanned: what loads joins the menus, what doesn't is listed with the reason.
        await manager.AddFolderAsync(folder);
        Assert.Contains(manager.Folders, f => f.Path == folder && f.CanRemove);
        Assert.Equal(["Fake Delay"], manager.Plugins.Select(p => p.Name));
        Assert.Equal("It crashed while loading.", Assert.Single(manager.Failed).Reason);
        Assert.Contains(vm.AddEffectOptions, o => o.Name == "Fake Delay");
        Assert.Contains(vm.NodeEditor.AddNodeOptions, o => o.Name == "Fake Delay");

        // Kept out: gone from the menus, listed as blocked.
        manager.Plugins.Single().BlockCommand.Execute(null);
        Assert.Empty(manager.Plugins);
        Assert.Equal("Fake Delay", Assert.Single(manager.Blocked).Name);
        Assert.DoesNotContain(vm.AddEffectOptions, o => o.Name == "Fake Delay");
        Assert.DoesNotContain(vm.NodeEditor.AddNodeOptions, o => o.Name == "Fake Delay");

        // Tried again, the odd one loads now.
        oddScans = true;
        await manager.Failed.Single().RetryCommand.ExecuteAsync(null);
        Assert.Empty(manager.Failed);
        Assert.Contains(vm.AddEffectOptions, o => o.Name == "Odd Reverb");

        manager.SeparateProcesses = false;

        // The next start remembers the folder, the block and the setting.
        var restarted = Service(_ => []);
        Assert.Contains(folder, restarted.Catalog.ExtraFolders);
        Assert.True(restarted.Catalog.IsBlocked(FakeId));
        Assert.False(restarted.SeparateProcesses);
        Assert.Null(restarted.Registry.Find("vst3:" + FakeId));
        Assert.NotNull(restarted.Registry.Find("vst3:11111111111111111111111111111111"));

        // Let back in, it is offered again.
        new PluginManagerViewModel(restarted).Blocked.Single().UnblockCommand.Execute(null);
        Assert.NotNull(restarted.Registry.Find("vst3:" + FakeId));
    }

    [AvaloniaFact]
    public async Task The_window_lists_folders_plugins_and_problems()
    {
        string folder = Path.Combine(_directory, "MyPlugins");
        CreateBundle(folder, "Fake.vst3", FakeId, "Fake Delay");
        var service = Service(_ => []);
        var vm = new MainWindowViewModel(new FakeAudioBackend(), new SettingsStore(Path.Combine(_directory, "settings.json")), plugins: service);
        await vm.PluginManager!.AddFolderAsync(folder);
        var window = new MainWindow { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.True(window.FindControl<Button>("PluginManagerButton")!.IsVisible);

        var manager = window.ShowPluginManager()!;
        Dispatcher.UIThread.RunJobs();

        Assert.Same(manager, window.ShowPluginManager());
        Assert.Equal(PluginCatalog.DefaultFolders.Count + 1, manager.FindControl<ItemsControl>("FolderList")!.ItemCount);
        Assert.Equal(1, manager.FindControl<ItemsControl>("PluginList")!.ItemCount);
        Assert.Equal("1 plug-in.", manager.FindControl<TextBlock>("StatusText")!.Text);
        Assert.True(manager.FindControl<CheckBox>("SeparateProcessesBox")!.IsChecked);
        manager.Close();
        window.Close();
    }

    [AvaloniaFact]
    public void Without_a_plugin_service_there_is_no_manager()
    {
        var vm = new MainWindowViewModel(new FakeAudioBackend(), new SettingsStore(Path.Combine(_directory, "settings.json")));
        Assert.False(vm.HasPlugins);
        Assert.Null(vm.PluginManager);
    }

    [AvaloniaFact]
    public async Task A_plugin_that_keeps_crashing_is_kept_out()
    {
        var info = TestPlugin.Require();
        var service = new PluginService(new PluginCatalog(CachePath), EffectRegistry.CreateDefault(), AppHost(), PluginScanner.ScanModule);
        await service.AddFolderAsync(TestPlugin.Folder!);
        Assert.True(service.Hosting.SeparateProcesses);
        var effect = Assert.IsAssignableFrom<PluginEffect>(service.Registry.Create(info.TypeId));
        Assert.True(effect.RunsInSeparateProcess);
        effect.Prepare(48000, 1);
        var block = new float[64];

        // It crashes, restarts (on the UI thread), and crashes again until it is given up.
        for (int crash = 0; crash <= PluginEffect.MaxRestarts && !effect.HasCrashed; crash++)
        {
            var before = effect.Instance;
            effect.SetNormalized(TestPlugin.Crash, 1);
            effect.Process(block);
            Assert.True(Pump(() => effect.HasCrashed || (effect.Instance != before && effect.Instance.IsAlive)));
        }

        Assert.True(effect.HasCrashed);
        Assert.True(service.Catalog.IsBlocked(info.ClassId));
        Assert.Null(service.Registry.Find(info.TypeId));
        Assert.Contains("crashing", Assert.Single(service.Catalog.Blocked).Reason, StringComparison.Ordinal);
        effect.Dispose();
    }

    /// <summary>Runs the UI thread's work until <paramref name="done"/> holds (or 15 seconds pass).</summary>
    private static bool Pump(Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!done())
        {
            if (DateTime.UtcNow > deadline)
            {
                return false;
            }

            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(20);
        }

        return true;
    }

    private PluginService Service(Func<string, IReadOnlyList<PluginInfo>> scan)
    {
        var catalog = new PluginCatalog(CachePath);
        catalog.Load();
        return new PluginService(catalog, EffectRegistry.CreateDefault(), new PluginHostCommand("host", []), scan);
    }

    /// <summary>The app itself as the plug-in host (it is built next to the tests).</summary>
    private static PluginHostCommand AppHost() =>
        new(Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "VoiceChanger.exe" : "VoiceChanger"), []);

    /// <summary>A bundle with a moduleinfo.json listing one effect, or (without an id) none.</summary>
    private static void CreateBundle(string folder, string name, string? classId, string? effect)
    {
        string bundle = Path.Combine(folder, name);
        Directory.CreateDirectory(Path.Combine(bundle, "Contents", "x86_64-win"));
        File.WriteAllText(Path.Combine(bundle, "Contents", "x86_64-win", name), "binary");
        if (classId is null)
        {
            return;
        }

        Directory.CreateDirectory(Path.Combine(bundle, "Contents", "Resources"));
        File.WriteAllText(Path.Combine(bundle, "Contents", "Resources", "moduleinfo.json"), $$"""
            {
              "Factory Info": { "Vendor": "Fake Vendor" },
              "Classes": [
                { "CID": "{{classId}}", "Category": "Audio Module Class", "Name": "{{effect}}", "Sub Categories": ["Fx"] }
              ]
            }
            """);
    }
}
