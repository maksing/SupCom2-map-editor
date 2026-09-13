// SC2ObjectsExport.cs
//
// Converts SC2TerrainData.Obj* (the procedural/build-compatible Objects
// Editor lists, SC2ObjectsModel.cs) into the two on-disk formats the game
// actually reads:
//  - mapobjs.bdf (binary, BdfIO.Formats.MapObjs.MapObjsModel)
//  - the managed blocks of _save.lua/_scenario.lua (text, patched via
//    SC2LuaBlocks — the same brace-counting technique SC2SaveLuaWriter/
//    SC2ScenarioLuaWriter already use for the older, Editor-only,
//    scene-based (Mode A) workflow's export path).
//
// This is the Mode B (procedural/build-compatible) equivalent of those two
// writers; SC2TerrainExporter.DoExport is the only caller. If
// SC2TerrainData.ObjSaveLuaTextOriginal/ObjScenarioLuaTextOriginal are empty
// (a map created from scratch in this editor, never imported from a real
// .scd), a minimal skeleton containing just the blocks this editor manages
// is generated instead of patching — enough for the game to load Objects
// data, but NOT a substitute for the many other real _save.lua/_scenario.lua
// sections (ScenarioInfo, Configurations, etc.) this editor doesn't model.
//
// Markers/Areas/Chains/StartPositions use the exact wrapper syntax
// (STRING()/VECTOR3()/RECTANGLE()) confirmed byte-for-byte against 56 real
// vanilla _save.lua/_scenario.lua files.
// The Armies/Units block was re-verified directly against real files
// after an earlier pass shipped an unverified guess: the real format nests
// each level's children under a
// bare 'Units' sub-key inside 'GROUP { ... }' (NOT a made-up 'Interior'
// wrapper), and — notably — mixes bracketed-and-quoted keys
// (['color']/['Units']/['Position']/['Orientation']) with bare identifier
// keys (personality/plans/faction/Economy/Alliances/type) *within the same
// army entry*, and uses bare unwrapped Lua string/number literals for
// personality/plans/faction/mass/energy/Alliance-stance/unit-type instead of
// STRING()/FLOAT()/INTEGER() wrappers. The writer below reproduces that
// exact per-field mix; SC2ObjectsImport parses both bracketed and bare forms
// per key (and both wrapped and bare literals) so it isn't sensitive to
// which style a given file happens to use.

using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using BdfIO.Formats.MapObjs;
using BdfIO.Formats.Info;

public static class SC2ObjectsExport
{
    private static readonly Regex ArmyPositionRe = new(@"^ARMY_[A-Za-z0-9_]+$", RegexOptions.Compiled);

    /// <summary>Matches ONLY a top-level `name = '...'` line (anchored to
    /// line start via ^ in Multiline mode, so it can't accidentally match
    /// `devname = '...'` — that line's own first non-whitespace characters
    /// are "devname", not "name"). Used by RegenerateScenarioLua to patch
    /// an imported map's real display name on export — see
    /// SC2TerrainData.DisplayName's own doc comment for why this field is
    /// no longer just mapName.</summary>
    private static readonly Regex ScenarioNameFieldRe = new(@"^(\s*)name(\s*=\s*)'[^']*'", RegexOptions.Multiline | RegexOptions.Compiled);

    // ---- "Auto Path Node" resolution (author once, resolve at export) ----
    // A marker authored with Type == "Auto Path Node" (SC2MarkerTemplates)
    // is never written as-is — it's resolved here into "Land Path Node" or
    // "Water Path Node" by comparing sampled terrain height against the
    // map's water elevation (info.bdf's EnvironmentHeader, parsed the same
    // way SC2EnvironmentShell already does for its own Water fields).

    private const string AutoPathNodeType = "Auto Path Node";

    public static string ResolveEffectiveType(SC2TerrainData data, SC2ObjMarker m, byte[] workingEnvironmentHeader = null)
    {
        if (!string.Equals(m.Type, AutoPathNodeType, System.StringComparison.Ordinal)) return m.Type ?? "";
        var waterElevation = GetWaterElevation(data, workingEnvironmentHeader);
        var terrainHeight = SampleTerrainHeight(data, m.Position.x, m.Position.z);
        return terrainHeight < waterElevation ? "Water Path Node" : "Land Path Node";
    }

