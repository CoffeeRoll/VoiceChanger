using System.Buffers.Binary;
using System.Text;
using VoiceChanger.Plugins.Native;

namespace VoiceChanger.Plugins.Hosting;

/// <summary>
/// How the app and a host process talk (besides the audio, which goes through
/// <see cref="SharedAudio"/>): frames of a 32-bit length and a body, over two pipes. The app sends
/// requests and gets one response each, in order; the host also sends events (a parameter edited
/// in the plug-in's window, a restart request, the window closed), which come before the response
/// to the request that caused them.
/// </summary>
internal static class HostProtocol
{
    /// <summary>Largest frame either side accepts (a plug-in state can be large, but not this large).</summary>
    public const int MaxFrame = 256 * 1024 * 1024;

    public enum Kind : byte
    {
        Request = 1,
        Response = 2,
        Event = 3,
    }

    public enum Op : byte
    {
        Open = 1,
        Setup,
        Suspend,
        GetParam,
        SetParam,
        Format,
        GetState,
        SetState,
        LoadPreset,
        SavePreset,
        OpenEditor,
        CloseEditor,
        Close,
    }

    public enum EventType : byte
    {
        Edited = 1,
        Restart,
        EditorClosed,
    }

    /// <summary>Writes a frame whose body <paramref name="write"/> fills.</summary>
    public static void WriteFrame(Stream stream, Action<BinaryWriter> write)
    {
        using var body = new MemoryStream();
        using (var writer = new BinaryWriter(body, Encoding.UTF8, leaveOpen: true))
        {
            write(writer);
        }

        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, (int)body.Length);
        stream.Write(length);
        stream.Write(body.GetBuffer(), 0, (int)body.Length);
        stream.Flush();
    }

    /// <summary>Reads a frame's body, or returns null when the other side is gone.</summary>
    public static BinaryReader? ReadFrame(Stream stream)
    {
        Span<byte> length = stackalloc byte[4];
        if (!ReadExactly(stream, length))
        {
            return null;
        }

        int size = BinaryPrimitives.ReadInt32LittleEndian(length);
        if (size < 0 || size > MaxFrame)
        {
            throw new InvalidDataException($"A frame of {size} bytes.");
        }

        var body = new byte[size];
        return ReadExactly(stream, body) ? new BinaryReader(new MemoryStream(body), Encoding.UTF8) : null;
    }

    public static void WriteText(this BinaryWriter writer, string? text)
    {
        writer.Write(text is not null);
        if (text is not null)
        {
            writer.Write(text);
        }
    }

    public static string? ReadText(this BinaryReader reader) => reader.ReadBoolean() ? reader.ReadString() : null;

    public static void WriteBytes(this BinaryWriter writer, byte[]? bytes)
    {
        writer.Write(bytes?.Length ?? -1);
        if (bytes is not null)
        {
            writer.Write(bytes);
        }
    }

    public static byte[]? ReadBytes(this BinaryReader reader)
    {
        int length = reader.ReadInt32();
        return length < 0 ? null : reader.ReadBytes(length);
    }

    public static void WriteDescription(this BinaryWriter writer, PluginDescription description)
    {
        writer.Write(description.InputName);
        writer.Write(description.SidechainName);
        writer.Write(description.HasSidechain);
        writer.Write(description.Parameters.Count);
        foreach (var parameter in description.Parameters)
        {
            writer.Write(parameter.Id);
            writer.Write(parameter.Title);
            writer.Write(parameter.Units);
            writer.Write(parameter.StepCount);
            writer.Write(parameter.DefaultValue);
            writer.Write((int)parameter.Flags);
            writer.Write(parameter.Value);
            writer.Write(parameter.Choices?.Count ?? -1);
            foreach (string choice in parameter.Choices ?? [])
            {
                writer.Write(choice);
            }
        }

        writer.Write(description.Programs.Count);
        foreach (string program in description.Programs)
        {
            writer.Write(program);
        }

        writer.Write(description.ProgramParameter.HasValue);
        writer.Write(description.ProgramParameter ?? 0);
        writer.Write(description.HasEditor);
    }

    public static PluginDescription ReadDescription(this BinaryReader reader)
    {
        string inputName = reader.ReadString();
        string sidechainName = reader.ReadString();
        bool hasSidechain = reader.ReadBoolean();
        var parameters = new List<PluginParameterInfo>();
        for (int count = reader.ReadInt32(); count > 0; count--)
        {
            uint id = reader.ReadUInt32();
            string title = reader.ReadString();
            string units = reader.ReadString();
            int steps = reader.ReadInt32();
            double defaultValue = reader.ReadDouble();
            var flags = (ParamFlags)reader.ReadInt32();
            double value = reader.ReadDouble();
            int choiceCount = reader.ReadInt32();
            List<string>? choices = choiceCount < 0 ? null : [];
            for (int i = 0; i < choiceCount; i++)
            {
                choices!.Add(reader.ReadString());
            }

            parameters.Add(new PluginParameterInfo(id, title, units, steps, defaultValue, flags, value, choices));
        }

        var programs = new List<string>();
        for (int count = reader.ReadInt32(); count > 0; count--)
        {
            programs.Add(reader.ReadString());
        }

        bool hasProgramParameter = reader.ReadBoolean();
        uint programParameter = reader.ReadUInt32();
        bool hasEditor = reader.ReadBoolean();
        return new PluginDescription(inputName, sidechainName, hasSidechain, parameters, programs, hasProgramParameter ? programParameter : null, hasEditor);
    }

    private static bool ReadExactly(Stream stream, Span<byte> buffer)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int count = stream.Read(buffer[read..]);
            if (count == 0)
            {
                return false;
            }

            read += count;
        }

        return true;
    }
}
