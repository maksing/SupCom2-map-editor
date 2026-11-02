using System.Collections.Generic;
using SC2.MapEditor.Document;

/// <summary>Reads the names the mission graph can point at (armies, areas, markers, chains, unit groups) out of the map being edited.</summary>
public static class SC2MissionMapInfoBuilder
{
    public static SC2MissionMapInfo From(SC2TerrainData data)
    {
        var info = new SC2MissionMapInfo
        {
            Armies = new HashSet<string>(),
            Areas = new HashSet<string>(),
            Markers = new HashSet<string>(),
            Chains = new HashSet<string>(),
            Groups = new Dictionary<string, HashSet<string>>(),
            Units = new Dictionary<string, HashSet<string>>(),
            GroupUnitCounts = new Dictionary<string, Dictionary<string, int>>(),
            ArmyUnitCounts = new Dictionary<string, int>(),
        };
        if (data == null) return info;

        info.ArmyOrder = SC2ObjectsExport.ScenarioArmyNames(data);
        info.PlayableArea = SC2ObjectsExport.PlayableAreaName(data) ?? "";

        foreach (var a in data.ObjArmies) if (!string.IsNullOrEmpty(a.Name)) info.Armies.Add(a.Name);
        foreach (var m in data.ObjMarkers)
        {
            if (string.IsNullOrEmpty(m.Name)) continue;
            info.Markers.Add(m.Name);
            if (SC2ObjectsShell.IsArmyStartPosition(m.Name)) info.Armies.Add(m.Name);
        }
        foreach (var z in data.ObjZones) if (!string.IsNullOrEmpty(z.Name)) info.Areas.Add(z.Name);
        foreach (var c in data.ObjChains) if (!string.IsNullOrEmpty(c.Name)) info.Chains.Add(c.Name);
        foreach (var u in data.ObjUnits)
        {
            if (string.IsNullOrEmpty(u.ArmyName) || string.IsNullOrEmpty(u.Name)) continue;
            var table = u.IsGroup ? info.Groups : info.Units;
            if (!table.TryGetValue(u.ArmyName, out var set)) table[u.ArmyName] = set = new HashSet<string>();
            set.Add(u.Name);
        }

        // Units are stored flat with a parent index; each unit counts for the army and for every group above it.
        for (var i = 0; i < data.ObjUnits.Count; i++)
        {
            var unit = data.ObjUnits[i];
            if (unit.IsGroup || string.IsNullOrEmpty(unit.ArmyName)) continue;
            info.ArmyUnitCounts.TryGetValue(unit.ArmyName, out var armyCount);
            info.ArmyUnitCounts[unit.ArmyName] = armyCount + 1;
            if (!info.GroupUnitCounts.TryGetValue(unit.ArmyName, out var counts)) info.GroupUnitCounts[unit.ArmyName] = counts = new Dictionary<string, int>();
            var parent = unit.ParentIndex;
            for (var depth = 0; parent >= 0 && parent < data.ObjUnits.Count && depth < 64; depth++)
            {
                var group = data.ObjUnits[parent];
                if (group.IsGroup && !string.IsNullOrEmpty(group.Name))
                {
                    counts.TryGetValue(group.Name, out var n);
                    counts[group.Name] = n + 1;
                }
                parent = group.ParentIndex;
            }
        }
        return info;
    }

    /// <summary>A sorted list for a dropdown.</summary>
    public static string[] Sorted(IEnumerable<string> names)
    {
        var list = new List<string>(names ?? new string[0]);
        list.Sort(System.StringComparer.Ordinal);
        return list.ToArray();
    }
}