    /// <summary>Prefers the live SC2MapDocument.Environment.WorkingHeader
    /// (kept current every frame by SC2EnvironmentShell.MaybeSnapshot, and
    /// synced from a freshly-loaded terrain at load time regardless of which
    /// tabs were opened — see SC2TerrainShell.SetData) over
    /// data.EnvironmentHeader, which is only refreshed when the user
    /// explicitly commits/saves — so an edited-but-unsaved WaterElevation is
    /// reflected here immediately instead of only after a save. Takes the
    /// raw header bytes rather than SC2MapSession itself: this file lives in
    /// SC2.Core.asmdef, which (deliberately, see SC2.Core.asmdef's own
    /// scope) cannot reference SC2MapSession's assembly (Scripts/MapDocument
    /// has no asmdef of its own, so it compiles into the implicit default
    /// assembly, and MapDocument itself depends back on SC2.Core's types —
    /// referencing SC2MapSession's type here directly would be a real,
    /// circular cross-assembly reference, not just a style choice).
    /// Public: also used by SC2ObjectsShell's water-aware unit height
    /// snapping (naval/amphibious units), the same real signal Auto Path
    /// Node already relies on here.</summary>
    public static float GetWaterElevation(SC2TerrainData data, byte[] workingEnvironmentHeader = null)
    {
        var documentHeader = workingEnvironmentHeader;
        if (documentHeader != null && documentHeader.Length == 508)
        {
            var docInfo = new InfoModel();
            System.Array.Copy(documentHeader, docInfo.Header, 508);
            return docInfo.WaterElevation;
        }
        if (data.EnvironmentHeader == null || data.EnvironmentHeader.Length < 508) return 0f;
        var info = new InfoModel();
        System.Array.Copy(data.EnvironmentHeader, info.Header, 508);
        return info.WaterElevation;
    }

    /// <summary>Same header-resolution precedence as GetWaterElevation (see
    /// its own doc comment) — whether the decorative water surface is
    /// actually rendered at all. Used to gate prop placement: a map with
    /// water visually disabled has no real "underwater" region for a prop
    /// to be incorrectly placed into, so the WaterElevation comparison alone
    /// (which can be a meaningless leftover/default value on a from-scratch
    /// map with no water authored) must not be trusted without this.</summary>
    public static bool IsWaterEnabled(SC2TerrainData data, byte[] workingEnvironmentHeader = null)
    {
        var documentHeader = workingEnvironmentHeader;
        if (documentHeader != null && documentHeader.Length == 508)
        {
            var docInfo = new InfoModel();
            System.Array.Copy(documentHeader, docInfo.Header, 508);
            return docInfo.WaterEnabled != 0;
        }
        if (data.EnvironmentHeader == null || data.EnvironmentHeader.Length < 508) return false;
        var info = new InfoModel();
        System.Array.Copy(data.EnvironmentHeader, info.Header, 508);
        return info.WaterEnabled != 0;
    }

    /// <summary>Bilinear height lookup against the live procedural
    /// heightfield (Data.Heights) at a world (x,z) position. Public: also
    /// used by SC2CostsAutoGen's real navmesh generation, which needs the
    /// exact same terrain-height source Auto Path Node
    /// resolution above already uses, for consistency.</summary>
    public static float SampleTerrainHeight(SC2TerrainData data, float x, float z)
    {
        if (data.Heights == null || data.Heights.Length == 0 || data.CellSize <= 0f) return 0f;
        var stride = data.GridWidth + 1;
        var fx = Mathf.Clamp(x / data.CellSize, 0f, data.GridWidth);
        var fz = Mathf.Clamp(z / data.CellSize, 0f, data.GridHeight);
        var x0 = Mathf.FloorToInt(fx); var z0 = Mathf.FloorToInt(fz);
        var x1 = Mathf.Min(x0 + 1, data.GridWidth); var z1 = Mathf.Min(z0 + 1, data.GridHeight);
        var tx = fx - x0; var tz = fz - z0;
        float H(int gx, int gz) => data.Heights[gz * stride + gx];
        var h00 = H(x0, z0); var h10 = H(x1, z0); var h01 = H(x0, z1); var h11 = H(x1, z1);
        var hx0 = Mathf.Lerp(h00, h10, tx); var hx1 = Mathf.Lerp(h01, h11, tx);
        return Mathf.Lerp(hx0, hx1, tz);
    }

    // ---- mapobjs.bdf ----

