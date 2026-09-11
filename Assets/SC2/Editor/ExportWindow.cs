// ExportWindow.cs
// Final export step: scene + SC2MapRawData -> 7x*.win.bdf +
// _save.lua/_scenario.lua/_script.lua -> .scd (zip).
//
//  - terrain.bdf  : SC2MapRawData.TerrainData/OffsetTable (raw, as at
//                    import time) + a material-only patch (TerrainBdf.PatchMaterials
//                    + SC2TerrainMaterialExporter) — geometry/BSP are not
//                    changed.
//  - mapobjs.bdf  : SC2SceneToMapObjs.Build(mapRoot) -> MapObjsBdf.Serialize
//                    (accounts for symmetry copies and deletions).
//  - info.bdf     : SC2Environment -> InfoBdf.Serialize.
//  - hfield/costs : SC2MapRawData.HfieldData/CostsData (updated by
//                    the Costs paint tools) — passthrough Write.
//  - collision2/decals : SC2MapRawData, pure passthrough.
//  - *.lua        : SC2SaveLuaWriter/SC2ScenarioLuaWriter regenerate the
//                    Markers block (_save.lua) and StartPositions
//                    block (_scenario.lua) from the scene (including
//                    lua-only markers like ARMY_N/Mass/... and symmetry
//                    copies); everything else stays as at import time.
//                    _script.lua is left unchanged.

#if UNITY_EDITOR
using System.IO;
using System.Linq;
using System.IO.Compression;
using UnityEditor;
using UnityEngine;
using BdfIO.Core;
using BdfIO.Formats.Terrain;
using BdfIO.Formats.MapObjs;
using BdfIO.Formats.Info;

public class ExportWindow : EditorWindow
{
    [MenuItem("SC2/Export .scd...")]
    public static void ShowWindow() => GetWindow<ExportWindow>("SC2 Map Export");

    private SC2MapRawData? _data;
    private GameObject? _mapRoot;
    private string _outputDir = "";
    private bool _exportAsFolder; // SC2 EnableDiskWatch: a <map>.scd folder instead of a zip archive

    private void OnGUI()
    {
        _data = (SC2MapRawData)EditorGUILayout.ObjectField("SC2MapRawData", _data, typeof(SC2MapRawData), false);
        _mapRoot = (GameObject)EditorGUILayout.ObjectField("Map root (scene)", _mapRoot, typeof(GameObject), true);

        EditorGUILayout.LabelField("Output folder", _outputDir);
        if (GUILayout.Button("Choose folder..."))
        {
            var picked = EditorUtility.OpenFolderPanel("Folder for the .scd", "", "");
            if (!string.IsNullOrEmpty(picked)) _outputDir = picked;
        }

        EditorGUILayout.Space();
        _exportAsFolder = EditorGUILayout.ToggleLeft(
            "Export as a folder (no zip) — for EnableDiskWatch",
            _exportAsFolder);
        if (_exportAsFolder)
            EditorGUILayout.HelpBox(
                "The map's files will be copied into a \"<name>.scd\" folder instead of " +
                "packing them into a zip archive. With EnableDiskWatch enabled in SC2, " +
                "this lets you update the map on the fly and check changes " +
                "without restarting the game.",
                MessageType.None);

        EditorGUI.BeginDisabledGroup(_data == null || _mapRoot == null || string.IsNullOrEmpty(_outputDir));
        if (GUILayout.Button("Export .scd"))
        {
            try { Export(_data!, _mapRoot!, _outputDir, _exportAsFolder); }
            catch (System.IO.IOException ex)
            {
                EditorUtility.DisplayDialog("Export failed", ex.Message, "OK");
            }
        }
        EditorGUI.EndDisabledGroup();

        EditorGUILayout.Space();
        EditorGUI.BeginDisabledGroup(_data == null || _mapRoot == null || string.IsNullOrEmpty(_outputDir));
        if (GUILayout.Button("⚡ Update only info.bdf in the .scd folder"))
        {
            try { ExportInfoOnly(_data!, _mapRoot!, _outputDir); }
            catch (System.IO.IOException ex)
            {
                EditorUtility.DisplayDialog("Failed to write info.bdf",
                    ex.Message, "OK");
            }
        }
        EditorGUI.EndDisabledGroup();
        EditorGUILayout.HelpBox(
            "Overwrites ONLY info.bdf inside an already-existing " +
            "\"<name>.scd\" folder (created by \"export as a folder\" above) — a quick " +
            "way to check lighting/water/fog changes through EnableDiskWatch " +
            "without a full re-export of the whole map. Requires the " +
            "\"<name>.scd\" folder to already exist.\n\n" +
            "⚠ Data source: if the map has ever been saved through " +
            "the Environment tab's editor, ALL " +
            "fields come from there, and the scene's SC2Environment component is ignored. " +
            "Otherwise SC2Environment is used, but only SunDirection/SunColor/" +
            "SunIntensity, WaterElevation/AirElevation and the texture paths; " +
            "ShadowColor/fog/detailed water/glow/gaussian/intel/range " +
            "circles/DoF stay as they were at import time. To change those " +
            "fields, use the Environment tab's editor, not the component's Inspector.",
            MessageType.None);

        EditorGUILayout.Space();
        EditorGUILayout.HelpBox(
            "_save.lua: the Markers block is regenerated from the scene (including " +
            "ARMY_N/Mass/... and symmetry copies). _scenario.lua: the " +
            "StartPositions block comes from ARMY_N. Everything else is copied as at " +
            "import time. _script.lua is left unchanged.",
            MessageType.Info);
    }

