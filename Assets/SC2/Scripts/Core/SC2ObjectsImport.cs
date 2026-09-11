// SC2ObjectsImport.cs
//
// Reverse of SC2ObjectsExport — reads an existing map's mapobjs.bdf and
// _save.lua and populates SC2TerrainData.Obj* so an
// already-shipped (or previously exported) map's objects can be edited
// through the Objects tab instead of only ever starting from an empty list.
//
// No UnityEditor dependency (plain BCL + BdfIO + the POCOs in
// SC2ObjectsModel.cs), so — like SC2TerrainBdfImporter — this runs
// identically from the Editor's Import tab and from a standalone build
// (see SC2ObjectsShell.ImportObjectsFromFile, which only differs in which
// file-picker supplies the path).
//
// _save.lua is authoritative for markers. mapobjs.bdf contributes props,
// path graphs, and only marker rows referenced by those graphs. Parses the
// same blocks SC2ObjectsExport writes (Markers/Areas/Armies/Units/Chains)
// using the existing SC2LuaBlocks brace-
// counting utility + SC2LuaValue helpers — not a real Lua parser, same
// "automatically generated, very regular" assumption the rest of this
// codebase's lua tooling already relies on (SC2SaveLuaParser's doc
// comment). All five block kinds — including Armies/Units — were verified
// directly against real files (SC2_CA_C01_save.lua, SC2_MP_001_save.lua;
// see SC2ObjectsExport.cs's header for the exact per-field bracket/wrapper
// findings). Kept in a per-army try/catch regardless, since a from-scratch
// or hand-edited map could still contain a shape this parser doesn't expect
// — one bad army is reported as a warning rather than aborting the import.
//
// Fields this editor doesn't model at all (ScenarioInfo, Configurations,
// decals, etc.) are left alone: the raw text is kept verbatim in
// ObjSaveLuaTextOriginal/ObjScenarioLuaTextOriginal so a later export only
// ever touches the blocks parsed here.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;
using BdfIO.Core;
using BdfIO.Formats.MapObjs;

public static class SC2ObjectsImport
{
    public sealed class Result
    {
        public bool Success;
        public string Summary = "";
        public List<string> Warnings = new();
        public int SaveLuaMarkerCount;
        public int MapObjsGraphMarkerCount;
        public int IgnoredMapObjsMarkerCount;
    }