    public static MapObjsModel BuildMapObjsModel(SC2TerrainData data, byte[] workingEnvironmentHeader = null)
    {
        data.EnsureObjectIds();
        var model = new MapObjsModel { FileVersion = 4 };

        foreach (var p in data.ObjProps)
        {
            model.Props.Add(new MapProp
            {
                Quat = new[] { p.Rotation.w, p.Rotation.x, p.Rotation.y, p.Rotation.z },
                Position = new[] { p.Position.x, p.Position.y, p.Position.z },
                Scale = p.Scale,
                BlueprintPath = p.BlueprintPath ?? "",
                Unknown9 = 1f,
                Flags = p.Flags
            });
        }

        // ARMY_* markers are start-position markers (Scenario.MasterChain
        // Markers + _scenario.lua StartPositions) — never part of
        // mapobjs.bdf's marker list (confirmed by a real-data survey).
        var indexById = new Dictionary<int, int>();
        foreach (var m in data.ObjMarkers)
        {
            if (ArmyPositionRe.IsMatch(m.Name)) continue;
            indexById[m.Id] = model.Markers.Count;
            model.Markers.Add(new MapMarker
            {
                Name = m.Name,
                AlwaysOne = 1f,
                Position = new[] { m.Position.x, m.Position.y, m.Position.z },
                MarkerType = ResolveEffectiveType(data, m, workingEnvironmentHeader)
            });
        }

        foreach (var g in data.ObjGraphs)
        {
            var graph = new MapGraph { Name = g.Name };
            foreach (var (a, b) in SC2ObjectGraphRefs.Enumerate(g))
            {
                if (!indexById.TryGetValue(a, out var ia)) continue;
                if (!indexById.TryGetValue(b, out var ib)) continue;
                graph.Entries.Add((ia, ib));
            }
            model.Graphs.Add(graph);
        }

        return model;
    }

    // ---- _save.lua ----

    // Key order (Props, Areas, MasterChain, Chains, Armies) and the presence
    // of Props confirmed against real vanilla _save.lua files (SC2_MP_001,
    // SC2_CA_C01, SC2_CA_C02) — the from-scratch skeleton was previously
    // missing Props entirely, which no real file omits.
    private const string MinimalSaveLuaSkeleton =
        "Scenario = {\r\n" +
        "    Props = {\r\n" +
        "    },\r\n" +
        "    Areas = {\r\n" +
        "    },\r\n" +
        "    MasterChain = {\r\n" +
        "        ['_MASTERCHAIN_'] = {\r\n" +
        "            Markers = {\r\n" +
        "            },\r\n" +
        "        },\r\n" +
        "    },\r\n" +
        "    Chains = {\r\n" +
        "    },\r\n" +
        "    Armies = {\r\n" +
        "    },\r\n" +
        "}\r\n";

    /// <param name="originalTextOverride">When non-empty, used in place of
    /// data.ObjSaveLuaTextOriginal — lets the
    /// exporter pass SC2MapDocument.Objects.SaveLuaTextOriginal instead
    /// (kept in lockstep with this same field by SC2ObjectsShell.
    /// EnsureObjectsDocumentSynced, safe to trust because nothing mutates
    /// ObjSaveLuaTextOriginal after import — see that field's own call
    /// sites). Defaults to null so every other caller keeps reading
    /// data.ObjSaveLuaTextOriginal exactly as before.</param>
    public static string RegenerateSaveLua(SC2TerrainData data, string originalTextOverride = null, byte[] workingEnvironmentHeader = null)
    {
        data.EnsureObjectIds();
        var original = string.IsNullOrEmpty(originalTextOverride) ? data.ObjSaveLuaTextOriginal : originalTextOverride;
        var text = string.IsNullOrEmpty(original) ? MinimalSaveLuaSkeleton : original;

        text = RegenerateMarkersBlock(text, data, workingEnvironmentHeader);
        text = RegenerateAreasBlock(text, data);
        text = RegenerateChainsBlock(text, data);
        text = RegenerateArmiesBlock(text, data);
        return text;
    }

    private static string RegenerateMarkersBlock(string text, SC2TerrainData data, byte[] workingEnvironmentHeader = null)
    {
        var (open, close) = SC2LuaBlocks.FindBlock(text, "Markers = ");
        if (open < 0) return text; // no Markers block in this file — leave untouched

        var sb = new StringBuilder();
        sb.Append("\r\n");
        foreach (var m in data.ObjMarkers)
            WriteMarkerEntry(sb, data, m, workingEnvironmentHeader);
        sb.Append("            ");

        return SC2LuaBlocks.ReplaceBlockContent(text, open, close, sb.ToString());
    }

