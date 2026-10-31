using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using VoiceChanger.Plugins.Native;

namespace VoiceChanger.Plugins;

/// <summary>What a scan of one module found, as the scan command prints it.</summary>
public sealed class ScanResult
{
    public List<PluginInfo> Classes { get; set; } = [];

    /// <summary>Why the module couldn't be scanned, or <c>null</c>.</summary>
    public string? Error { get; set; }
}

/// <summary>Finds the effects in a VST3 module.</summary>
public static class PluginScanner
{
    /// <summary>The app scans a module in a separate process run with this argument and the module's path.</summary>
    public const string Command = "--scan-plugin";

    /// <summary>How long a scan in a separate process may take.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private const string EffectCategory = "Audio Module Class";

    /// <summary>
    /// Reads the effects from the module's <c>Contents/Resources/moduleinfo.json</c>, without loading
    /// any of its code. Returns <c>null</c> when the module has none or it can't be read.
    /// </summary>
    public static IReadOnlyList<PluginInfo>? ReadModuleInfo(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string file = Path.Combine(path, "Contents", "Resources", "moduleinfo.json");
        if (!File.Exists(file))
        {
            return null;
        }

        try
        {
            // The SDK writes it with trailing commas (JSON5).
            using var document = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
            var root = document.RootElement;
            string factoryVendor = root.TryGetProperty("Factory Info", out var factory) ? Text(factory, "Vendor") : "";
            var plugins = new List<PluginInfo>();
            if (!root.TryGetProperty("Classes", out var classes) || classes.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var entry in classes.EnumerateArray())
            {
                if (Text(entry, "Category") != EffectCategory || Text(entry, "CID") is not { Length: > 0 } classId)
                {
                    continue;
                }

                var subcategories = entry.TryGetProperty("Sub Categories", out var list) && list.ValueKind == JsonValueKind.Array
                    ? list.EnumerateArray().Select(c => c.GetString()).OfType<string>()
                    : [];
                string vendor = Text(entry, "Vendor");
                plugins.Add(new PluginInfo
                {
                    Path = path,
                    ClassId = classId,
                    Name = Text(entry, "Name"),
                    Vendor = string.IsNullOrEmpty(vendor) ? factoryVendor : vendor,
                    Version = Text(entry, "Version"),
                    SdkVersion = Text(entry, "SDKVersion"),
                    Subcategories = string.Join('|', subcategories),
                });
            }

            return plugins.Where(p => p.IsEffect).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Loads the module in this process and lists its effects.</summary>
    /// <exception cref="PluginException">The module could not be loaded.</exception>
    public static unsafe IReadOnlyList<PluginInfo> ScanModule(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!VcPlugin.IsAvailable(out string? problem))
        {
            throw new PluginException(problem!);
        }

        var classes = new ClassInfo[16];
        int count;
        while (true)
        {
            fixed (ClassInfo* buffer = classes)
            {
                count = VcPlugin.Scan(path, buffer, classes.Length);
            }

            if (count < 0)
            {
                throw new PluginException(VcPlugin.LastError);
            }

            if (count <= classes.Length)
            {
                break;
            }

            classes = new ClassInfo[count];
        }

        var plugins = new List<PluginInfo>(count);
        for (int i = 0; i < count; i++)
        {
            fixed (ClassInfo* info = &classes[i])
            {
                plugins.Add(new PluginInfo
                {
                    Path = path,
                    ClassId = VcPlugin.Text(info->ClassId, 40),
                    Name = VcPlugin.Text(info->Name, 128),
                    Vendor = VcPlugin.Text(info->Vendor, 128),
                    Version = VcPlugin.Text(info->Version, 64),
                    SdkVersion = VcPlugin.Text(info->SdkVersion, 64),
                    Subcategories = VcPlugin.Text(info->Subcategories, 128),
                });
            }
        }

        return plugins.Where(p => p.IsEffect).ToList();
    }

    /// <summary>
    /// The scan command (<see cref="Command"/>): scans the module in this process and writes a
    /// <see cref="ScanResult"/> as JSON. Returns the process exit code.
    /// </summary>
    public static int RunCommand(string path, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var result = new ScanResult();
        try
        {
            result.Classes = [.. ScanModule(path)];
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
        }

        output.Write(JsonSerializer.Serialize(result, PluginJsonContext.Default.ScanResult));
        output.Flush();
        return result.Error is null ? 0 : 1;
    }

    /// <summary>
    /// Scans the module in a separate process, so a module that crashes or hangs while loading
    /// can't take the app with it: runs <paramref name="executable"/> with
    /// <paramref name="arguments"/>, <see cref="Command"/> and the module's path.
    /// </summary>
    /// <exception cref="PluginException">The scan failed, crashed or timed out.</exception>
    public static IReadOnlyList<PluginInfo> ScanInSeparateProcess(string path, string executable, IEnumerable<string> arguments, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(executable);
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments ?? [])
        {
            start.ArgumentList.Add(argument);
        }

        start.ArgumentList.Add(Command);
        start.ArgumentList.Add(path);

        using var process = Process.Start(start) ?? throw new PluginException("The plug-in scanner could not be started.");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeout ?? Timeout))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // It ended after all.
            }

            throw new PluginException($"Scanning took longer than {(timeout ?? Timeout).TotalSeconds:0} seconds.");
        }

        process.WaitForExit();
        ScanResult? result = null;
        try
        {
            result = JsonSerializer.Deserialize(output.Result, PluginJsonContext.Default.ScanResult);
        }
        catch (JsonException)
        {
            // Nothing (or not a result) was written: it crashed.
        }

        if (result is null)
        {
            string detail = errors.Result.Trim();
            throw new PluginException(
                $"The scan crashed (exit code {process.ExitCode})" + (detail.Length > 0 ? $": {detail}" : "."));
        }

        if (result.Error is not null)
        {
            throw new PluginException(result.Error);
        }

        return result.Classes.Where(p => p.IsEffect).Select(p => p with { Path = path }).ToList();
    }

    private static string Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ScanResult))]
[JsonSerializable(typeof(PluginCache))]
internal sealed partial class PluginJsonContext : JsonSerializerContext;
