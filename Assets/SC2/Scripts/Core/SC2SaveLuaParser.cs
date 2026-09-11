// SC2SaveLuaParser.cs
// Parses the Scenario.MasterChain._MASTERCHAIN_.Markers block out of
// _save.lua into a Dictionary<markerName, List<SC2LuaField>> — ordered
// key/value pairs EXACTLY AS IN THE FILE (including type/position/
// adjacentTo — their values get overwritten by SC2SaveLuaWriter at export
// time, but their position in the list is preserved for a readable diff).
//
// Marker record format (observed across every map examined):
//   ['Name'] = {
//       ['key'] = EXPR( ... ),
//       ...
//   },
// EXPR — STRING(...)/VECTOR3(...)/FLOAT(...)/BOOLEAN(...)/GROUP{...} etc.
// — kept as a raw string, not interpreted (except for type/position/
// adjacentTo at regeneration time).

using System.Collections.Generic;
using System.Text.RegularExpressions;

public static class SC2SaveLuaParser
{
    private static readonly Regex MarkerStart = new(@"\['(?<name>[^']*)'\]\s*=\s*\{", RegexOptions.Compiled);
    private static readonly Regex FieldLine = new(@"\['(?<key>[a-zA-Z]+)'\]\s*=\s*(?<value>.+?),\s*$", RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>
    /// Returns (markers, markersBlockOpen, markersBlockClose) — the
    /// positions are needed by <see cref="SC2SaveLuaWriter"/> to replace the
    /// block. markersBlockOpen == -1 if "Markers = {" wasn't found (no
    /// save.lua / a different format).
    /// </summary>
    public static (Dictionary<string, List<SC2LuaField>> Markers, int BlockOpen, int BlockClose) Parse(string saveLua)
    {
        var result = new Dictionary<string, List<SC2LuaField>>(System.StringComparer.OrdinalIgnoreCase);
        var (open, close) = SC2LuaBlocks.FindBlock(saveLua, "Markers = ");
        if (open < 0) return (result, -1, -1);

        var content = saveLua.Substring(open + 1, close - open - 1);

        var matches = MarkerStart.Matches(content);
        for (var m = 0; m < matches.Count; m++)
        {
            var match = matches[m];
            var name = match.Groups["name"].Value;
            var entryOpen = match.Index + match.Length - 1; // index of '{'
            var entryClose = SC2LuaBlocks.FindMatchingBrace(content, entryOpen);
            if (entryClose < 0) continue;

            var body = content.Substring(entryOpen + 1, entryClose - entryOpen - 1);
            var fields = new List<SC2LuaField>();
            foreach (Match fm in FieldLine.Matches(body))
                fields.Add(new SC2LuaField { Key = fm.Groups["key"].Value, RawValue = fm.Groups["value"].Value.TrimEnd() });

            // The game treats the Markers table as name-keyed data and skips
            // duplicate keys. Preserve the first valid entry instead of
            // inventing a second editor-only "(dupN)" marker that the game
            // itself would never expose as a distinct marker.
            if (!result.ContainsKey(name))
                result.Add(name, fields);
        }

        return (result, open, close);
    }
}