    /// <summary>
    /// terrainBdfPath: path to an already-extracted "*.terrain.win.bdf" (the
    /// same file picked for "Import terrain.bdf") — mapobjs.bdf/_save.lua/
    /// _scenario.lua are located by stem next to it (searching up to 3
    /// parent directories, to tolerate both a flat folder and a nested
    /// maps/&lt;name&gt;/ layout without hardcoding either).
    /// </summary>
    public static Result ImportFromTerrainBdfPath(SC2TerrainData data, string terrainBdfPath)
    {
        var result = new Result();
        data.EnsureObjectsLists();

        if (string.IsNullOrEmpty(terrainBdfPath) || !File.Exists(terrainBdfPath))
        {
            result.Summary = "Terrain BDF path not found.";
            return result;
        }

        var fileName = Path.GetFileName(terrainBdfPath);
        const string suffix = ".terrain.win.bdf";
        if (!fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            result.Summary = "Expected a '*.terrain.win.bdf' path.";
            return result;
        }
        var stem = fileName.Substring(0, fileName.Length - suffix.Length);
        var startDir = Path.GetDirectoryName(terrainBdfPath);

        // Import is a full replacement. Keeping any domain from the previous
        // map makes missing files look like duplicate objects on the next map.
        data.ObjProps.Clear();
        data.ObjMarkers.Clear();
        data.ObjGraphs.Clear();
        data.ObjZones.Clear();
        data.ObjArmies.Clear();
        data.ObjUnits.Clear();
        data.ObjChains.Clear();
        data.ObjSymmetryGroups.Clear();
        data.ObjSaveLuaTextOriginal = "";
        data.ObjScenarioLuaTextOriginal = "";
        data.EnsureObjectIds();

        var mapObjsPath = FindSibling(startDir, $"{stem}.mapobjs.win.bdf");
        var saveLuaPath = FindSibling(startDir, $"{stem}_save.lua");
        var scenarioLuaPath = FindSibling(startDir, $"{stem}_scenario.lua");

        var propCount = 0; var markerCount = 0; var graphCount = 0;
        var zoneCount = 0; var armyCount = 0; var unitCount = 0; var chainCount = 0;
        MapObjsModel mapObjsModel = null;

        if (mapObjsPath != null)
        {
            try
            {
                var container = BdfContainer.Read(File.ReadAllBytes(mapObjsPath), MapObjsBdf.Padding);
                mapObjsModel = MapObjsBdf.Parse(container);
            }
            catch (Exception ex) { result.Warnings.Add("mapobjs.bdf: " + ex.Message); }
        }
        else
        {
            result.Warnings.Add("mapobjs.bdf not found next to terrain.bdf — Props and Path Graphs left empty.");
        }

        if (saveLuaPath != null)
        {
            try
            {
                // Real _save.lua as extracted from an actual .scd is
                // usually Lua 5.1 BYTECODE, not the plain text the
                // "uncompiled" reference source corpus has - a bytecode
                // file fed to File.ReadAllText decodes as garbage, so every
                // text-based block-finder below silently finds nothing.
                // Markers/Chains/Armies/Units come from _save.lua, so the
                // bytecode form must be decoded before BDF graph references
                // are merged into that authoritative marker set.
                var saveLuaBytes = File.ReadAllBytes(saveLuaPath);
                if (SC2SaveLuaBytecodeReader.IsBytecode(saveLuaBytes))
                {
                    // No sensible "original text" exists to patch on a
                    // later export (SC2SaveLuaWriter splices into it) -
                    // leave ObjSaveLuaTextOriginal empty so export instead
                    // regenerates a real from-scratch skeleton, per this
                    // project's own decision that exported maps should
                    // never contain bytecode.
                    var scenario = SC2SaveLuaBytecodeReader.ReadGlobalTable(saveLuaBytes, "Scenario");
                    if (scenario == null)
                    {
                        result.Warnings.Add("_save.lua: bytecode decoded but no 'Scenario' global table was found.");
                    }
                    else
                    {
                        PopulateMarkerExtrasFromSaveLuaTree(data, scenario);
                        zoneCount = PopulateAreasFromSaveLuaTree(data, scenario);
                        chainCount = PopulateChainsFromSaveLuaTree(data, scenario);
                        (armyCount, unitCount) = PopulateArmiesFromSaveLuaTree(data, scenario, result.Warnings);
                    }
                }
                else
                {
                    var text = File.ReadAllText(saveLuaPath);
                    data.ObjSaveLuaTextOriginal = text;
                    PopulateMarkerExtrasFromSaveLua(data, text);
                    zoneCount = PopulateAreasFromSaveLua(data, text);
                    chainCount = PopulateChainsFromSaveLua(data, text);
                    (armyCount, unitCount) = PopulateArmiesFromSaveLua(data, text, result.Warnings);
                }
            }
            catch (Exception ex) { result.Warnings.Add("_save.lua: " + ex.Message); }
        }
        else
        {
            result.Warnings.Add("_save.lua not found — only graph-referenced path markers can be recovered from mapobjs.bdf.");
        }

        // Resolve BDF graph endpoints only after save.lua markers exist.
        // Name/placement matches reuse the Lua object without overwriting
        // any Lua fields or position. Unreferenced BDF markers are ignored.
        data.EnsureObjectIds();
        if (mapObjsModel != null)
        {
            var merge = PopulatePropsAndPathGraphsFromMapObjs(data, mapObjsModel);
            result.MapObjsGraphMarkerCount = merge.AddedPathMarkers;
            result.IgnoredMapObjsMarkerCount = merge.UnreferencedMarkers;
            if (merge.UnreferencedMarkers > 0)
                result.Warnings.Add($"mapobjs.bdf: ignored {merge.UnreferencedMarkers} marker(s) not referenced by a path graph.");
            if (merge.ReusedSaveMarkers > 0)
                result.Warnings.Add($"mapobjs.bdf: reused {merge.ReusedSaveMarkers} save.lua marker(s) as graph endpoints.");
            if (merge.DuplicateBdfMarkers > 0)
                result.Warnings.Add($"mapobjs.bdf: collapsed {merge.DuplicateBdfMarkers} duplicate graph marker row(s).");
            if (merge.InvalidEdges > 0)
                result.Warnings.Add($"mapobjs.bdf: skipped {merge.InvalidEdges} graph edge(s) with invalid indexes or a self-reference.");
        }

        if (scenarioLuaPath != null)
        {
            try
            {
                var scenarioLuaBytes = File.ReadAllBytes(scenarioLuaPath);
                // Scenario data is retained for later patch/export, but it is
                // never an object-import source. In particular StartPositions
                // must not override or recreate save.lua markers.
                if (!SC2SaveLuaBytecodeReader.IsBytecode(scenarioLuaBytes))
                    data.ObjScenarioLuaTextOriginal = File.ReadAllText(scenarioLuaPath);
            }
            catch (Exception ex) { result.Warnings.Add("_scenario.lua: " + ex.Message); }
        }
        else
        {
            result.Warnings.Add("_scenario.lua not found — original scenario text is unavailable for patch export.");
        }

        data.EnsureObjectIds();
        var finalDuplicateCount = SC2MarkerDeduplication.RemoveDuplicates(data);
        if (finalDuplicateCount > 0)
            result.Warnings.Add($"Skipped {finalDuplicateCount} duplicate marker(s); the game does not retain duplicate marker table entries.");

        propCount = data.ObjProps.Count;
        markerCount = data.ObjMarkers.Count;
        graphCount = data.ObjGraphs.Count;
        result.SaveLuaMarkerCount = Math.Max(0, markerCount - result.MapObjsGraphMarkerCount);

        result.Success = true;
        result.Summary = $"Imported: {propCount} props, {markerCount} markers, {graphCount} graphs, " +
                         $"{zoneCount} zones, {armyCount} armies, {unitCount} units, {chainCount} chains. " +
                         $"Marker sources: {result.SaveLuaMarkerCount} save.lua, " +
                         $"{result.MapObjsGraphMarkerCount} graph-only mapobjs; " +
                         $"{result.IgnoredMapObjsMarkerCount} unreferenced mapobjs row(s) ignored.";
        return result;
    }