    private static void WriteMarkerEntry(StringBuilder sb, SC2TerrainData data, SC2ObjMarker m, byte[] workingEnvironmentHeader = null)
    {
        sb.Append("                ['").Append(m.Name).Append("'] = {\r\n");
        void Field(string key, string value) => sb.Append("                    ['").Append(key).Append("'] = ").Append(value).Append(",\r\n");

        Field("type", $"STRING( '{ResolveEffectiveType(data, m, workingEnvironmentHeader)}' )");
        if (!string.IsNullOrEmpty(m.Prop)) Field("prop", $"STRING( '{m.Prop}' )");
        if (!string.IsNullOrEmpty(m.ColorHex)) Field("color", $"STRING( '{m.ColorHex}' )");

        if (m.OrientationIsQuaternion)
        {
            var q = Quaternion.Euler(m.OrientationEuler);
            Field("orientation", $"QUATERNION( {Fmt(q.w)}, {Fmt(q.x)}, {Fmt(q.y)}, {Fmt(q.z)} )");
        }
        else
        {
            Field("orientation", $"VECTOR3( {Fmt(m.OrientationEuler.x)}, {Fmt(m.OrientationEuler.y)}, {Fmt(m.OrientationEuler.z)} )");
        }

        Field("position", $"VECTOR3( {Fmt(m.Position.x)}, {Fmt(m.Position.y)}, {Fmt(m.Position.z)} )");

        // No 'adjacentTo' field here — confirmed absent from all 56 sampled
        // real _save.lua files. Real path-graph connectivity lives only in
        // mapobjs.bdf's binary Graphs table (SC2ObjectGraphRefs/BuildMapObjsModel
        // above), never duplicated into save.lua text.

        foreach (var f in m.ExtraFields)
            Field(f.Key, f.RawValue);

        sb.Append("                },\r\n");
    }

    private static string RegenerateAreasBlock(string text, SC2TerrainData data)
    {
        var (open, close) = SC2LuaBlocks.FindBlock(text, "Areas = ");
        if (open < 0) return text;

        var sb = new StringBuilder();
        sb.Append("\r\n");
        foreach (var z in data.ObjZones)
        {
            sb.Append("        ['").Append(z.Name).Append("'] = {\r\n");
            sb.Append("            ['rectangle'] = RECTANGLE( ")
              .Append(Fmt(z.X1)).Append(", ").Append(Fmt(z.Z1)).Append(", ")
              .Append(Fmt(z.X2)).Append(", ").Append(Fmt(z.Z2)).Append(" ),\r\n");
            sb.Append("        },\r\n");
        }
        sb.Append("    ");
        return SC2LuaBlocks.ReplaceBlockContent(text, open, close, sb.ToString());
    }

    private static string RegenerateChainsBlock(string text, SC2TerrainData data)
    {
        var (open, close) = SC2LuaBlocks.FindBlock(text, "Chains = ");
        if (open < 0) return text;

        var sb = new StringBuilder();
        sb.Append("\r\n");
        foreach (var c in data.ObjChains)
        {
            sb.Append("        ['").Append(c.Name).Append("'] = {\r\n");
            sb.Append("            Markers = {\r\n");
            foreach (var name in c.MarkerNames)
                sb.Append("                \"").Append(name).Append("\",\r\n");
            sb.Append("            },\r\n");
            sb.Append("        },\r\n");
        }
        sb.Append("    ");
        return SC2LuaBlocks.ReplaceBlockContent(text, open, close, sb.ToString());
    }

    /// <summary>Real vanilla _save.lua files always define an Armies-table
    /// entry for EXACTLY the set of ARMY_* position markers actually placed
    /// on the map (same source StartPositions/scenario.lua's
    /// Configurations.armies already use — confirmed all three must agree,
    /// via a corpus diff against real D1/CA files), plus one extra
    /// 'ARMY_EXTRA' entry (referenced by scenario.lua's
    /// customprops.ExtraArmies, present in every sampled real file).
    /// data.ObjArmies is a separate metadata list a user may or may not
    /// have populated for a given army marker — when it's missing, a
    /// default-shaped entry (matching every real file's own default:
    /// personality/plans blank, color=1, faction=0, zero Economy, no
    /// Alliances, no Units) is generated automatically instead of silently
    /// omitting the army, so save.lua's Armies table can never drift out of
    /// sync with the markers/scenario.lua that reference it.</summary>
    private static string RegenerateArmiesBlock(string text, SC2TerrainData data)
    {
        var (open, close) = SC2LuaBlocks.FindBlock(text, "Armies = ");
        if (open < 0) return text;

        var armyNames = new List<string>();
        foreach (var m in data.ObjMarkers)
            if (ArmyPositionRe.IsMatch(m.Name)) armyNames.Add(m.Name);
        armyNames.Add("ARMY_EXTRA");

        var sb = new StringBuilder();
        sb.Append("\r\n");
        foreach (var name in armyNames)
            WriteArmyEntry(sb, data, name, data.ObjArmies.Find(a => a.Name == name));
        sb.Append("    ");
        return SC2LuaBlocks.ReplaceBlockContent(text, open, close, sb.ToString());
    }

