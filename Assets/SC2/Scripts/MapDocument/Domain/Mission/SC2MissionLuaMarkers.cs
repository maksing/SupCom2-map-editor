using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SC2.MapEditor.Document
{
    /// <summary>
    /// The comment lines that fence every generated region of the script ("-- SCED:begin stage1 hash=1a2b3c4d" ... "-- SCED:end stage1").
    /// The hash covers the text between the two lines, so a region that was edited by hand after generation can be told apart from one
    /// that still matches the graph.
    /// </summary>
    public static class SC2MissionLuaMarkers
    {
        public const string BeginPrefix = "-- SCED:begin ";
        public const string EndPrefix = "-- SCED:end ";

        public sealed class Region
        {
            public string Id;
            public string Body;
            public string DeclaredHash;
            public string ActualHash;
            public bool Intact => DeclaredHash == ActualHash;
        }

        public static string Wrap(string id, string body)
        {
            body = Normalize(body);
            if (body.Length > 0 && !body.EndsWith("\n")) body += "\n";
            return BeginPrefix + id + " hash=" + Hash(body) + "\n" + body + EndPrefix + id + "\n";
        }

        /// <summary>FNV-1a over the text with every line ending turned into \n, as 8 hex digits.</summary>
        public static string Hash(string body)
        {
            var text = Normalize(body);
            uint h = 2166136261;
            foreach (var b in Encoding.UTF8.GetBytes(text))
            {
                h ^= b;
                h *= 16777619;
            }
            return h.ToString("x8", CultureInfo.InvariantCulture);
        }

        public static string Normalize(string text) => (text ?? "").Replace("\r\n", "\n").Replace("\r", "\n");

        /// <summary>The regions found in a script, in file order. A begin line without a matching end line is ignored.</summary>
        public static List<Region> Parse(string script)
        {
            var regions = new List<Region>();
            var lines = Normalize(script).Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (!lines[i].StartsWith(BeginPrefix)) continue;
                var header = lines[i].Substring(BeginPrefix.Length).Trim();
                var space = header.IndexOf(' ');
                if (space < 0) continue;
                var id = header.Substring(0, space);
                var declared = header.Substring(space + 1).Trim();
                if (!declared.StartsWith("hash=")) continue;
                declared = declared.Substring("hash=".Length);

                var body = new StringBuilder();
                var j = i + 1;
                while (j < lines.Length && lines[j] != EndPrefix + id)
                {
                    body.Append(lines[j]).Append('\n');
                    j++;
                }
                if (j >= lines.Length) continue;
                var text = body.ToString();
                regions.Add(new Region { Id = id, Body = text, DeclaredHash = declared, ActualHash = Hash(text) });
                i = j;
            }
            return regions;
        }
    }
}
