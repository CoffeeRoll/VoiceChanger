using VoiceChanger.Core.Effects;
using VoiceChanger.Core.Graph;

namespace VoiceChanger.Plugins.Tests;

public sealed class PluginCatalogTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "vc-plugins-" + Guid.NewGuid().ToString("N"));

    public PluginCatalogTests() => Directory.CreateDirectory(Path.Combine(_directory, "VST3"));

    private string Folder => Path.Combine(_directory, "VST3");

    private string CachePath => Path.Combine(_directory, "plugins.json");

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void Reads_moduleinfo_without_loading_the_module()
    {
        string bundle = CreateBundle("Fake.vst3", ("ABCDEF0123456789ABCDEF0123456789", "Fake Delay", "Fx|Delay"), ("11111111222222223333333344444444", "Fake Synth", "Instrument|Synth"));
        var catalog = new PluginCatalog(CachePath);

        Assert.True(catalog.Scan([Folder], _ => throw new InvalidOperationException("The module should not be loaded."), TestContext.Current.CancellationToken));

        var plugin = Assert.Single(catalog.Plugins);
        Assert.Equal("Fake Delay", plugin.Name);
        Assert.Equal("Fake Vendor", plugin.Vendor);
        Assert.Equal("Fx|Delay", plugin.Subcategories);
        Assert.Equal(bundle, plugin.Path);
        Assert.Equal("vst3:ABCDEF0123456789ABCDEF0123456789", plugin.TypeId);
    }

    [Fact]
    public void Rescans_only_new_or_changed_modules_and_drops_removed_ones()
    {
        string first = CreateBundle("One.vst3");
        string nested = Path.Combine(Folder, "Vendor", "Two.vst3");
        CreateBundle(Path.Combine("Vendor", "Two.vst3"), withModuleInfo: false);
        var scanned = new List<string>();
        IReadOnlyList<PluginInfo> Scan(string path)
        {
            scanned.Add(path);
            return [new PluginInfo { Path = path, ClassId = "22222222222222222222222222222222", Name = "Two", Subcategories = "Fx" }];
        }

        var catalog = new PluginCatalog(CachePath);
        Assert.True(catalog.Scan([Folder], Scan, TestContext.Current.CancellationToken));
        Assert.Equal([nested], scanned);
        Assert.Equal(["Plugin", "Two"], catalog.Plugins.Select(p => p.Name));
        catalog.Save();

        // A new start: the cache answers, nothing is scanned.
        var restarted = new PluginCatalog(CachePath);
        restarted.Load();
        scanned.Clear();
        Assert.False(restarted.Scan([Folder], Scan, TestContext.Current.CancellationToken));
        Assert.Empty(scanned);
        Assert.Equal(2, restarted.Plugins.Count);

        // Changed: scanned again. Removed: dropped.
        File.AppendAllText(Path.Combine(nested, "Contents", "x86_64-win", "Two.vst3"), "more");
        Directory.Delete(first, recursive: true);
        Assert.True(restarted.Scan([Folder], Scan, TestContext.Current.CancellationToken));
        Assert.Equal([nested], scanned);
        Assert.Equal(["Two"], restarted.Plugins.Select(p => p.Name));
    }

    [Fact]
    public void A_module_that_fails_to_scan_is_left_out_until_it_changes()
    {
        CreateBundle("Broken.vst3", withModuleInfo: false);
        int scans = 0;
        var catalog = new PluginCatalog(CachePath);

        catalog.Scan([Folder], _ =>
        {
            scans++;
            throw new PluginException("The scan crashed.");
        }, TestContext.Current.CancellationToken);
        catalog.Scan([Folder], _ => throw new InvalidOperationException("Not scanned again."), TestContext.Current.CancellationToken);

        Assert.Equal(1, scans);
        Assert.Empty(catalog.Plugins);
        Assert.Equal("The scan crashed.", Assert.Single(catalog.Modules).Error);
    }

    [Fact]
    public void A_damaged_cache_is_ignored()
    {
        File.WriteAllText(CachePath, "{ not json");
        var catalog = new PluginCatalog(CachePath);
        catalog.Load();
        Assert.Empty(catalog.Modules);
    }

    [Fact]
    public void Registered_plugins_become_effects_and_nodes()
    {
        CreateBundle("One.vst3", ("ABCDEF0123456789ABCDEF0123456789", "Fake Delay", "Fx|Delay"));
        var catalog = new PluginCatalog(CachePath);
        catalog.Scan([Folder], _ => [], TestContext.Current.CancellationToken);
        var registry = EffectRegistry.CreateDefault();

        var added = Assert.Single(catalog.RegisterInto(registry));
        Assert.Empty(catalog.RegisterInto(registry));
        Assert.Equal("Fake Vendor", added.Group);
        Assert.Contains("Fake Vendor", added.Description, StringComparison.Ordinal);

        var nodes = NodeRegistry.CreateDefault(registry);
        Assert.NotNull(nodes.Find(EffectNode.TypePrefix + "vst3:ABCDEF0123456789ABCDEF0123456789"));

        // Its module doesn't really load: it becomes a stand-in rather than an error.
        var effect = Assert.IsAssignableFrom<MissingEffect>(registry.Create(added.TypeId));
        Assert.Equal("Fake Delay (missing)", effect.Name);
    }

    [Fact]
    public void Finds_the_test_plugin_in_the_native_build()
    {
        var info = TestPlugin.Info;
        var catalog = new PluginCatalog(CachePath);

        catalog.Scan([TestPlugin.Folder!], PluginScanner.ScanModule, TestContext.Current.CancellationToken);

        var found = Assert.Single(catalog.Plugins, p => p.ClassId == info.ClassId);
        Assert.Equal(info.Path, found.Path);
    }

    /// <summary>A bundle like the SDK lays it out, with a binary and (optionally) moduleinfo.json.</summary>
    private string CreateBundle(string relativePath, params (string ClassId, string Name, string Categories)[] classes) =>
        CreateBundle(relativePath, withModuleInfo: true, classes);

    private string CreateBundle(string relativePath, bool withModuleInfo, params (string ClassId, string Name, string Categories)[] classes)
    {
        string bundle = Path.Combine(Folder, relativePath);
        string name = Path.GetFileName(relativePath);
        Directory.CreateDirectory(Path.Combine(bundle, "Contents", "x86_64-win"));
        File.WriteAllText(Path.Combine(bundle, "Contents", "x86_64-win", name), "binary");
        if (!withModuleInfo)
        {
            return bundle;
        }

        if (classes.Length == 0)
        {
            classes = [(Guid.NewGuid().ToString("N").ToUpperInvariant(), "Plugin", "Fx")];
        }

        // As the SDK's moduleinfotool writes it, trailing commas included.
        var entries = classes.Select(c => $$"""
                {
                  "CID": "{{c.ClassId}}",
                  "Category": "Audio Module Class",
                  "Name": "{{c.Name}}",
                  "Vendor": "",
                  "Version": "1.0.0",
                  "SDKVersion": "VST 3.8.1",
                  "Sub Categories": [{{string.Join(", ", c.Categories.Split('|').Select(s => $"\"{s}\""))}},],
                },
            """);
        string json = $$"""
            {
              "Name": "{{name}}",
              "Factory Info": { "Vendor": "Fake Vendor", },
              "Classes": [
                {{string.Join("\n", entries)}}
                {
                  "CID": "99999999999999999999999999999999",
                  "Category": "Component Controller Class",
                  "Name": "Controller",
                },
              ],
            }
            """;
        Directory.CreateDirectory(Path.Combine(bundle, "Contents", "Resources"));
        File.WriteAllText(Path.Combine(bundle, "Contents", "Resources", "moduleinfo.json"), json);
        return bundle;
    }
}