    private static void WriteArmyEntry(StringBuilder sb, SC2TerrainData data, string name, SC2ObjArmy a)
    {
        // Field-by-field bracket/wrapper style below is copied exactly
        // from two real vanilla _save.lua files (SC2_CA_C01, SC2_MP_001)
        // — personality/plans/faction/mass/energy/Alliance-stance/unit-
        // type are all BARE keys with bare (unwrapped) Lua literals,
        // while ['color']/['Units']/['Position']/['Orientation'] are
        // bracketed-and-quoted despite living right next to bare keys in
        // the same entry. This isn't a stylistic choice on our part —
        // it's exactly what the game's own serializer produced, and the
        // importer (SC2ObjectsImport) matches both forms per-key so
        // whichever style a given file actually uses still parses.
        sb.Append("        ['").Append(name).Append("'] = {\r\n");
        sb.Append("            personality = '").Append(a?.Personality ?? "").Append("',\r\n");
        sb.Append("            plans = '").Append(a?.Plans ?? "").Append("',\r\n");
        sb.Append("            ['color'] = FLOAT( ").Append(Fmt(a?.Color ?? 1f)).Append(" ),\r\n");
        sb.Append("            faction = ").Append(a?.Faction ?? 0).Append(",\r\n");
        sb.Append("            Economy = {\r\n");
        sb.Append("                mass = ").Append(Fmt(a?.Mass ?? 0f)).Append(",\r\n");
        sb.Append("                energy = ").Append(Fmt(a?.Energy ?? 0f)).Append(",\r\n");
        sb.Append("            },\r\n");

        sb.Append("            Alliances = {\r\n");
        if (a != null)
            foreach (var al in a.Alliances)
                sb.Append("                ['").Append(al.OtherArmy).Append("'] = '").Append(al.Stance).Append("',\r\n");
        sb.Append("            },\r\n");

        sb.Append("            ['Units'] = GROUP {\r\n");
        sb.Append("                Units = {\r\n");
        WriteUnitsForArmy(sb, data, name, -1, 5);
        sb.Append("                },\r\n");
        sb.Append("            },\r\n");

        sb.Append("        },\r\n");
    }

    private static void WriteUnitsForArmy(StringBuilder sb, SC2TerrainData data, string armyName, int parentIndex, int depth)
    {
        var indent = new string(' ', depth * 4);
        for (var i = 0; i < data.ObjUnits.Count; i++)
        {
            var u = data.ObjUnits[i];
            if (u.ArmyName != armyName || u.ParentIndex != parentIndex) continue;

            if (u.IsGroup)
            {
                // Nested groups reuse the SAME bare 'Units' sub-key at every
                // depth (confirmed against real files) — there is no
                // separate "Interior" wrapper.
                sb.Append(indent).Append("['").Append(u.Name).Append("'] = GROUP {\r\n");
                if (u.IsWreckage)
                    sb.Append(indent).Append("    wreckage = true,\r\n");
                sb.Append(indent).Append("    Units = {\r\n");
                WriteUnitsForArmy(sb, data, armyName, i, depth + 2);
                sb.Append(indent).Append("    },\r\n");
                sb.Append(indent).Append("},\r\n");
            }
            else
            {
                sb.Append(indent).Append("['").Append(u.Name).Append("'] = {\r\n");
                sb.Append(indent).Append("    type = '").Append(u.Type).Append("',\r\n");
                sb.Append(indent).Append("    ['Position'] = VECTOR3( ")
                  .Append(Fmt(u.Position.x)).Append(", ").Append(Fmt(u.Position.y)).Append(", ").Append(Fmt(u.Position.z)).Append(" ),\r\n");
                sb.Append(indent).Append("    ['Orientation'] = VECTOR3( ")
                  .Append(Fmt(u.OrientationEuler.x)).Append(", ").Append(Fmt(u.OrientationEuler.y)).Append(", ").Append(Fmt(u.OrientationEuler.z)).Append(" ),\r\n");
                sb.Append(indent).Append("},\r\n");
            }
        }
    }

