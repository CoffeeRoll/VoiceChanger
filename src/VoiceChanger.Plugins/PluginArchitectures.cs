using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace VoiceChanger.Plugins;

/// <summary>Which processes can load a VST3 module: what it was built for.</summary>
public static class PluginArchitectures
{
    // Bundle folders (Contents/<folder>) and the processes that load their binaries.
    private static readonly Dictionary<string, Architecture[]> Folders = new(StringComparer.OrdinalIgnoreCase)
    {
        ["x86_64-win"] = [Architecture.X64],
        ["arm64-win"] = [Architecture.Arm64],
        ["arm64ec-win"] = [Architecture.X64],
        ["arm64x-win"] = [Architecture.Arm64, Architecture.X64],
        ["x86-win"] = [Architecture.X86],
        ["x86_64-linux"] = [Architecture.X64],
        ["aarch64-linux"] = [Architecture.Arm64],
        ["i386-linux"] = [Architecture.X86],
        ["armv7l-linux"] = [Architecture.Arm],
    };

    /// <summary>
    /// The architectures of processes that can load the module, from its bundle's folders (or a
    /// single-file module's header). Empty when that can't be told (then try this process's own).
    /// </summary>
    public static IReadOnlySet<Architecture> Of(string modulePath)
    {
        ArgumentNullException.ThrowIfNull(modulePath);
        var architectures = new HashSet<Architecture>();
        try
        {
            if (File.Exists(modulePath))
            {
                if (OfWindowsBinary(modulePath) is { } architecture)
                {
                    architectures.Add(architecture);
                }

                return architectures;
            }

            string contents = Path.Combine(modulePath, "Contents");
            if (!Directory.Exists(contents))
            {
                return architectures;
            }

            foreach (string folder in Directory.EnumerateDirectories(contents))
            {
                if (Folders.TryGetValue(Path.GetFileName(folder), out var supported))
                {
                    architectures.UnionWith(supported);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Can't tell.
        }

        return architectures;
    }

    /// <summary>The machine a Windows DLL (a PE file) was built for, or null.</summary>
    private static Architecture? OfWindowsBinary(string path)
    {
        using var file = File.OpenRead(path);
        Span<byte> header = stackalloc byte[64];
        if (file.Read(header) < header.Length || header[0] != 'M' || header[1] != 'Z')
        {
            return null;
        }

        file.Position = BinaryPrimitives.ReadInt32LittleEndian(header[0x3C..]);
        Span<byte> signature = stackalloc byte[6];
        if (file.Read(signature) < signature.Length || !signature[..4].SequenceEqual("PE\0\0"u8))
        {
            return null;
        }

        return BinaryPrimitives.ReadUInt16LittleEndian(signature[4..]) switch
        {
            0x8664 => Architecture.X64,
            0xAA64 => Architecture.Arm64,
            0x014C => Architecture.X86,
            _ => null,
        };
    }
}
