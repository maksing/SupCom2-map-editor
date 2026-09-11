// SC2SaveLuaWriter.cs
// Regenerates ONLY the Scenario.MasterChain._MASTERCHAIN_.Markers block in
// _save.lua — the rest of the file (Props, Areas, per-Army blocks,
// ScenarioInfo, etc.) is copied unchanged (SC2LuaBlocks.FindBlock +
// ReplaceBlockContent).
//
// For each scene marker:
//  - fields from SC2Marker.LuaExtras are written out in the SAME ORDER, but
//    the 'type'/'position'/'adjacentTo' values are replaced with current
//    ones (from MapMarker / transform / graphs);
//  - if 'adjacentTo' isn't in LuaExtras but the marker has graph edges, a
//    new entry is added (after 'graph' if present, else after
//    'hint', else first);
//  - markers with no LuaExtras (new, without inheriting from a leader
//    during symmetry — shouldn't normally happen, see
//    SymmetryToolWindow.CopyLuaExtras) get a minimal default set
//    (type/prop/orientation/position [+adjacentTo]).

using System.Collections.Generic;
using System.Linq;
using System.Text;
using BdfIO.Formats.MapObjs;

public static class SC2SaveLuaWriter
{
    private const string Indent16 = "                ";
    private const string Indent20 = "                    ";

    public static string Regenerate(string originalSaveLua, MapObjsModel model,
        IReadOnlyList<SC2Marker?> markerComponents, IReadOnlyList<SC2Marker> luaOnlyMarkers)
    {
        var (_, open, close) = SC2SaveLuaParser.Parse(originalSaveLua);
        if (open < 0)
        {
            // No Markers block (different format/empty save.lua) — leave the file untouched.
            return originalSaveLua;
        }

        // adjacency: marker index -> list of neighbor names (from every graph)
        var adjacency = new Dictionary<int, List<string>>();
        foreach (var graph in model.Graphs)
            foreach (var (a, b) in graph.Entries)
            {
                AddAdjacency(adjacency, a, model.Markers[b].Name);
                AddAdjacency(adjacency, b, model.Markers[a].Name);
            }

        var sb = new StringBuilder();
        sb.Append("\r\n");

        for (var i = 0; i < model.Markers.Count; i++)
        {
            var marker = model.Markers[i];
            var extras = markerComponents.Count > i ? markerComponents[i]?.LuaExtras : null;
            var adjacentNames = adjacency.TryGetValue(i, out var list) ? string.Join(' ', list) : null;

            WriteMarkerEntry(sb, marker.Name, marker.MarkerType, marker.Position, extras, adjacentNames);
        }

        // ---- lua-only markers (ARMY_N/Mass/Combat Zone/... — not in mapobjs.bdf,
        // adjacentTo isn't recomputed for them, and they have no graphs) ----
        foreach (var m in luaOnlyMarkers)
        {
            var pos = m.transform.position;
            WriteMarkerEntry(sb, m.MarkerName, m.MarkerType, new[] { pos.x, pos.y, pos.z }, m.LuaExtras, adjacentNames: null);
        }

        sb.Append("            "); // indent before the Markers block's closing '}' (level 12)

        var newContent = sb.ToString();
        return SC2LuaBlocks.ReplaceBlockContent(originalSaveLua, open, close, newContent);
    }

    private static void WriteMarkerEntry(StringBuilder sb, string name, string markerType, float[] position,
        List<SC2LuaField>? extras, string? adjacentNames)
    {
        sb.Append(Indent16).Append("['").Append(name).Append("'] = {\r\n");

        var fields = extras != null && extras.Count > 0
            ? new List<SC2LuaField>(extras)
            : DefaultFields();

        var hasAdjacent = fields.Any(f => f.Key == "adjacentTo");
        if (!hasAdjacent && adjacentNames != null)
        {
            var afterKey = fields.FindIndex(f => f.Key == "graph");
            if (afterKey < 0) afterKey = fields.FindIndex(f => f.Key == "hint");
            var insertAt = afterKey < 0 ? 0 : afterKey + 1;
            fields.Insert(insertAt, new SC2LuaField { Key = "adjacentTo", RawValue = "" });
        }

        foreach (var f in fields)
        {
            var value = f.Key switch
            {
                "type" => $"STRING( '{markerType}' )",
                "position" => $"VECTOR3( {Fmt(position[0])}, {Fmt(position[1])}, {Fmt(position[2])} )",
                "adjacentTo" => $"STRING( '{adjacentNames ?? ""}' )",
                _ => f.RawValue
            };

            // adjacentTo with no neighbors and no original value -> skip the line
            if (f.Key == "adjacentTo" && string.IsNullOrEmpty(adjacentNames) && string.IsNullOrEmpty(f.RawValue))
                continue;

            sb.Append(Indent20).Append("['").Append(f.Key).Append("'] = ").Append(value).Append(",\r\n");
        }

        sb.Append(Indent16).Append("},\r\n");
    }

    /// <summary>Minimal field set for a marker with no LuaExtras (new, no inheritance).</summary>
    private static List<SC2LuaField> DefaultFields() => new()
    {
        new SC2LuaField { Key = "type", RawValue = "" },
        new SC2LuaField { Key = "prop", RawValue = "STRING( '/env/common/props/markers/M_Blank_prop.bp' )" },
        new SC2LuaField { Key = "orientation", RawValue = "VECTOR3( 0, 0, 0 )" },
        new SC2LuaField { Key = "position", RawValue = "" },
    };

    private static void AddAdjacency(Dictionary<int, List<string>> adjacency, int index, string neighborName)
    {
        if (!adjacency.TryGetValue(index, out var list))
            adjacency[index] = list = new List<string>();
        if (!list.Contains(neighborName)) list.Add(neighborName);
    }

    private static string Fmt(float v) => v.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
}