    private static string FindSibling(string startDir, string fileName)
    {
        var dir = startDir;
        for (var i = 0; i <= 3 && dir != null; i++)
        {
            var candidate = Path.Combine(dir, fileName);
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    // ---- mapobjs.bdf -> ObjProps/ObjGraphs + referenced path markers ----

    private sealed class MapObjsMergeStats
    {
        public int UnreferencedMarkers;
        public int ReusedSaveMarkers;
        public int DuplicateBdfMarkers;
        public int InvalidEdges;
        public int AddedPathMarkers;
    }

    private static MapObjsMergeStats PopulatePropsAndPathGraphsFromMapObjs(SC2TerrainData data, MapObjsModel model)
    {
        var stats = new MapObjsMergeStats();
        data.ObjProps.Clear();
        foreach (var p in model.Props)
            data.ObjProps.Add(new SC2ObjProp
            {
                Id = data.AllocateObjectId(),
                BlueprintPath = p.BlueprintPath,
                Position = new Vector3(p.Position[0], p.Position[1], p.Position[2]),
                Rotation = new Quaternion(p.Quat[1], p.Quat[2], p.Quat[3], p.Quat[0]),
                Scale = p.Scale,
                Flags = p.Flags
            });

        var candidateSourceIndexes = new HashSet<int>();
        foreach (var graph in model.Graphs)
        {
            foreach (var (x, y) in graph.Entries)
            {
                if (x < 0 || x >= model.Markers.Count || y < 0 || y >= model.Markers.Count || x == y)
                {
                    stats.InvalidEdges++;
                    continue;
                }
                candidateSourceIndexes.Add(x);
                candidateSourceIndexes.Add(y);
            }
        }

        data.EnsureObjectIds();
        var markerIdsBySourceIndex = new int[model.Markers.Count];
        var bdfAddedMarkerIds = new HashSet<int>();
        var saveMarkerIds = new HashSet<int>(data.ObjMarkers.Select(marker => marker.Id));
        var markersByName = new Dictionary<string, SC2ObjMarker>(StringComparer.OrdinalIgnoreCase);
        foreach (var marker in data.ObjMarkers)
            if (marker != null && !string.IsNullOrEmpty(marker.Name) && !markersByName.ContainsKey(marker.Name))
                markersByName.Add(marker.Name, marker);

        foreach (var sourceIndex in candidateSourceIndexes.OrderBy(index => index))
        {
            var m = model.Markers[sourceIndex];
            var candidate = new SC2ObjMarker
            {
                Name = m.Name,
                Type = m.MarkerType,
                Position = new Vector3(m.Position[0], m.Position[1], m.Position[2])
            };
            SC2ObjMarker existing = null;
            if (!string.IsNullOrEmpty(m.Name)) markersByName.TryGetValue(m.Name, out existing);
            existing ??= data.ObjMarkers.FirstOrDefault(marker => SC2MarkerDeduplication.SamePlacement(marker, candidate));
            if (existing != null)
            {
                markerIdsBySourceIndex[sourceIndex] = existing.Id;
                if (saveMarkerIds.Contains(existing.Id)) stats.ReusedSaveMarkers++;
                else stats.DuplicateBdfMarkers++;
                continue;
            }

            var marker = candidate;
            marker.Id = data.AllocateObjectId();
            data.ObjMarkers.Add(marker);
            bdfAddedMarkerIds.Add(marker.Id);
            markerIdsBySourceIndex[sourceIndex] = marker.Id;
            if (!string.IsNullOrEmpty(marker.Name)) markersByName.Add(marker.Name, marker);
        }

        data.ObjGraphs.Clear();
        var usedMarkerIds = new HashSet<int>();
        var usedSourceIndexes = new HashSet<int>();
        foreach (var g in model.Graphs)
        {
            var graph = new SC2ObjGraph { Id = data.AllocateObjectId(), Name = g.Name, GraphKind = ClassifyGraphKind(g.Name) };
            foreach (var (x, y) in g.Entries)
            {
                if (x < 0 || x >= markerIdsBySourceIndex.Length || y < 0 || y >= markerIdsBySourceIndex.Length) continue;
                if (markerIdsBySourceIndex[x] == 0 || markerIdsBySourceIndex[y] == 0) continue;
                if (markerIdsBySourceIndex[x] == markerIdsBySourceIndex[y]) continue;
                SC2ObjectGraphRefs.AddEdge(graph, markerIdsBySourceIndex[x], markerIdsBySourceIndex[y]);
                usedMarkerIds.Add(markerIdsBySourceIndex[x]);
                usedMarkerIds.Add(markerIdsBySourceIndex[y]);
                usedSourceIndexes.Add(x);
                usedSourceIndexes.Add(y);
            }
            data.ObjGraphs.Add(graph);
        }

        // A structurally valid BDF row is not enough: after name/placement
        // deduplication both endpoints can resolve to the same retained Lua
        // marker, causing AddEdge to reject the edge. Remove any marker that
        // came only from mapobjs and is absent from every surviving edge.
        data.ObjMarkers.RemoveAll(marker => bdfAddedMarkerIds.Contains(marker.Id) && !usedMarkerIds.Contains(marker.Id));
        stats.AddedPathMarkers = bdfAddedMarkerIds.Count(usedMarkerIds.Contains);
        stats.UnreferencedMarkers = model.Markers.Count - usedSourceIndexes.Count;
        return stats;
    }

    /// <summary>Real vanilla mapobjs.bdf graph names, confirmed against all
    /// 56 corpus files (a standalone probe over
    /// gamedata.scd\maps\*\*.mapobjs.win.bdf): "LandGraph"/"AirGraph"/
    /// "NavalGraph"/"AmphibiousGraph" (plus one real vanilla typo,
    /// "AmphbiousGraph" on one map, missing the 'i' — handled by a substring
    /// check on "amph" rather than a strict prefix match). SC2ObjGraph.
    /// GraphKind previously stayed at its class-default "Land" for every
    /// imported graph (never set here), so SC2ObjectsShell.EnsureRequiredGraphs
    /// — which recognizes the required Land/Air/Naval/Amphibious set purely
    /// by GraphKind — saw only "Land" satisfied and silently created 1-3
    /// extra empty graphs for the "missing" kinds on every import, on top of
    /// the real imported ones (reported as "more than 4 graphs after
    /// import" on a playable-water map). Matching real names to the correct
    /// kind here is what lets EnsureRequiredGraphs recognize them as already
    /// satisfied.</summary>
    private static string ClassifyGraphKind(string name)
    {
        if (string.IsNullOrEmpty(name)) return "Land";
        var n = name.ToLowerInvariant();
        if (n.Contains("amph")) return "Amphibious";
        if (n.Contains("naval")) return "Naval";
        if (n.Contains("air")) return "Air";
        if (n.Contains("land")) return "Land";
        return "Land";
    }

    // ---- _save.lua: authoritative marker source ----

    private static void PopulateMarkerExtrasFromSaveLua(SC2TerrainData data, string saveLuaText)
    {
        var (markers, _, _) = SC2SaveLuaParser.Parse(saveLuaText);

        foreach (var marker in data.ObjMarkers)
            if (markers.TryGetValue(marker.Name, out var fields))
                ApplyLuaFieldsToMarker(marker, fields);

        var existingNames = new HashSet<string>(data.ObjMarkers.Select(m => m.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var (name, fields) in markers)
        {
            if (existingNames.Contains(name)) continue;
            var marker = new SC2ObjMarker { Name = name };
            ApplyLuaFieldsToMarker(marker, fields);
            if (data.ObjMarkers.Any(existing => SC2MarkerDeduplication.SamePlacement(existing, marker))) continue;
            data.ObjMarkers.Add(marker);
            existingNames.Add(name);
        }
    }

    private static void ApplyLuaFieldsToMarker(SC2ObjMarker marker, List<SC2LuaField> fields)
    {
        var extras = new List<SC2LuaField>();
        foreach (var f in fields)
        {
            switch (f.Key)
            {
                case "type":
                    marker.Type = SC2LuaValue.ExtractString(f.RawValue) ?? marker.Type;
                    break;
                case "position":
                    var pos = SC2LuaValue.ExtractVector3(f.RawValue);
                    if (pos.HasValue) marker.Position = pos.Value;
                    break;
                case "prop":
                    marker.Prop = SC2LuaValue.ExtractString(f.RawValue) ?? marker.Prop;
                    break;
                case "color":
                    marker.ColorHex = SC2LuaValue.ExtractString(f.RawValue) ?? marker.ColorHex;
                    break;
                case "orientation":
                    var vec = SC2LuaValue.ExtractVector3(f.RawValue);
                    if (vec.HasValue)
                    {
                        marker.OrientationEuler = vec.Value;
                        marker.OrientationIsQuaternion = false;
                    }
                    else
                    {
                        var quat = SC2LuaValue.ExtractQuaternion(f.RawValue);
                        if (quat.HasValue)
                        {
                            marker.OrientationEuler = quat.Value.eulerAngles;
                            marker.OrientationIsQuaternion = true;
                        }
                    }
                    break;
                case "adjacentTo":
                    break; // recomputed from ObjGraphs on export, not stored verbatim
                default:
                    extras.Add(f);
                    break;
            }
        }
        marker.ExtraFields = extras;
    }

    // ---- _save.lua: Areas ----

    private static readonly Regex BracketEntryRe = new(@"\['(?<name>[^']*)'\]\s*=\s*\{", RegexOptions.Compiled);
    private static readonly Regex RectangleRe = new(
        @"RECTANGLE\(\s*(?<x1>-?[\d.]+)\s*,\s*(?<z1>-?[\d.]+)\s*,\s*(?<x2>-?[\d.]+)\s*,\s*(?<z2>-?[\d.]+)\s*\)", RegexOptions.Compiled);

    private static int PopulateAreasFromSaveLua(SC2TerrainData data, string saveLuaText)
    {
        data.ObjZones.Clear();
        var (open, close) = SC2LuaBlocks.FindBlock(saveLuaText, "Areas = ");
        if (open < 0) return 0;

        var content = saveLuaText.Substring(open + 1, close - open - 1);
        foreach (Match m in BracketEntryRe.Matches(content))
        {
            var name = m.Groups["name"].Value;
            var entryOpen = m.Index + m.Length - 1;
            var entryClose = SC2LuaBlocks.FindMatchingBrace(content, entryOpen);
            if (entryClose < 0) continue;
            var body = content.Substring(entryOpen + 1, entryClose - entryOpen - 1);

            var zone = new SC2ObjZone { Name = name, Role = SC2ObjZone.GuessRole(name) };
            var rm = RectangleRe.Match(body);
            if (rm.Success)
            {
                zone.X1 = ParseF(rm.Groups["x1"].Value);
                zone.Z1 = ParseF(rm.Groups["z1"].Value);
                zone.X2 = ParseF(rm.Groups["x2"].Value);
                zone.Z2 = ParseF(rm.Groups["z2"].Value);
            }
            data.ObjZones.Add(zone);
        }
        return data.ObjZones.Count;
    }

    // ---- _save.lua: Chains ----

    private static readonly Regex QuotedNameRe = new("\"(?<n>[^\"]*)\"", RegexOptions.Compiled);

    private static int PopulateChainsFromSaveLua(SC2TerrainData data, string saveLuaText)
    {
        data.ObjChains.Clear();
        var (open, close) = SC2LuaBlocks.FindBlock(saveLuaText, "Chains = ");
        if (open < 0) return 0;

        var content = saveLuaText.Substring(open + 1, close - open - 1);
        foreach (Match m in BracketEntryRe.Matches(content))
        {
            var name = m.Groups["name"].Value;
            var entryOpen = m.Index + m.Length - 1;
            var entryClose = SC2LuaBlocks.FindMatchingBrace(content, entryOpen);
            if (entryClose < 0) continue;
            var body = content.Substring(entryOpen + 1, entryClose - entryOpen - 1);

            var chain = new SC2ObjChain { Name = name };
            foreach (Match nm in QuotedNameRe.Matches(body))
                chain.MarkerNames.Add(nm.Groups["n"].Value);
            data.ObjChains.Add(chain);
        }
        return data.ObjChains.Count;
    }

    // ---- _save.lua: Armies (+ nested Units tree) ----
    // Verified directly against two real files (SC2_CA_C01_save.lua,
    // SC2_MP_001_save.lua — see SC2ObjectsExport.cs's header for the exact
    // findings). The real format mixes bracketed-and-quoted keys
    // (['color']/['Units']/['Position']/['Orientation']) with bare
    // identifier keys (personality/plans/faction/Economy/Alliances/type)
    // *within the same entry*, so every lookup below tries both forms
    // rather than assuming one. Wrapped in a try/catch per army so one
    // malformed entry doesn't abort the rest of the import.

    private static readonly Regex KeyValueRe = new(
        @"(?:\['(?<bkey>[^']*)'\]|(?<key>[A-Za-z_][A-Za-z0-9_]*))\s*=\s*(?<value>.+?),\s*\r?$",
        RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex UnitEntryRe = new(
        @"\['(?<name>[^']*)'\]\s*=\s*(?<group>GROUP)?\s*\{", RegexOptions.Compiled);

    private static string KeyOf(Match m) => m.Groups["bkey"].Success ? m.Groups["bkey"].Value : m.Groups["key"].Value;

    /// <summary>Finds a "key = {...}" or "['key'] = {...}" block — real
    /// files use either style for the same key name depending on the field,
    /// so both are tried.</summary>
    private static (int Open, int Close) FindNamedBlock(string text, string key)
    {
        var bracketed = SC2LuaBlocks.FindBlock(text, $"['{key}'] = ");
        if (bracketed.Open >= 0) return bracketed;
        return SC2LuaBlocks.FindBlock(text, $"{key} = ");
    }

    /// <summary>BracketEntryRe.Matches(content) over the WHOLE Armies block
    /// (as PopulateAreasFromSaveLua/PopulateChainsFromSaveLua do for their
    /// own blocks) does NOT respect nesting - unlike Areas/Chains entries
    /// (whose only nested content, RECTANGLE(...)/Markers={"str",...},
    /// never itself matches this bracket-key-to-table pattern), every
    /// Army's own nested Units tree DOES use the identical ['name'] = {...}
    /// / ['name'] = GROUP {...} syntax - so the unscoped regex matched
    /// every unit and sub-group buried anywhere inside every army as if it
    /// were ALSO a separate top-level army. Confirmed against a real map:
    /// only 3 real top-level armies exist, but the unscoped match produced
    /// 218, and re-running ParseUnitsTree from each fake "army" that
    /// happened to itself contain a Units sub-key re-added (duplicated)
    /// that whole subtree's units a second time - inflating a real
    /// ~278-unit map to 2825 recorded units. Fixed by manually advancing
    /// the search position past each fully-consumed top-level entry's
    /// closing brace, so nothing inside it can ever be re-matched as a
    /// sibling.</summary>
    private static (int armies, int units) PopulateArmiesFromSaveLua(SC2TerrainData data, string saveLuaText, List<string> warnings)
    {
        data.ObjArmies.Clear();
        data.ObjUnits.Clear();
        var (open, close) = SC2LuaBlocks.FindBlock(saveLuaText, "Armies = ");
        if (open < 0) return (0, 0);

        var content = saveLuaText.Substring(open + 1, close - open - 1);
        var searchFrom = 0;
        while (searchFrom < content.Length)
        {
            var m = BracketEntryRe.Match(content, searchFrom);
            if (!m.Success) break;
            var name = m.Groups["name"].Value;
            var entryOpen = m.Index + m.Length - 1;
            var entryClose = SC2LuaBlocks.FindMatchingBrace(content, entryOpen);
            if (entryClose < 0) { searchFrom = m.Index + m.Length; continue; }
            searchFrom = entryClose + 1; // past this whole army's body - its nested Units can't be re-matched as a sibling

            try
            {
                var body = content.Substring(entryOpen + 1, entryClose - entryOpen - 1);

                var army = new SC2ObjArmy
                {
                    Name = name,
                    Personality = ExtractScalarString(body, "personality"),
                    Plans = ExtractScalarString(body, "plans"),
                    Color = ExtractScalarFloat(body, "color"),
                    Faction = (int)ExtractScalarFloat(body, "faction"),
                    Mass = ExtractNestedScalarFloat(body, "Economy", "mass"),
                    Energy = ExtractNestedScalarFloat(body, "Economy", "energy")
                };

                var (allianceOpen, allianceClose) = FindNamedBlock(body, "Alliances");
                if (allianceOpen >= 0)
                {
                    var allianceBody = body.Substring(allianceOpen + 1, allianceClose - allianceOpen - 1);
                    foreach (Match am in BracketKeyLineRe.Matches(allianceBody))
                        army.Alliances.Add(new SC2ObjAlliance
                        {
                            OtherArmy = am.Groups["key"].Value,
                            Stance = SC2LuaValue.ExtractString(am.Groups["value"].Value) ?? "ENEMY"
                        });
                }

                data.ObjArmies.Add(army);

                // Army-level Units wrapper is itself a GROUP (['Units'] =
                // GROUP { Units = {...} }) — unwrap the outer GROUP body
                // first, then look for its bare 'Units' sub-key, same as any
                // nested group (ParseUnitsTree/FindNamedBlock handle the
                // per-file bracket-style variance either way).
                var (unitsOpen, unitsClose) = FindNamedBlock(body, "Units");
                if (unitsOpen >= 0)
                {
                    var unitsWrapperBody = body.Substring(unitsOpen + 1, unitsClose - unitsOpen - 1);
                    var (innerOpen, innerClose) = FindNamedBlock(unitsWrapperBody, "Units");
                    var unitsBody = innerOpen >= 0
                        ? unitsWrapperBody.Substring(innerOpen + 1, innerClose - innerOpen - 1)
                        : unitsWrapperBody;
                    ParseUnitsTree(data, name, unitsBody, -1);
                }
            }
            catch (Exception ex)
            {
                warnings.Add($"Army '{name}': {ex.Message} (skipped)");
            }
        }
        return (data.ObjArmies.Count, data.ObjUnits.Count);
    }

    private static readonly Regex BracketKeyLineRe = new(
        @"\['(?<key>[^']*)'\]\s*=\s*(?<value>.+?),\s*\r?$", RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>Same unscoped-Regex.Matches bug as PopulateArmiesFromSaveLua
    /// (see its own doc comment) recurs one level deeper here: a GROUP
    /// node's descendants are nested INSIDE its own consumed span, but
    /// UnitEntryRe.Matches(body) doesn't know that and re-matches them as
    /// if they were siblings of `body` too - in addition to being correctly
    /// walked via the recursive ParseUnitsTree call a few lines down. Fixed
    /// the same way: advance the search position past each fully-consumed
    /// entry's closing brace before looking for the next match.</summary>
    private static void ParseUnitsTree(SC2TerrainData data, string armyName, string body, int parentIndex)
    {
        var searchFrom = 0;
        while (searchFrom < body.Length)
        {
            var m = UnitEntryRe.Match(body, searchFrom);
            if (!m.Success) break;
            var name = m.Groups["name"].Value;
            var isGroup = m.Groups["group"].Success;
            var entryOpen = m.Index + m.Length - 1;
            var entryClose = SC2LuaBlocks.FindMatchingBrace(body, entryOpen);
            if (entryClose < 0) { searchFrom = m.Index + m.Length; continue; }
            searchFrom = entryClose + 1;
            var entryBody = body.Substring(entryOpen + 1, entryClose - entryOpen - 1);

            var node = new SC2ObjUnitNode { ArmyName = armyName, Name = name, ParentIndex = parentIndex, IsGroup = isGroup };
            if (isGroup)
                node.IsWreckage = ExtractScalarBool(entryBody, "wreckage");
            data.ObjUnits.Add(node);
            var nodeIndex = data.ObjUnits.Count - 1;

            if (isGroup)
            {
                // Nested groups reuse the same bare 'Units' sub-key at every
                // depth (confirmed against real files) — no "Interior" key.
                var (childOpen, childClose) = FindNamedBlock(entryBody, "Units");
                if (childOpen >= 0)
                {
                    var childBody = entryBody.Substring(childOpen + 1, childClose - childOpen - 1);
                    ParseUnitsTree(data, armyName, childBody, nodeIndex);
                }
            }
            else
            {
                node.Type = ExtractScalarString(entryBody, "type");
                node.OrientationEuler = ExtractScalarVector3(entryBody, "Orientation");
                node.Position = ExtractScalarVector3(entryBody, "Position");
            }
        }
    }

    private static string ExtractScalarString(string body, string key)
    {
        foreach (Match m in KeyValueRe.Matches(body))
            if (KeyOf(m) == key)
                return SC2LuaValue.ExtractString(m.Groups["value"].Value) ?? "";
        return "";
    }

    private static bool ExtractScalarBool(string body, string key)
    {
        foreach (Match m in KeyValueRe.Matches(body))
            if (KeyOf(m) == key)
                return m.Groups["value"].Value.Trim().StartsWith("true", StringComparison.OrdinalIgnoreCase);
        return false;
    }

    private static float ExtractScalarFloat(string body, string key)
    {
        foreach (Match m in KeyValueRe.Matches(body))
            if (KeyOf(m) == key)
                return SC2LuaValue.ExtractFloat(m.Groups["value"].Value) ?? 0f;
        return 0f;
    }

    private static Vector3 ExtractScalarVector3(string body, string key)
    {
        foreach (Match m in KeyValueRe.Matches(body))
            if (KeyOf(m) == key)
                return SC2LuaValue.ExtractVector3(m.Groups["value"].Value) ?? Vector3.zero;
        return Vector3.zero;
    }

    private static float ExtractNestedScalarFloat(string body, string blockKey, string key)
    {
        var (open, close) = FindNamedBlock(body, blockKey);
        if (open < 0) return 0f;
        var inner = body.Substring(open + 1, close - open - 1);
        return ExtractScalarFloat(inner, key);
    }

    private static float ParseF(string s) => float.Parse(s, System.Globalization.CultureInfo.InvariantCulture);

    // ---- Bytecode-tree equivalents of the text-based populate functions
    // above. Each mirrors its text sibling's exact semantics (same field
    // names, same "existing marker gets enriched / new one gets created"
    // shape) but reads from the SC2LuaBytecodeValue tree
    // SC2SaveLuaBytecodeReader produces instead of regex-matching text. ----

    private static void PopulateMarkerExtrasFromSaveLuaTree(SC2TerrainData data, SC2LuaBytecodeValue scenario)
    {
        var markersTable = SC2SaveLuaBytecodeReader.FindMarkers(scenario);
        if (markersTable?.Kind != SC2LuaBytecodeValueKind.Table) return;

        var existingNames = new HashSet<string>(data.ObjMarkers.Select(m => m.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var (name, entry) in markersTable.TableValue.Fields)
        {
            if (entry?.Kind != SC2LuaBytecodeValueKind.Table) continue;
            var marker = data.ObjMarkers.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
            var isNew = marker == null;
            marker ??= new SC2ObjMarker { Name = name };
            ApplyLuaTreeFieldsToMarker(marker, entry.TableValue);
            if (isNew && !existingNames.Contains(name))
            {
                if (data.ObjMarkers.Any(existing => SC2MarkerDeduplication.SamePlacement(existing, marker))) continue;
                data.ObjMarkers.Add(marker);
                existingNames.Add(name);
            }
        }
    }

    private static void ApplyLuaTreeFieldsToMarker(SC2ObjMarker marker, SC2LuaBytecodeTable fields)
    {
        var extras = new List<SC2LuaField>();
        foreach (var (key, value) in fields.Fields)
        {
            switch (key)
            {
                case "type":
                    marker.Type = SC2SaveLuaBytecodeReader.AsString(value) ?? marker.Type;
                    break;
                case "position":
                    var pos = SC2SaveLuaBytecodeReader.AsVector3(value);
                    if (pos.HasValue) marker.Position = pos.Value;
                    break;
                case "prop":
                    marker.Prop = SC2SaveLuaBytecodeReader.AsString(value) ?? marker.Prop;
                    break;
                case "color":
                    marker.ColorHex = SC2SaveLuaBytecodeReader.AsString(value) ?? marker.ColorHex;
                    break;
                case "orientation":
                    var vec = SC2SaveLuaBytecodeReader.AsVector3(value);
                    if (vec.HasValue)
                    {
                        marker.OrientationEuler = vec.Value;
                        marker.OrientationIsQuaternion = false;
                    }
                    else
                    {
                        var quat = SC2SaveLuaBytecodeReader.AsQuaternion(value);
                        if (quat.HasValue)
                        {
                            marker.OrientationEuler = quat.Value.eulerAngles;
                            marker.OrientationIsQuaternion = true;
                        }
                    }
                    break;
                case "adjacentTo":
                    break; // recomputed from ObjGraphs on export, not stored verbatim
                default:
                    extras.Add(new SC2LuaField { Key = key, RawValue = SerializeValueForExtra(value) });
                    break;
            }
        }
        marker.ExtraFields = extras;
    }

    private static int PopulateAreasFromSaveLuaTree(SC2TerrainData data, SC2LuaBytecodeValue scenario)
    {
        data.ObjZones.Clear();
        var areasTable = SC2SaveLuaBytecodeReader.FindAreas(scenario);
        if (areasTable?.Kind != SC2LuaBytecodeValueKind.Table) return 0;

        foreach (var (name, entry) in areasTable.TableValue.Fields)
        {
            var zone = new SC2ObjZone { Name = name, Role = SC2ObjZone.GuessRole(name) };
            if (entry?.Kind == SC2LuaBytecodeValueKind.Table &&
                entry.TableValue.Fields.TryGetValue("rectangle", out var rectValue))
            {
                var rect = SC2SaveLuaBytecodeReader.AsRectangle(rectValue);
                if (rect.HasValue)
                {
                    zone.X1 = rect.Value.X1; zone.Z1 = rect.Value.Z1;
                    zone.X2 = rect.Value.X2; zone.Z2 = rect.Value.Z2;
                }
            }
            data.ObjZones.Add(zone);
        }
        return data.ObjZones.Count;
    }

    private static int PopulateChainsFromSaveLuaTree(SC2TerrainData data, SC2LuaBytecodeValue scenario)
    {
        data.ObjChains.Clear();
        var chainsTable = SC2SaveLuaBytecodeReader.FindChains(scenario);
        if (chainsTable?.Kind != SC2LuaBytecodeValueKind.Table) return 0;

        foreach (var (name, entry) in chainsTable.TableValue.Fields)
        {
            var chain = new SC2ObjChain { Name = name };
            if (entry?.Kind == SC2LuaBytecodeValueKind.Table &&
                entry.TableValue.Fields.TryGetValue("Markers", out var markersList) &&
                markersList.Kind == SC2LuaBytecodeValueKind.Table)
            {
                foreach (var item in markersList.TableValue.Items)
                    if (item.Kind == SC2LuaBytecodeValueKind.String)
                        chain.MarkerNames.Add(item.StringValue);
            }
            data.ObjChains.Add(chain);
        }
        return data.ObjChains.Count;
    }

    private static (int armies, int units) PopulateArmiesFromSaveLuaTree(SC2TerrainData data, SC2LuaBytecodeValue scenario, List<string> warnings)
    {
        data.ObjArmies.Clear();
        data.ObjUnits.Clear();
        var armiesTable = SC2SaveLuaBytecodeReader.FindArmies(scenario);
        if (armiesTable?.Kind != SC2LuaBytecodeValueKind.Table) return (0, 0);

        foreach (var (name, entry) in armiesTable.TableValue.Fields)
        {
            if (entry?.Kind != SC2LuaBytecodeValueKind.Table) continue;
            try
            {
                var fields = entry.TableValue.Fields;
                var army = new SC2ObjArmy
                {
                    Name = name,
                    Personality = SC2SaveLuaBytecodeReader.AsString(fields.GetValueOrDefault("personality")) ?? "",
                    Plans = SC2SaveLuaBytecodeReader.AsString(fields.GetValueOrDefault("plans")) ?? "",
                    Color = SC2SaveLuaBytecodeReader.AsFloat(fields.GetValueOrDefault("color")) ?? 0f,
                    Faction = (int)(SC2SaveLuaBytecodeReader.AsFloat(fields.GetValueOrDefault("faction")) ?? 0f)
                };

                if (fields.TryGetValue("Economy", out var econ) && econ.Kind == SC2LuaBytecodeValueKind.Table)
                {
                    army.Mass = SC2SaveLuaBytecodeReader.AsFloat(econ.TableValue.Fields.GetValueOrDefault("mass")) ?? 0f;
                    army.Energy = SC2SaveLuaBytecodeReader.AsFloat(econ.TableValue.Fields.GetValueOrDefault("energy")) ?? 0f;
                }

                if (fields.TryGetValue("Alliances", out var alliances) && alliances.Kind == SC2LuaBytecodeValueKind.Table)
                {
                    foreach (var (otherArmy, stanceValue) in alliances.TableValue.Fields)
                        army.Alliances.Add(new SC2ObjAlliance
                        {
                            OtherArmy = otherArmy,
                            Stance = SC2SaveLuaBytecodeReader.AsString(stanceValue) ?? "ENEMY"
                        });
                }

                data.ObjArmies.Add(army);

                // ['Units'] = GROUP { Units = {...} } - the GROUP wrapper's
                // OWN "Units" field is the real per-unit-name table (one
                // unwrap, not the text parser's apparent two - its "double
                // FindNamedBlock" was locating the GROUP's braces and then
                // the nested key within them, which collapses to a single
                // direct Fields lookup once the value is already a tree).
                if (fields.TryGetValue("Units", out var unitsGroup) &&
                    SC2SaveLuaBytecodeReader.WrapperOf(unitsGroup) == "GROUP" &&
                    unitsGroup.TableValue.Fields.TryGetValue("Units", out var unitsBody))
                {
                    ParseUnitsTreeFromTree(data, name, unitsBody, -1);
                }
            }
            catch (Exception ex)
            {
                warnings.Add($"Army '{name}': {ex.Message} (skipped)");
            }
        }
        return (data.ObjArmies.Count, data.ObjUnits.Count);
    }

    private static void ParseUnitsTreeFromTree(SC2TerrainData data, string armyName, SC2LuaBytecodeValue unitsBody, int parentIndex)
    {
        if (unitsBody?.Kind != SC2LuaBytecodeValueKind.Table) return;
        foreach (var (name, entry) in unitsBody.TableValue.Fields)
        {
            if (entry?.Kind != SC2LuaBytecodeValueKind.Table) continue;
            var isGroup = SC2SaveLuaBytecodeReader.WrapperOf(entry) == "GROUP";
            var node = new SC2ObjUnitNode { ArmyName = armyName, Name = name, ParentIndex = parentIndex, IsGroup = isGroup };
            if (isGroup)
                node.IsWreckage = SC2SaveLuaBytecodeReader.AsBool(entry.TableValue.Fields.GetValueOrDefault("wreckage")) ?? false;
            data.ObjUnits.Add(node);
            var nodeIndex = data.ObjUnits.Count - 1;

            if (isGroup)
            {
                // Nested groups reuse the same "Units" sub-key at every
                // depth (confirmed against real files).
                if (entry.TableValue.Fields.TryGetValue("Units", out var childUnits))
                    ParseUnitsTreeFromTree(data, armyName, childUnits, nodeIndex);
            }
            else
            {
                node.Type = SC2SaveLuaBytecodeReader.AsString(entry.TableValue.Fields.GetValueOrDefault("type")) ?? "";
                node.OrientationEuler = SC2SaveLuaBytecodeReader.AsVector3(entry.TableValue.Fields.GetValueOrDefault("Orientation")) ?? Vector3.zero;
                node.Position = SC2SaveLuaBytecodeReader.AsVector3(entry.TableValue.Fields.GetValueOrDefault("Position")) ?? Vector3.zero;
            }
        }
    }

    /// <summary>Best-effort re-serialization of a bytecode-tree value back
    /// into the same wrapper syntax the text format uses - only needed for
    /// marker fields this editor doesn't specifically model (extras like
    /// hint/resource/amount/size/graph), which are preserved as opaque text
    /// and re-emitted verbatim on export. Never round-trips a nested
    /// unwrapped table (not a real shape any observed extra field takes) -
    /// falls back to an empty table literal rather than throwing.</summary>
    private static string SerializeValueForExtra(SC2LuaBytecodeValue value)
    {
        var wrapper = SC2SaveLuaBytecodeReader.WrapperOf(value);
        switch (wrapper)
        {
            case "STRING": return $"STRING( '{SC2SaveLuaBytecodeReader.AsString(value)?.Replace("'", "\\'")}' )";
            case "FLOAT": return $"FLOAT( {SC2SaveLuaBytecodeReader.AsFloat(value)?.ToString(System.Globalization.CultureInfo.InvariantCulture)} )";
            case "BOOLEAN": return $"BOOLEAN( {(SC2SaveLuaBytecodeReader.AsBool(value) == true ? "true" : "false")} )";
            case "VECTOR3":
                var v3 = SC2SaveLuaBytecodeReader.AsVector3(value) ?? Vector3.zero;
                return $"VECTOR3( {v3.x.ToString(System.Globalization.CultureInfo.InvariantCulture)}, {v3.y.ToString(System.Globalization.CultureInfo.InvariantCulture)}, {v3.z.ToString(System.Globalization.CultureInfo.InvariantCulture)} )";
            case "QUATERNION":
                var q = SC2SaveLuaBytecodeReader.AsQuaternion(value) ?? Quaternion.identity;
                return $"QUATERNION( {q.w.ToString(System.Globalization.CultureInfo.InvariantCulture)}, {q.x.ToString(System.Globalization.CultureInfo.InvariantCulture)}, {q.y.ToString(System.Globalization.CultureInfo.InvariantCulture)}, {q.z.ToString(System.Globalization.CultureInfo.InvariantCulture)} )";
            case "RECTANGLE":
                var r = SC2SaveLuaBytecodeReader.AsRectangle(value);
                return r.HasValue
                    ? $"RECTANGLE( {r.Value.X1.ToString(System.Globalization.CultureInfo.InvariantCulture)}, {r.Value.Z1.ToString(System.Globalization.CultureInfo.InvariantCulture)}, {r.Value.X2.ToString(System.Globalization.CultureInfo.InvariantCulture)}, {r.Value.Z2.ToString(System.Globalization.CultureInfo.InvariantCulture)} )"
                    : "RECTANGLE( 0, 0, 0, 0 )";
            default:
                if (value == null || value.Kind == SC2LuaBytecodeValueKind.Nil) return "BOOLEAN( false )";
                if (value.Kind == SC2LuaBytecodeValueKind.String) return $"'{value.StringValue.Replace("'", "\\'")}'";
                if (value.Kind == SC2LuaBytecodeValueKind.Number) return value.NumberValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (value.Kind == SC2LuaBytecodeValueKind.Boolean) return value.BoolValue ? "true" : "false";
                return "{ }"; // unwrapped nested table - not a real shape for any observed extra field
        }
    }
}