    // ---- _scenario.lua ----

    // Real vanilla _scenario.lua files are never wrapped in a top-level
    // "Scenario = { ... }" table (that's _save.lua's shape) - they're
    // "version = 3" followed directly by a top-level "ScenarioInfo = { ... }"
    // table, with StartPositions nested inside it.
    //
    // Field set confirmed by diffing all 10 real D1 (multiplayer/skirmish)
    // scenario.lua files against all 20 real CA (campaign) ones:
    //  - Skirmish-only (100% of D1, 0% of CA): devname, x360_name,
    //    x360_maptype, StartPositions.
    //  - Campaign-only (100% of CA, 0% of D1): campaignID (faction name),
    //    UIDeactivate = true.
    //  - Shared by both, but with different real values: type ('skirmish' vs
    //    'campaign'), norushradius (always a real >0 value for D1, always
    //    0.000000 for CA - norush is a skirmish-only mechanic), customprops
    //    (['ExtraArmies']=STRING('ARMY_EXTRA') for D1, empty {} for CA).
    //  - x360_name/x360_maptype encode player-count/format info
    //    ("[4] ... (2v2)") the editor has no way to know a priori -
    //    deliberately NOT generated here (would be invented content, not a
    //    real default) even though they're present in every D1 sample; the
    //    map author fills them in via the KnownFields UI.
    //  - mappack=4 appears in every D1 sample here but is ABSENT from the
    //    base-game SC2_MP_001 file - it's DLC-pack metadata, not a genuine
    //    multiplayer requirement, so also omitted.
    //  - name/devname ARE generated (unlike the two above): every sampled
    //    file (D1 and CA alike) has a real `name`, and CA's own value is
    //    never a fancy localized string - it's just the map's own name
    //    (`name = 'SC2_CA_C01'`), the exact same convention already used
    //    for `description`/`reverbPreset` here. Defaulting name/devname to
    //    mapName follows that real, observed convention rather than
    //    inventing the elaborate "[4] DisplayName (2v2)"/"D1_001: 2v2 LAND"
    //    classification text D1 maps use, which the editor has no way to
    //    derive (same reasoning that excludes x360_name above).
    // The editor only ever creates skirmish maps by default (per the above),
    // so this skeleton is skirmish-shaped; campaign maps are always imported
    // (OriginalPassthrough), never generated from scratch.
    private static string BuildMinimalScenarioLuaSkeleton(SC2TerrainData data, string sanitizedMapName)
    {
        // devname/description/reverbPreset/map/save/script below must use
        // the SAME filesystem-safe name the
        // real export actually writes terrain.bdf/_save.lua/_scenario.lua
        // under (SC2TerrainExporter.SanitizeMapName — spaces and other
        // invalid characters replaced with '_'), not the raw, possibly-
        // spaced display name — otherwise the map/save/script path strings
        // point at a folder ('/maps/New Map111/...') that never actually
        // gets created (the real folder is 'New_Map111'), and the game
        // can't find its own scenario/save/script files.
        var mapName = string.IsNullOrEmpty(sanitizedMapName) ? "NewMap" : sanitizedMapName;
        // `name` is the map's real in-game display name (SC2TerrainData.
        // DisplayName — genuinely separate from mapName, see that field's
        // own doc comment) — this one is fine to keep the raw, human-
        // readable text, since it's never used as a file/folder path.
        var displayName = string.IsNullOrEmpty(data.DisplayName) ? mapName : data.DisplayName;
        var sizeX = Mathf.Max(1, Mathf.RoundToInt(data.GridWidth * data.CellSize));
        var sizeY = Mathf.Max(1, Mathf.RoundToInt(data.GridHeight * data.CellSize));
        return
            "version = 3\r\n" +
            "ScenarioInfo = {\r\n" +
            $"    devname = '{mapName}',\r\n" +
            $"    name = '{displayName}',\r\n" +
            $"    description = '{mapName}',\r\n" +
            "    type = 'skirmish',\r\n" +
            "    starts = true,\r\n" +
            "    preview = '',\r\n" +
            $"    reverbPreset = '{mapName}',\r\n" +
            $"    size = {{{sizeX}, {sizeY}}},\r\n" +
            $"    map = '/maps/{mapName}/{mapName}.scmap',\r\n" +
            $"    save = '/maps/{mapName}/{mapName}_save.lua',\r\n" +
            $"    script = '/maps/{mapName}/{mapName}_script.lua',\r\n" +
            "    norushradius = 0.000000,\r\n" +
            "    StartPositions = {\r\n" +
            "    },\r\n" +
            "    Configurations = {\r\n" +
            "        ['standard'] = {\r\n" +
            "            teams = {\r\n" +
            "                { name = 'FFA', armies = {} },\r\n" +
            "            },\r\n" +
            "            customprops = {\r\n" +
            "                ['ExtraArmies'] = STRING( 'ARMY_EXTRA' ),\r\n" +
            "            },\r\n" +
            "        },\r\n" +
            "    },\r\n" +
            "}\r\n";
    }