    private static void Export(SC2MapRawData data, GameObject mapRoot, string outputDir, bool asFolder = false)
    {
        var mapName = string.IsNullOrEmpty(data.MapName) ? mapRoot.name : data.MapName;
        var tmpRoot = Path.Combine(Path.GetTempPath(), $"sc2_export_{mapName}");
        if (Directory.Exists(tmpRoot)) Directory.Delete(tmpRoot, true);

        // Parse the paths from scenario.lua for the exact file names
        // map = '/maps/SC2_D3_crab/SC2_D3_crab.scmap' -> file name SC2_D3_crab.scmap -> terrain.win.bdf
        var mapStem   = string.IsNullOrEmpty(data.ScenarioMapPath)
            ? mapName
            : System.IO.Path.GetFileNameWithoutExtension(data.ScenarioMapPath); // SC2_D3_crab
        var saveName  = string.IsNullOrEmpty(data.ScenarioSavePath)
            ? $"{mapName}_save.lua"
            : System.IO.Path.GetFileName(data.ScenarioSavePath);  // SC2_D3_crab_save.lua
        var scriptName = string.IsNullOrEmpty(data.ScenarioScriptPath)
            ? $"{mapName}_script.lua"
            : System.IO.Path.GetFileName(data.ScenarioScriptPath); // SC2_D3_crab_script.lua

        // Archive layout: maps/<mapStem>/ + Textures/ + uncompiled/maps/<mapStem>/
        var mapsDir     = Path.Combine(tmpRoot, "maps", mapStem);
        var lightingDir = Path.Combine(mapsDir, "lighting");
        var texturesDir = Path.Combine(tmpRoot, "Textures");
        var uncompDir   = Path.Combine(tmpRoot, "uncompiled", "maps", mapStem);

        Directory.CreateDirectory(mapsDir);
        Directory.CreateDirectory(lightingDir);
        Directory.CreateDirectory(texturesDir);
        Directory.CreateDirectory(uncompDir);

        // ---- terrain.bdf ----
        var terrainContainer = new BdfContainer
        {
            Platform = data.Platform,
            Data = data.GetTerrainData(),
            OffsetTable = data.GetTerrainOt(),
            NumFiles = data.TerrainNumFiles,
            TrailingData = data.TerrainTrailingData.Length > 0 ? data.TerrainTrailingData : null
        };
        var terrainGo = FindTerrainGameObject(mapRoot.transform);
        var terrainExportContainer = terrainGo != null
            ? TerrainBdf.PatchMaterials(terrainContainer,
                materials => SC2TerrainMaterialExporter.ApplyFromScene(materials,
                    terrainGo.GetComponent<MeshRenderer>().sharedMaterials))
            : terrainContainer; // no patch — use the original container (with its TrailingData)
        // terrain.bdf is named terrain.win.bdf (like every other .bdf)
        // .scmap in scenario.lua is just a reference to the map as a whole, not a file name
        WriteBdf(mapsDir, mapStem, "terrain", terrainExportContainer, TerrainBdf.Padding);

        // ---- mapobjs.bdf ----
        var sceneObjs = SC2SceneToMapObjs.Build(mapRoot.transform);
        var mapObjsModel = sceneObjs.Model;
        var (mapObjsData, mapObjsOt) = MapObjsBdf.Serialize(mapObjsModel, data.Platform);
        WriteBdf(mapsDir, mapStem, "mapobjs", new BdfContainer
        { Platform = data.Platform, Data = mapObjsData, OffsetTable = mapObjsOt, NumFiles = 1 }, MapObjsBdf.Padding);

        // ---- info.bdf: prefer info.bytes (edited through the Environment editor) ----
        var infoData2 = data.GetInfoData();
        if (infoData2.Length > 0)
        {
            WriteBdf(mapsDir, mapStem, "info", new BdfContainer
                { Platform=data.Platform, Data=infoData2, OffsetTable=data.GetInfoOt(), NumFiles=1 }, InfoBdf.Padding);
        }
        else
        {
            var env = mapRoot.GetComponentInChildren<SC2Environment>();
            if (env != null)
            {
                var infoModel = BuildInfoModel(env);
                var (infoBytes, infoOt) = InfoBdf.Serialize(infoModel, data.Platform);
                WriteBdf(mapsDir, mapStem, "info", new BdfContainer
                    { Platform=data.Platform, Data=infoBytes, OffsetTable=infoOt, NumFiles=1 }, InfoBdf.Padding);
            }
        }

        WriteBdf(mapsDir, mapStem, "hfield",     new BdfContainer { Platform=data.Platform, Data=data.GetHfieldData(),     OffsetTable=data.GetHfieldOt(),     NumFiles=1                   }, BdfIO.Formats.Hfield.HfieldBdf.Padding);
        WriteBdf(mapsDir, mapStem, "costs",      new BdfContainer { Platform=data.Platform, Data=data.GetCostsData(),      OffsetTable=data.GetCostsOt(),      NumFiles=1                   }, BdfIO.Formats.Costs.CostsBdf.Padding);
        WriteBdf(mapsDir, mapStem, "collision2", new BdfContainer { Platform=data.Platform, Data=data.GetCollision2Data(), OffsetTable=data.GetCollision2Ot(), NumFiles=1                   }, BdfIO.Formats.Collision2.Collision2Bdf.Padding);
        WriteBdf(mapsDir, mapStem, "decals",     new BdfContainer { Platform=data.Platform, Data=data.GetDecalsData(),     OffsetTable=data.GetDecalsOt(),     NumFiles=data.DecalsNumFiles }, BdfIO.Formats.Decals.DecalsBdf.Padding);

        // ---- lua: names taken from scenario.lua ----
        var newSaveLua = SC2SaveLuaWriter.Regenerate(data.GetSaveLua(), mapObjsModel,
            sceneObjs.MarkerComponents, sceneObjs.LuaOnlyMarkers);
        var armyMarkers = sceneObjs.LuaOnlyMarkers.Where(m => m.MarkerName.StartsWith("ARMY_"));
        var newScenarioLua = SC2ScenarioLuaWriter.Regenerate(data.ScenarioLua, armyMarkers);

        WriteTextIfNotEmpty(mapsDir, saveName,             newSaveLua);
        WriteTextIfNotEmpty(mapsDir, scriptName,           data.ScriptLua);
        WriteTextIfNotEmpty(mapsDir, $"{mapStem}_scenario.lua", newScenarioLua);

        WriteTextIfNotEmpty(uncompDir, saveName,           newSaveLua);
        WriteTextIfNotEmpty(uncompDir, $"{mapStem}_scenario.lua", newScenarioLua);

        // ---- Textures ----
        CopyTexturesForExport(mapStem, texturesDir, lightingDir);

        // ---- packaging: .scd (zip) or a <map>.scd folder (for EnableDiskWatch) ----
        if (asFolder)
        {
            var scdDir = Path.Combine(outputDir, $"{mapName}.scd");
            if (Directory.Exists(scdDir)) Directory.Delete(scdDir, true);
            CopyDirectoryRecursive(tmpRoot, scdDir);

            Debug.Log($"[Export] '{mapName}' -> {scdDir} (folder, EnableDiskWatch)");
            EditorUtility.RevealInFinder(scdDir);
        }
        else
        {
            var scdPath = Path.Combine(outputDir, $"{mapName}.scd");
            if (File.Exists(scdPath)) File.Delete(scdPath);
            ZipFile.CreateFromDirectory(tmpRoot, scdPath, System.IO.Compression.CompressionLevel.Optimal, includeBaseDirectory: false);

            Debug.Log($"[Export] '{mapName}' -> {scdPath}");
            EditorUtility.RevealInFinder(scdPath);
        }
    }

