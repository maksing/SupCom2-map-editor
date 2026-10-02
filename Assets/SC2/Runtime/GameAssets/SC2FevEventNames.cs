using System;
using System.Collections.Generic;
using System.IO;

/// <summary>
/// Reads the event names out of an FMOD Designer .fev file (the "FEV1" format
/// Supreme Commander 2 ships as sounds/win/fmod/SC2.fev and SC2_DLC1.fev) and
/// returns each one as its full hierarchy path, in the form a map's Sound
/// marker stores in its EventName field:
/// "SC2/SC2/Environments/SC2_MP_001/snd_SC2_MP_001_Generic_Ocean" (project
/// name / event groups / event name).
///
/// Only what is needed to walk the event tree is understood: the header, the
/// wave bank list, the category tree, and the nested event groups with their
/// events. An event record itself is long and varies between FEV1 versions,
/// so its body is skipped rather than parsed: every record ends with a
/// 32-bit 1 followed by the length-prefixed path of the event's category
/// (a path from the category tree read earlier), and that marker is searched
/// for. The result has been compared against the real SC2.fev, whose 2698
/// events are exactly the 2698 FMOD Event Player reports.
/// </summary>
public static class SC2FevEventNames
{
    // Far longer than any real event record (about 700 bytes); a marker not found within this many bytes means the
    // file is not laid out the way this reader expects, which is reported instead of skipping ahead blindly.
    private const int MaxEventBodyBytes = 20000;
    private const int MaxStringBytes = 4096;

    public static List<string> Read(byte[] data)
    {
        if (data == null) throw new ArgumentNullException(nameof(data));
        if (data.Length < 16 || data[0] != (byte)'F' || data[1] != (byte)'E' || data[2] != (byte)'V' || data[3] != (byte)'1')
            throw new InvalidDataException("Not an FMOD FEV1 file.");

        var reader = new Cursor(data);
        reader.Skip(4 + 12); // magic, version and two values whose meaning is not needed
        var project = reader.ReadString();

        var bankCount = reader.ReadUInt32();
        for (uint i = 0; i < bankCount; i++)
        {
            reader.Skip(4 + 4 + 8); // streaming flags, max streams, 8 bytes not needed
            reader.ReadString();
        }

        var categories = new HashSet<string>(StringComparer.Ordinal);
        ReadCategory(reader, string.Empty, categories);

        var events = new List<string>();
        var groupCount = reader.ReadUInt32();
        for (uint i = 0; i < groupCount; i++)
            ReadGroup(reader, project, string.Empty, categories, events);
        return events;
    }

    private static void ReadCategory(Cursor reader, string parentPath, HashSet<string> categories)
    {
        var name = reader.ReadString();
        var path = parentPath.Length == 0 ? name : parentPath + "/" + name;
        categories.Add(path);
        // An event stores its category without the root ("master/") in front of it.
        var slash = path.IndexOf('/');
        if (slash >= 0) categories.Add(path.Substring(slash + 1));
        reader.Skip(4 + 4 + 8); // volume, pitch, two values not needed
        var subCount = reader.ReadUInt32();
        for (uint i = 0; i < subCount; i++)
            ReadCategory(reader, path, categories);
    }

    private static void ReadGroup(Cursor reader, string project, string parentPath, HashSet<string> categories, List<string> events)
    {
        var name = reader.ReadString();
        var path = parentPath.Length == 0 ? name : parentPath + "/" + name;
        SkipProperties(reader);
        var subGroupCount = reader.ReadUInt32();
        var eventCount = reader.ReadUInt32();
        for (uint i = 0; i < subGroupCount; i++)
            ReadGroup(reader, project, path, categories, events);
        for (uint i = 0; i < eventCount; i++)
        {
            reader.Skip(4);
            var eventName = reader.ReadString();
            events.Add(project + "/" + path + "/" + eventName);
            SkipEventBody(reader, categories, eventName);
        }
    }

    private static void SkipProperties(Cursor reader)
    {
        var count = reader.ReadUInt32();
        for (uint i = 0; i < count; i++)
        {
            reader.ReadString();
            var type = reader.ReadUInt32();
            if (type == 0 || type == 1) reader.Skip(4);
            else if (type == 2) reader.ReadString();
            else throw new InvalidDataException("Unknown FEV property type " + type + " at offset " + reader.Position + ".");
        }
    }

    private static void SkipEventBody(Cursor reader, HashSet<string> categories, string eventName)
    {
        var data = reader.Data;
        var start = reader.Position;
        var limit = Math.Min(data.Length, start + MaxEventBodyBytes);
        var search = start;
        while (search + 8 <= limit)
        {
            var found = Array.IndexOf(data, (byte)1, search, limit - search);
            if (found < 0 || found + 8 > data.Length) break;
            search = found + 1;
            if (data[found + 1] != 0 || data[found + 2] != 0 || data[found + 3] != 0) continue;

            var length = BitConverter.ToUInt32(data, found + 4);
            if (length < 2 || length > 200 || found + 8 + length > data.Length) continue;
            if (data[found + 8 + length - 1] != 0) continue;

            var text = Latin1(data, found + 8, (int)length - 1);
            if (!categories.Contains(text)) continue;
            reader.Position = found + 8 + (int)length;
            return;
        }
        throw new InvalidDataException("Could not find the end of event \"" + eventName + "\" (record starts at offset " + start + ").");
    }

    // Event and group names are plain ASCII in practice; mapping each byte to the char of the same value keeps any other
    // byte intact instead of failing on it.
    private static string Latin1(byte[] data, int offset, int count)
    {
        var chars = new char[count];
        for (var i = 0; i < count; i++) chars[i] = (char)data[offset + i];
        return new string(chars);
    }

    private sealed class Cursor
    {
        public readonly byte[] Data;
        public int Position;

        public Cursor(byte[] data) { Data = data; }

        public void Skip(int count)
        {
            if (count < 0 || Position + count > Data.Length) throw new InvalidDataException("FEV data ends unexpectedly at offset " + Position + ".");
            Position += count;
        }

        public uint ReadUInt32()
        {
            if (Position + 4 > Data.Length) throw new InvalidDataException("FEV data ends unexpectedly at offset " + Position + ".");
            var value = BitConverter.ToUInt32(Data, Position);
            Position += 4;
            return value;
        }

        public string ReadString()
        {
            var length = ReadUInt32();
            if (length > MaxStringBytes || Position + length > Data.Length)
                throw new InvalidDataException("Implausible string length " + length + " at offset " + (Position - 4) + ".");
            if (length == 0) return string.Empty;
            if (Data[Position + length - 1] != 0)
                throw new InvalidDataException("String is not NUL-terminated at offset " + (Position - 4) + ".");
            var text = Latin1(Data, Position, (int)length - 1);
            Position += (int)length;
            return text;
        }
    }
}