    /// <param name="sanitizedMapName">The same filesystem-safe map name the
    /// real export writes terrain.bdf/_save.lua/etc. under
    /// (SC2TerrainExporter.SanitizeMapName's output) — used for the
    /// generated skeleton's devname/description/reverbPreset/map/save/
    /// script fields so they actually match where the files really live.
    /// Not used at all when patching an EXISTING imported scenario.lua
    /// (those keep their own real, already-correct paths verbatim). Optional
    /// — falls back to data.MapName as-is (the old, pre-fix behavior) for
    /// callers that don't have a sanitized name handy (e.g. the
    /// characterization tests, which only exercise the skeleton's
    /// structure, not real filesystem-safety).</param>
    /// <param name="originalTextOverride">See RegenerateSaveLua's matching
    /// parameter doc comment — same reasoning,
    /// SC2MapDocument.Objects.ScenarioLuaTextOriginal in place of
    /// data.ObjScenarioLuaTextOriginal.</param>
    public static string RegenerateScenarioLua(SC2TerrainData data, string sanitizedMapName = null, string originalTextOverride = null)
    {
        data.EnsureObjectIds();
        var original = string.IsNullOrEmpty(originalTextOverride) ? data.ObjScenarioLuaTextOriginal : originalTextOverride;
        var text = string.IsNullOrEmpty(original)
            ? BuildMinimalScenarioLuaSkeleton(data, string.IsNullOrEmpty(sanitizedMapName) ? data.MapName : sanitizedMapName)
            : original;

        // Patch the real display name in for an IMPORTED map too —
        // BuildMinimalScenarioLuaSkeleton already bakes it in for a
        // from-scratch map, but until this patch existed, editing Display
        // Name on an already-imported map (the `original` passthrough
        // branch above) never reached the exported file at all — only
        // StartPositions/armies below were ever patched into `original`.
        var displayName = string.IsNullOrEmpty(data.DisplayName)
            ? (string.IsNullOrEmpty(data.MapName) ? "NewMap" : data.MapName)
            : data.DisplayName;
        text = ScenarioNameFieldRe.Replace(text, m => m.Groups[1].Value + "name" + m.Groups[2].Value + "'" + displayName + "'", 1);

        // Same source list (ARMY_* position markers) feeds both
        // StartPositions and Configurations.standard.teams[0].armies below -
        // real files always list every army in one FFA team, even for
        // 4v4/8v8 maps (confirmed: no sample splits armies across multiple
        // teams), so both blocks stay in lockstep with data.ObjMarkers
        // automatically rather than needing separate manual upkeep.
        var armyNames = new List<string>();
        var (spOpen, spClose) = SC2LuaBlocks.FindBlock(text, "StartPositions = ");
        if (spOpen >= 0)
        {
            var sb = new StringBuilder();
            sb.Append("\r\n");
            foreach (var m in data.ObjMarkers)
            {
                if (!ArmyPositionRe.IsMatch(m.Name)) continue;
                armyNames.Add(m.Name);
                sb.Append("        ['").Append(m.Name).Append("'] = { ")
                  .Append(Fmt(m.Position.x)).Append(", ").Append(Fmt(m.Position.y)).Append(", ").Append(Fmt(m.Position.z))
                  .Append(" },\r\n");
            }
            sb.Append("    ");
            text = SC2LuaBlocks.ReplaceBlockContent(text, spOpen, spClose, sb.ToString());
        }
        else
        {
            // Campaign maps have no StartPositions block at all (confirmed:
            // 0/20 real CA _scenario.lua files have one) - still need the
            // army name list for the armies patch below.
            foreach (var m in data.ObjMarkers)
                if (ArmyPositionRe.IsMatch(m.Name)) armyNames.Add(m.Name);
        }

        var (arOpen, arClose) = SC2LuaBlocks.FindBlock(text, "armies = ");
        if (arOpen >= 0)
        {
            var armiesSb = new StringBuilder();
            foreach (var name in armyNames)
                armiesSb.Append('\'').Append(name).Append("',");
            text = SC2LuaBlocks.ReplaceBlockContent(text, arOpen, arClose, armiesSb.ToString());
        }

        return text;
    }