    /// <summary>Recursively copies a directory's contents (for the "folder .scd" mode).</summary>
    private static void CopyDirectoryRecursive(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var filePath in Directory.GetFiles(sourceDir))
        {
            var destFile = Path.Combine(destDir, Path.GetFileName(filePath));
            File.Copy(filePath, destFile, overwrite: true);
        }
        foreach (var dirPath in Directory.GetDirectories(sourceDir))
        {
            var destSubDir = Path.Combine(destDir, Path.GetFileName(dirPath));
            CopyDirectoryRecursive(dirPath, destSubDir);
        }
    }

    /// <summary>
    /// Quick, targeted rewrite of only info.bdf inside an already-existing
    /// "&lt;name&gt;.scd" folder (created via "Export as a folder").
    /// Does not touch terrain/mapobjs/hfield/costs/lua etc. — only needed
    /// for quickly checking lighting/water/fog changes through
    /// EnableDiskWatch without a full re-export of the map.
    /// </summary>
    private static void ExportInfoOnly(SC2MapRawData data, GameObject mapRoot, string outputDir)
    {
        var mapName = string.IsNullOrEmpty(data.MapName) ? mapRoot.name : data.MapName;
        var mapStem = string.IsNullOrEmpty(data.ScenarioMapPath)
            ? mapName
            : System.IO.Path.GetFileNameWithoutExtension(data.ScenarioMapPath);

        var scdDir  = Path.Combine(outputDir, $"{mapName}.scd");
        var mapsDir = Path.Combine(scdDir, "maps", mapStem);

        if (!Directory.Exists(scdDir))
        {
            EditorUtility.DisplayDialog("Folder not found",
                $"\"{scdDir}\" does not exist.\n\nRun a full export " +
                "with \"Export as a folder\" enabled first, then use " +
                "this button for quick info.bdf updates.", "OK");
            return;
        }
        Directory.CreateDirectory(mapsDir); // in case the folder structure is missing

        // Same priority logic as the full Export: prefer info.bytes
        // (edited through the Environment editor), otherwise fall back to the scene's SC2Environment.
        var infoData = data.GetInfoData();
        if (infoData.Length > 0)
        {
            // Diagnostics: show the real values from the SOURCE (data.InfoBytes)
            // that's about to be written — so it's immediately clear whether
            // an edit from the Environment editor's Save() made it to this point.
            var diagContainer = new BdfContainer
                { Platform = data.Platform, Data = infoData, OffsetTable = data.GetInfoOt(), NumFiles = 1 };
            var diagModel = InfoBdf.Parse(diagContainer);
            Debug.Log($"[Export] Source: data.InfoBytes (edited in the Environment editor). " +
                $"SunDirection=({diagModel.SunDirectionX:F4},{diagModel.SunDirectionY:F4},{diagModel.SunDirectionZ:F4})");

            WriteBdf(mapsDir, mapStem, "info", new BdfContainer
                { Platform = data.Platform, Data = infoData, OffsetTable = data.GetInfoOt(), NumFiles = 1 }, InfoBdf.Padding);
        }
        else
        {
            var env = mapRoot.GetComponentInChildren<SC2Environment>();
            if (env == null)
            {
                EditorUtility.DisplayDialog("No info.bdf data",
                    "Found neither info.bytes nor a SC2Environment in the scene — nothing to export.", "OK");
                return;
            }
            var infoModel = BuildInfoModel(env);
            Debug.Log($"[Export] Source: the scene's SC2Environment (data.InfoBytes is empty). " +
                $"SunDirection=({infoModel.SunDirectionX:F4},{infoModel.SunDirectionY:F4},{infoModel.SunDirectionZ:F4})");

            var (infoBytes, infoOt) = InfoBdf.Serialize(infoModel, data.Platform);
            WriteBdf(mapsDir, mapStem, "info", new BdfContainer
                { Platform = data.Platform, Data = infoBytes, OffsetTable = infoOt, NumFiles = 1 }, InfoBdf.Padding);
        }

        var infoPath = Path.Combine(mapsDir, $"{mapStem}.info.win.bdf");
        Debug.Log($"[Export] info.bdf updated -> {infoPath}");
    }

    /// <summary>
    /// Builds an InfoModel from SC2Environment. IMPORTANT: env.Header is a
    /// frozen copy from import time (508 bytes); it is NOT updated
    /// automatically when the component's typed C# fields (SunColor,
    /// WaterElevation, etc.) change through the Inspector. So the typed
    /// fields here are EXPLICITLY written back into the Header copy at
    /// their known offsets — otherwise, exporting without InfoBytes (i.e.
    /// without first saving through the Environment editor) would silently
    /// lose any changes made through the component's Inspector.
    /// Fields with no typed property on SC2Environment (detailed water,
    /// fog, glow, gaussian, intel, range circles, DoF — see the full list
    /// in InfoModel) stay as they were in the original Header: editing them
    /// requires the Environment editor, which saves to data.InfoBytes (which
    /// takes priority over this path — see the calling code).
    /// </summary>
    private static InfoModel BuildInfoModel(SC2Environment env)
    {
        var model = new InfoModel { Platform = env.Platform };
        System.Array.Copy(env.Header, model.Header, 508);

        model.SunDirection = (env.SunDirection.x, env.SunDirection.y, env.SunDirection.z);
        model.SunColor = (env.SunColor.r, env.SunColor.g, env.SunColor.b);
        model.SunIntensity = env.SunIntensity;
        model.WaterElevation = env.WaterElevation;
        model.AirElevation = env.AirElevation;
        model.EnvironmentMap = env.EnvironmentMap;
        model.SkyMap = env.SkyMap;
        model.ReservedTexturePath = env.TexturePath3;
        model.ColorCorrectionCube = env.ColorCorrectionCube;
        model.FogOfWarCorrectionCube = env.FogOfWarCorrectionCube;

        // Note: the typed properties above (model.SunDirection = ..., etc.)
        // ALREADY write directly into the Header copy at their corresponding
        // offsets (InfoModel.SunDirectionX/Y/Z etc. are implemented via WF_F,
        // writing into Header), so no extra manual write is needed —
        // assigning the typed properties above is enough.

        return model;
    }

    private static GameObject? FindTerrainGameObject(Transform mapRoot)
    {
        var t = mapRoot.Find("terrain");
        return t != null ? t.gameObject : null;
    }

    /// <summary>Copies textures into the archive layout, skipping Unity's .meta files.</summary>
    private static void CopyTexturesForExport(string mapStem, string texturesDir, string lightingDir)
    {
        var texRoot = SC2TerrainMaterialFactory.TextureRoot;
        if (!Directory.Exists(texRoot)) return;

        // Textures/* (excluding .meta)
        var topTexDir = Path.Combine(texRoot, "Textures");
        if (Directory.Exists(topTexDir))
            foreach (var f in Directory.GetFiles(topTexDir, "*", SearchOption.TopDirectoryOnly))
            {
                if (f.EndsWith(".meta", System.StringComparison.OrdinalIgnoreCase)) continue;
                File.Copy(f, Path.Combine(texturesDir, Path.GetFileName(f)), overwrite: true);
            }

        // maps/<mapStem>/lighting/* (excluding .meta)
        var srcLighting = Path.Combine(texRoot, "maps", mapStem, "lighting");
        if (Directory.Exists(srcLighting))
            foreach (var f in Directory.GetFiles(srcLighting, "*", SearchOption.TopDirectoryOnly))
            {
                if (f.EndsWith(".meta", System.StringComparison.OrdinalIgnoreCase)) continue;
                File.Copy(f, Path.Combine(lightingDir, Path.GetFileName(f)), overwrite: true);
            }
    }

    private static void WriteBdf(string dir, string prefix, string kind, BdfContainer container, BdfPadding padding) =>
        WriteBdfAs(dir, $"{prefix}.{kind}.win.bdf", container, padding);

    private static void WriteBdfAs(string dir, string fileName, BdfContainer container, BdfPadding padding)
    {
        var bytes = container.Write(padding);
        var finalPath = Path.Combine(dir, fileName);
        WriteFileSafe(finalPath, bytes);
    }

    /// <summary>
    /// Writes a file with several retries in case of a brief lock (e.g.
    /// EnableDiskWatch in SC2 reading the file at the exact moment of the
    /// write). Writes DIRECTLY into the target file via
    /// File.WriteAllBytes — no File.Replace/temp file: File.Replace can
    /// create a file with a new handle/inode, which the game may not
    /// recognize as the same tracked file (EnableDiskWatch's tracking could
    /// be bound to the original file handle), causing the game to stop
    /// seeing subsequent updates. Writing directly over the existing file
    /// keeps the same path and (typically) the same inode on NTFS, which is
    /// more reliable for an external file watcher.
    /// </summary>
    private static void WriteFileSafe(string finalPath, byte[] bytes)
    {
        const int maxAttempts = 6;
        var delayMs = 100;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                File.WriteAllBytes(finalPath, bytes);
                return; // success
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                // The file is held by another process (typically EnableDiskWatch
                // reading the map at that moment) — wait and try again.
                System.Threading.Thread.Sleep(delayMs);
                delayMs = System.Math.Min(delayMs * 2, 1000);
            }
        }

        // Last attempt — let it throw the real exception with useful context
        try
        {
            File.WriteAllBytes(finalPath, bytes);
        }
        catch (IOException ex)
        {
            throw new IOException(
                $"Failed to write \"{finalPath}\" after {maxAttempts} attempts — the file is held " +
                "by another process. If EnableDiskWatch is enabled in SC2, try minimizing " +
                "the game or closing the map for a moment before exporting again.", ex);
        }
    }

    private static void WriteTextIfNotEmpty(string dir, string fileName, string content)
    {
        if (string.IsNullOrEmpty(content)) return;
        WriteFileSafe(Path.Combine(dir, fileName), System.Text.Encoding.UTF8.GetBytes(content));
    }
}
#endif
