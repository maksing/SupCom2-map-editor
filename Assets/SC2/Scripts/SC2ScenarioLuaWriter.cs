// SC2ScenarioLuaWriter.cs
// Regenerates ONLY the StartPositions block in _scenario.lua, from the
// current positions of lua-only ARMY_N markers (see ScdImportWindow —
// imported from _save.lua, renamed by the symmetry tools). The rest of the
// file (ScenarioInfo, Configurations, norush*, etc.) is copied unchanged.
//
// If _scenario.lua has more/fewer ARMY_N entries than there are ARMY_N
// markers found in the scene, the ARMY_N list is built FROM THE SCENE (new
// positions from symmetry are included; ones missing from the scene are not).

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

public static class SC2ScenarioLuaWriter
{
    private static readonly Regex ArmyKeyRe = new(@"ARMY_(?<n>\d+)", System.Text.RegularExpressions.RegexOptions.Compiled);

    public static string Regenerate(string originalScenarioLua, IEnumerable<SC2Marker> armyMarkers)
    {
        var (open, close) = SC2LuaBlocks.FindBlock(originalScenarioLua, "StartPositions = ");
        if (open < 0) return originalScenarioLua;

        var ordered = armyMarkers
            .Select(m => (m, n: ArmyKeyRe.Match(m.MarkerName)))
            .Where(p => p.n.Success)
            .OrderBy(p => int.Parse(p.n.Groups["n"].Value, CultureInfo.InvariantCulture))
            .ToList();

        var sb = new StringBuilder();
        sb.Append("\r\n");
        foreach (var (m, n) in ordered)
        {
            var pos = m.transform.position;
            sb.Append("                ['ARMY_").Append(n.Groups["n"].Value).Append("'] = { ")
              .Append(Fmt(pos.x)).Append(", ").Append(Fmt(pos.y)).Append(", ").Append(Fmt(pos.z))
              .Append(" },\r\n");
        }
        sb.Append("    "); // indent before the closing '}' (level 4, matching StartPositions itself)

        return SC2LuaBlocks.ReplaceBlockContent(originalScenarioLua, open, close, sb.ToString());
    }

    private static string Fmt(float v) => v.ToString("0.######", CultureInfo.InvariantCulture);
}