    /// <summary>_script.lua — confirmed against a real vanilla file
    /// (user-supplied, SC2_D3_Eclipse) to be almost entirely fixed engine
    /// boilerplate (camera defaults, music cue, ScenarioTesting debug
    /// hotkeys): the ONLY per-map-varying line is
    /// ScenarioFramework.SetPlayableArea('AREA_01'). Unlike RegenerateSaveLua/
    /// RegenerateScenarioLua above, this always regenerates from the fixed
    /// skeleton rather than patching an "original" — a real vanilla
    /// _script.lua is compiled Lua bytecode (this editor never re-exports
    /// bytecode, see SC2ScenarioLegacyBridge's own doc comment) and there is
    /// no other real per-map content in this file to preserve.</summary>
    public static string RegenerateScriptLua(SC2TerrainData data)
    {
        var playableArea = ResolvePlayableAreaName(data);
        // Emitted verbatim (SetPlayableArea('')) regardless of whether a
        // real Area was ever assigned — an empty-string argument isn't a
        // real vanilla convention (every real vanilla _script.lua either
        // names a real Area or has no such call at all), so leave the
        // whole line out rather than calling it with nothing to clip to.
        var setPlayableAreaLine = string.IsNullOrEmpty(playableArea)
            ? ""
            : $"\timport('/lua/sim/ScenarioFramework.lua').SetPlayableArea('{playableArea}')\r\n";
        return
            "function OnPopulate()\r\n" +
            "\timport('/lua/sim/ScenarioUtilities.lua').InitializeArmies()\r\n" +
            "\tlocal camData = {\r\n" +
            "\t\tMinSpinPitch\t= 0.5,\t\t-- 0.1 = default\tThe min pitch resulting from a spin.\r\n" +
            "\t\tMaxZoomMult\t\t= 1.5,\t\t-- 1.1 = default\tExtra zoom out buffer so we can see the borders of the map clearly.\r\n" +
            "\t\tFarZoom\t\t\t= -1,\t\t-- -1 = default\t\tFarthest mouse can zoom out from terrain, measured in LOD units, -1 to ignore.\r\n" +
            "\t}\r\n" +
            "\timport('/lua/sim/SimCamera.lua').SimCamera('WorldCamera'):UpdateDefaults(camData)\r\n" +
            setPlayableAreaLine +
            "end\r\n" +
            "\r\n" +
            "function OnStart(self)\r\n" +
            "\tlocal strMusic = 'SC2/MUSIC/MP/Conditional_Music'\r\n" +
            "\tLOG('MP: Music: [', strMusic, ']')\r\n" +
            "\tSync.PlayMusic = strMusic\r\n" +
            "end\r\n" +
            "\r\n" +
            "function OnShiftF3()\r\n" +
            "\timport('/lua/sim/ScenarioFramework/ScenarioTesting.lua').GroupPicker_Increment()\r\n" +
            "end\r\n" +
            "\r\n" +
            "function OnCtrlF3()\r\n" +
            "\timport('/lua/sim/ScenarioFramework/ScenarioTesting.lua').GroupPicker_Decrement()\r\n" +
            "end\r\n" +
            "\r\n" +
            "function OnShiftF4()\r\n" +
            "\timport('/lua/sim/ScenarioFramework/ScenarioTesting.lua').TestPicker_Increment()\r\n" +
            "end\r\n" +
            "\r\n" +
            "function OnCtrlF4()\r\n" +
            "\timport('/lua/sim/ScenarioFramework/ScenarioTesting.lua').TestPicker_Decrement()\r\n" +
            "end\r\n" +
            "\r\n" +
            "function OnShiftF5()\r\n" +
            "\timport('/lua/sim/ScenarioFramework/ScenarioTesting.lua').ExecuteCurrentTest()\r\n" +
            "end\r\n";
    }

    private static string ResolvePlayableAreaName(SC2TerrainData data)
    {
        if (!string.IsNullOrEmpty(data.PlayableAreaName)) return data.PlayableAreaName;
        var zones = data.ObjZones;
        if (zones == null) return "";
        foreach (var z in zones)
            if (z.Role == "Playable") return z.Name;
        return "";
    }

    private static string Fmt(float v) => v.ToString("0.######", CultureInfo.InvariantCulture);
}
