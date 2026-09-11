// ScdImportWindow.cs
// Import tooling: .scd -> Unity scene. EditorWindow: pick a .scd -> unzip
// it -> run each *.win.bdf through BdfIO -> build the scene hierarchy:
//
//   <MapName>/
//     Terrain          (TerrainMeshBuilder.Build, optionally SC2/Terrain shader)
//     Props/           (SC2Prop placeholder cubes)
//     Markers/         (SC2Marker gizmos)
//     Graphs/          (SC2Graph, edges between markers)
//     Environment      (SC2Environment <- info.bdf)
//   + SC2MapRawData asset (hfield/costs/collision2/decals as-is)
//
// A .bp parser for real prop models is future work; props are cubes
// labeled with their blueprintPath.

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using UnityEditor;
using UnityEngine;
using BdfIO.Core;
using BdfIO.Formats.Terrain;
using BdfIO.Formats.Info;
using BdfIO.Formats.Hfield;
using BdfIO.Formats.Costs;
using BdfIO.Formats.Collision2;
using BdfIO.Formats.Decals;

public class ScdImportWindow : EditorWindow
{
    private string _scdPath = "";
    private bool _useShader;

    [MenuItem("SC2/Import .scd...")]
    public static void ShowWindow() => GetWindow<ScdImportWindow>("SC2 Map Import");

    /// <summary>One pipeline step of the most recent Import() call.</summary>
    public sealed class ImportStage
    {
        public string Name;
        public bool Ok;
        public string Detail;
    }

    /// <summary>Result of the most recent Import() call — success/failure summary, shown inline by the unified shell's Import tab.</summary>
    public static string LastImportSummary { get; private set; } = "";

    /// <summary>
    /// Per-stage breakdown of the most recent Import() call (unzip, entry-point
    /// resolution, each BDF, scene build...) — available right at the "Import
    /// .scd" button, not only in the separate batch tool, so a failure on one
    /// specific map shows exactly which stage broke instead of a single opaque
    /// error.
    /// </summary>
    public static List<ImportStage> LastImportStages { get; } = new List<ImportStage>();

    private void OnGUI()
    {
        DrawImportControls(ref _scdPath, ref _useShader);
    }

    /// <summary>
    /// Shared file-picker + options + "Import" button UI, used both by this
    /// standalone window and the unified shell's Import tab — one place for
    /// this UI instead of the unified shell just bridging to a separate
    /// popup window with no visibility into what happened.
    /// </summary>
    public static void DrawImportControls(ref string scdPath, ref bool useShader)
    {
        EditorGUILayout.LabelField("Map (.scd)", scdPath);
        if (GUILayout.Button("Choose .scd..."))
        {
            var picked = EditorUtility.OpenFilePanel("Choose a map .scd", "", "scd");
            if (!string.IsNullOrEmpty(picked)) scdPath = picked;
        }

        useShader = EditorGUILayout.Toggle("SC2/Terrain shader (instead of placeholder)", useShader);

        EditorGUI.BeginDisabledGroup(string.IsNullOrEmpty(scdPath));
        if (GUILayout.Button("Import", GUILayout.Height(SC2UiScale.Pxf(26f))))
            Import(scdPath, useShader);
        EditorGUI.EndDisabledGroup();

        if (!string.IsNullOrEmpty(LastImportSummary))
        {
            EditorGUILayout.Space(4);
            var messageType = LastImportSummary.StartsWith("Import failed") ? MessageType.Error : MessageType.Info;
            EditorGUILayout.HelpBox(LastImportSummary, messageType);
        }

        if (LastImportStages.Count > 0)
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Diagnostics", EditorStyles.boldLabel);
            foreach (var stage in LastImportStages)
            {
                EditorGUILayout.BeginHorizontal();
                var icon = stage.Ok ? "✓" : "✗";
                var iconStyle = new GUIStyle(EditorStyles.boldLabel)
                {
                    normal = { textColor = stage.Ok ? new Color(0.35f, 0.75f, 0.35f) : new Color(0.85f, 0.3f, 0.3f) }
                };
                GUILayout.Label(icon, iconStyle, GUILayout.Width(SC2UiScale.Pxf(16f)));
                GUILayout.Label(stage.Name, GUILayout.Width(SC2UiScale.Pxf(150f)));
                GUILayout.Label(stage.Detail ?? "", EditorStyles.wordWrappedMiniLabel);
                EditorGUILayout.EndHorizontal();
            }
        }
    }

    public static void Import(string scdPath, bool useShader)
    {
        LastImportStages.Clear();
        try
        {
            ImportCore(scdPath, useShader);
        }
        catch (Exception ex)
        {
            LastImportSummary = $"Import failed: {ex.Message}";
            Debug.LogError($"[SC2Import] Import of '{scdPath}' failed: {ex}");
            EditorUtility.DisplayDialog("SC2 Import failed",
                $"Import of '{Path.GetFileName(scdPath)}' failed:\n\n{ex.Message}\n\nSee the Diagnostics list above (or the Console) for exactly which stage broke.",
                "OK");
        }
    }

    /// <summary>
    /// Runs one pipeline step, records it to LastImportStages either way, and
    /// either rethrows (stages that the rest of the import can't meaningfully
    /// continue without — unzip, entry-point resolution, terrain.bdf,
    /// hfield.bdf) or swallows the exception with a warning (recoverable
    /// stages — costs/collision2/mapobjs/info/decals: some vanilla maps
    /// genuinely ship without one of these, and all are either optional
    /// overlays or regeneratable later).
    /// </summary>
    /// <summary>Stage whose action doesn't have a meaningful detail string beyond "OK".</summary>
    private static void RunStage(string name, bool hardFailure, Action action)
    {
        RunStage(name, hardFailure, () => { action(); return "OK"; });
    }

    /// <summary>Stage whose action returns a short detail string (e.g. counts) recorded alongside its OK/FAIL status.</summary>
    private static void RunStage(string name, bool hardFailure, Func<string> action)
    {
        try
        {
            var detail = action();
            LastImportStages.Add(new ImportStage { Name = name, Ok = true, Detail = detail });
        }
        catch (Exception ex)
        {
            LastImportStages.Add(new ImportStage { Name = name, Ok = false, Detail = ex.Message });
            if (hardFailure)
                throw;
            Debug.LogWarning($"[SC2Import] {name} skipped (recoverable): {ex.Message}");
        }
    }

    private static void ImportCore(string scdPath, bool useShader)
    {
        var mapName = Path.GetFileNameWithoutExtension(scdPath);
        var tmpDir = Path.Combine(Path.GetTempPath(), $"sc2_import_{mapName}");

        RunStage("Cleanup temp dir", hardFailure: false, () =>
        {
            if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
        });

        SC2ScdEntryPointResolver.EntryPointResult entryPoint = null;
        RunStage("Unzip", hardFailure: true, () =>
        {
            Directory.CreateDirectory(tmpDir);
            // ZipFile.ExtractToDirectory with overwriteFiles requires .NET 5+ — extract manually for broader compatibility.
            using (var archive = ZipFile.OpenRead(scdPath))
                foreach (var entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue; // directories
                    var destPath = Path.Combine(tmpDir, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                    entry.ExtractToFile(destPath, overwrite: true);
                }

            var allBdf = Directory.GetFiles(tmpDir, "*.bdf", SearchOption.AllDirectories);
            Debug.Log($"[SC2Import] Extracted to {tmpDir}, .bdf files found: {allBdf.Length}\n" +
                      string.Join("\n", allBdf.Select(f => "  " + f)));
            return $"{allBdf.Length} .bdf file(s) extracted";
        });

        // Entry point resolution: prefer scenario.lua's 'map' field (the real
        // source of truth for which BDF stem belongs to this map) over
        // globbing for "the first *.terrain.win.bdf found anywhere", which
        // silently picked an arbitrary file when more than one matched (e.g.
        // a merged lua.scd + map .scd extraction). Falls back to the old
        // heuristic if no usable scenario.lua is found.
        RunStage("Entry point resolution", hardFailure: true, () =>
        {
            entryPoint = SC2ScdEntryPointResolver.Resolve(tmpDir);
            foreach (var warning in entryPoint.Warnings)
                Debug.LogWarning($"[SC2Import] {warning}");

            if (entryPoint.BdfStem == null || entryPoint.BdfDirectory == null)
                throw new InvalidOperationException($"no *.terrain.win.bdf found under {tmpDir} (searched recursively through every subfolder; scenario.lua did not help either)");

            Debug.Log($"[SC2Import] Map stem '{entryPoint.BdfStem}' resolved via {entryPoint.ResolutionMethod}");
            return $"stem='{entryPoint.BdfStem}' via {entryPoint.ResolutionMethod}";
        });

        var prefix = entryPoint.BdfStem;
        var foundDir = entryPoint.BdfDirectory;
        tmpDir = foundDir; // .scd often nests files under a subfolder (e.g. maps/<MapName>/...)

        RunStage("Copy textures", hardFailure: false, () =>
        {
            // Must copy from the extraction ROOT (tmpRoot), not scdDir (maps/MapName/).
            // .scd layout: Textures/ at the root, maps/MapName/ in a subfolder — both levels are needed.
            // Extraction root: foundDir = .../tmp_xxx/maps/MapName/ -> walk up 2 levels.
            // Falls back to foundDir if the layout is flat (bdf directly at the root).
            var mapsParent = Directory.GetParent(foundDir)?.FullName;  // .../maps/
            var scdExtractRoot = (mapsParent != null && Directory.GetParent(mapsParent) != null)
                ? Directory.GetParent(mapsParent)!.FullName   // .../tmp_xxx/
                : foundDir;
            CopyTexturesFromScd(scdExtractRoot);
            // ForceSynchronousImport guarantees LoadAssetAtPath finds
            // every texture BEFORE SC2TerrainMaterialFactory starts loading them.
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        });

        var root = new GameObject(mapName);

        // ---- Diagnostics: confirm Unity actually sees the textures ----
        {
            var testPath = $"{SC2TerrainMaterialFactory.TextureRoot}/Textures/Ground046_2K-PNG_NormalDX.dds";
            var testTex = AssetDatabase.LoadAssetAtPath<Texture2D>(testPath);
            Debug.Log($"[SC2Import] Texture check: '{testPath}' -> {(testTex != null ? "FOUND ✓" : "NOT FOUND ✗")}");
            if (testTex == null)
            {
                // List what's actually in the Textures folder
                var texDir = $"{SC2TerrainMaterialFactory.TextureRoot}/Textures";
                var allTex = AssetDatabase.FindAssets("t:Texture2D", new[] { texDir });
                Debug.Log($"[SC2Import] Texture files in {texDir}: {allTex.Length}");
                foreach (var guid in allTex.Take(5))
                    Debug.Log($"  -> {AssetDatabase.GUIDToAssetPath(guid)}");
            }
        }

        BdfContainer terrainContainer = null;
        TerrainModel terrainModel = null;
        GameObject terrainGo = null;
        RunStage("terrain.bdf", hardFailure: true, () =>
        {
            terrainContainer = ReadContainer(tmpDir, prefix, "terrain", TerrainBdf.Padding);
            terrainModel = TerrainBdf.Parse(terrainContainer);
            terrainGo = TerrainMeshBuilder.Build(terrainModel, root.transform,
                new TerrainMeshBuilder.Options { UseSC2TerrainShader = useShader });
            return $"{terrainModel.Materials.Count} materials, {terrainModel.Vertices.Count} verts, {terrainModel.Triangles.Count} tris";
        });

        // ---- Diagnostics: TexturePaths ----
        if (terrainModel.Materials.Count > 4)
        {
            var m = terrainModel.Materials[4];
            var p0 = m.TexturePaths[0] ?? "null";
            var p2 = m.TexturePaths[2] ?? "null";
            Debug.Log($"[SC2Diag] mat[4].TexturePaths[0]='{p0}' bytes=[{string.Join(",", p0.Take(20).Select(c => ((int)c).ToString("x2")))}]");
            Debug.Log($"[SC2Diag] mat[4].TexturePaths[2]='{p2}' bytes=[{string.Join(",", p2.Take(20).Select(c => ((int)c).ToString("x2")))}]");
            var n2 = SC2TerrainMaterialFactory.NormalizePath(p2);
            Debug.Log($"[SC2Diag] NormalizePath='{n2}'");
            var b2 = SC2TerrainMaterialFactory.StripExtension(n2);
            var fullPath = $"{SC2TerrainMaterialFactory.TextureRoot}/{b2}.dds";
            var tex2 = AssetDatabase.LoadAssetAtPath<Texture2D>(fullPath);
            Debug.Log($"[SC2Diag] LoadAssetAtPath('{fullPath}') = {(tex2 != null ? tex2.name : "NULL")}");
        }

        // hfield is required: costs/collision2 viz and the raw-data asset all depend on it.
        HfieldModel hfieldModel = null;
        RunStage("hfield.bdf", hardFailure: true, () =>
        {
            var hfieldContainer = ReadContainer(tmpDir, prefix, "hfield", HfieldBdf.Padding);
            hfieldModel = HfieldBdf.Parse(hfieldContainer);
            BuildHeightmapViz(hfieldModel, root.transform);
            return $"{hfieldModel.MapX}x{hfieldModel.MapY}, heightmap={hfieldModel.Heightmap.Length}";
        });

        // ---- collision2.bdf -> Collision mesh (toggleable, wireframe) ----
        // collision2.win.bdf is an OPTIONAL file — many maps ship without one
        // (it's derived from hfield and can be auto-generated later), so its
        // absence is expected, not an error. Check existence first so a merely
        // missing file reports as a normal/successful "not present" stage
        // instead of surfacing a FileNotFoundException as a red FAIL — that's
        // reserved for a collision2.bdf that exists but fails to parse.
        RunStage("collision2.bdf", hardFailure: false, () =>
        {
            if (!File.Exists(Path.Combine(tmpDir, $"{prefix}.collision2.win.bdf")))
                return "not present (optional — map has no manual collision2, can be auto-generated from hfield)";

            var collision2Container = ReadContainer(tmpDir, prefix, "collision2", Collision2Bdf.Padding);
            var collision2Model = Collision2Bdf.Parse(collision2Container);
            BuildCollision2Viz(collision2Model, root.transform);
            return $"{collision2Model.Vertices.Count} verts, {collision2Model.Triangles.Count} tris";
        });

        // ---- costs.bdf -> Navmesh overlay (toggleable, over terrain) ----
        RunStage("costs.bdf", hardFailure: false, () =>
        {
            var costsContainer = ReadContainer(tmpDir, prefix, "costs", CostsBdf.Padding);
            var costsModel = CostsBdf.Parse(costsContainer);
            BuildCostsViz(costsModel, hfieldModel, root.transform);
            return $"{costsModel.Blocks.Count} block(s)";
        });

        // ---- _save.lua markers + mapobjs props/path graphs ----
        var objectCounts = (Props: 0, Markers: 0, Graphs: 0);
        RunStage("Objects (_save.lua + mapobjs path data)", hardFailure: false, () =>
        {
            objectCounts = BuildMapObjects(Path.Combine(tmpDir, $"{prefix}.terrain.win.bdf"), root.transform);
            return $"{objectCounts.Props} props, {objectCounts.Markers} markers, {objectCounts.Graphs} graphs";
        });

        // ---- info.bdf -> Environment ----
        // Recoverable: falls back to SC2Environment's defaults if info.bdf is missing/corrupt.
        var env = (SC2Environment)null;
        RunStage("info.bdf", hardFailure: false, () =>
        {
            var info = ReadFormat(tmpDir, prefix, "info", InfoBdf.Padding, InfoBdf.Parse);
            var envGo = new GameObject("Environment");
            envGo.transform.SetParent(root.transform, false);
            env = envGo.AddComponent<SC2Environment>();
            env.Apply(info);
        });
        if (env == null)
        {
            var envGo = new GameObject("Environment");
            envGo.transform.SetParent(root.transform, false);
            env = envGo.AddComponent<SC2Environment>();
            env.Apply(new InfoModel());
        }

        // ---- Water material (submesh "Water", last in the material list) ----
        var waterIndex = terrainModel.Materials.FindIndex(SC2HfieldProjectionService.IsWaterMaterial);
        if (waterIndex >= 0)
        {
            var renderer = terrainGo.GetComponent<MeshRenderer>();
            var mats = renderer.sharedMaterials;
            mats[waterIndex] = SC2WaterMaterialFactory.Create(terrainModel, env);
            renderer.sharedMaterials = mats;
        }

        // ---- hfield/costs/collision2/decals + terrain raw + lua -> SC2MapRawData asset ----
        RunStage("Save raw data asset", hardFailure: false, () =>
        {
            var rawData = BuildRawData(tmpDir, prefix, mapName);
            rawData.TerrainNumFiles = terrainContainer.NumFiles;
            rawData.TerrainTrailingData = terrainContainer.TrailingData ?? System.Array.Empty<byte>();
            var infoContainerForSave = ReadContainer(tmpDir, prefix, "info", BdfIO.Formats.Info.InfoBdf.Padding);
            SaveRawDataAsset(rawData, mapName, terrainContainer, infoContainerForSave);
        });

        PositionSceneCamera(terrainModel);

        Selection.activeGameObject = root;
        var failedStages = LastImportStages.Count(s => !s.Ok);
        LastImportSummary = $"'{mapName}' imported: " +
                   $"{terrainModel.Materials.Count} materials, {terrainModel.Vertices.Count} vertices, " +
                   $"{objectCounts.Props} props, {objectCounts.Markers} markers, {objectCounts.Graphs} graphs." +
                   (failedStages > 0 ? $" ({failedStages} recoverable stage(s) skipped — see Diagnostics below)" : "");
        Debug.Log($"[SC2Import] {LastImportSummary}");
    }

    // ----------------------------------------------------------------
    // Canonical object import -> legacy scene components
    // ----------------------------------------------------------------

    private static (int Props, int Markers, int Graphs) BuildMapObjects(string terrainBdfPath, Transform parent)
    {
        var imported = ScriptableObject.CreateInstance<SC2TerrainData>();
        try
        {
            imported.EnsureObjectsLists();
            var result = SC2ObjectsImport.ImportFromTerrainBdfPath(imported, terrainBdfPath);
            if (!result.Success) throw new InvalidOperationException(result.Summary);
            foreach (var warning in result.Warnings) Debug.LogWarning("[SC2Import] " + warning);

            var propsRoot = new GameObject("Props").transform;
            propsRoot.SetParent(parent, false);
            foreach (var p in imported.ObjProps)
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                go.name = Path.GetFileNameWithoutExtension(p.BlueprintPath);
                go.transform.SetParent(propsRoot, false);
                go.transform.localPosition = p.Position;
                go.transform.localRotation = p.Rotation;
                go.transform.localScale = Vector3.one * Mathf.Max(0.01f, p.Scale);

                var comp = go.AddComponent<SC2Prop>();
                comp.BlueprintPath = p.BlueprintPath;
                comp.Flags = p.Flags;
            }

            var markersRoot = new GameObject("Markers").transform;
            markersRoot.SetParent(parent, false);
            var markerTransforms = new System.Collections.Generic.List<Transform>(imported.ObjMarkers.Count);
            var markerIndexesById = new System.Collections.Generic.Dictionary<int, int>();
            var graphMarkerIds = new System.Collections.Generic.HashSet<int>(
                imported.ObjGraphs.SelectMany(graph => graph.MarkerIdsA.Concat(graph.MarkerIdsB)));
            foreach (var marker in imported.ObjMarkers)
            {
                var go = new GameObject(marker.Name);
                go.transform.SetParent(markersRoot, false);
                go.transform.localPosition = marker.Position;
                var comp = go.AddComponent<SC2Marker>();
                comp.MarkerName = marker.Name;
                comp.MarkerType = marker.Type;
                comp.IsLuaOnly = !graphMarkerIds.Contains(marker.Id);
                comp.LuaExtras = marker.ExtraFields;
                markerIndexesById[marker.Id] = markerTransforms.Count;
                markerTransforms.Add(go.transform);
            }

            var graphsRoot = new GameObject("Graphs").transform;
            graphsRoot.SetParent(parent, false);
            foreach (var graph in imported.ObjGraphs)
            {
                var go = new GameObject(graph.Name);
                go.transform.SetParent(graphsRoot, false);
                var comp = go.AddComponent<SC2Graph>();
                comp.GraphName = graph.Name;
                comp.MarkerTransforms = markerTransforms;
                var edgeCount = Math.Min(graph.MarkerIdsA.Count, graph.MarkerIdsB.Count);
                for (var i = 0; i < edgeCount; i++)
                    if (markerIndexesById.TryGetValue(graph.MarkerIdsA[i], out var a) &&
                        markerIndexesById.TryGetValue(graph.MarkerIdsB[i], out var b))
                        comp.Entries.Add(new Vector2Int(a, b));
            }

            return (imported.ObjProps.Count, imported.ObjMarkers.Count, imported.ObjGraphs.Count);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(imported);
        }
    }

    // ----------------------------------------------------------------
    // hfield/costs/collision2/decals/terrain -> SC2MapRawData + .bytes files
    // ----------------------------------------------------------------

    private static SC2MapRawData BuildRawData(string tmpDir, string prefix, string mapName)
    {
        var data = ScriptableObject.CreateInstance<SC2MapRawData>();
        data.MapName = mapName;

        data.ScenarioLua = ReadTextIfExists(tmpDir, $"{prefix}_scenario.lua");
        data.ScriptLua   = ReadTextIfExists(tmpDir, $"{prefix}_script.lua");
        _pendingSaveLua  = ReadTextIfExists(tmpDir, $"{prefix}_save.lua");

        ParseScenarioPaths(data.ScenarioLua, out data.ScenarioMapPath, out data.ScenarioSavePath, out data.ScenarioScriptPath);

        // hfield is required (downstream costs/collision2/terrainTypes all depend
        // on it); costs/collision2/decals are recoverable — some vanilla maps
        // genuinely ship without one of them, and all three can be regenerated
        // later, so a missing/corrupt file here becomes an empty passthrough
        // container plus a warning instead of aborting the whole import.
        var hfieldContainer    = ReadContainer(tmpDir, prefix, "hfield",     HfieldBdf.Padding);
        var costsContainer     = ReadContainerOrEmpty(tmpDir, prefix, "costs",      CostsBdf.Padding);
        var collisionContainer = ReadContainerOrEmpty(tmpDir, prefix, "collision2", Collision2Bdf.Padding);
        var decalsContainer    = ReadContainerOrEmpty(tmpDir, prefix, "decals",     DecalsBdf.Padding);

        var hfield = HfieldBdf.Parse(hfieldContainer);
        data.Platform     = hfieldContainer.Platform;
        data.HfieldMapX   = hfield.MapX;
        data.HfieldMapY   = hfield.MapY;
        // Heightmap is stored in a .raw file (see SaveRawDataAsset)
        // TerrainTypes are stored in hfield.bytes (passthrough bdf)
        _pendingHfield = hfield;

        // Returned for SaveRawDataAsset (writes them out as .bytes files)
        _pendingContainers = (hfieldContainer, costsContainer, collisionContainer, decalsContainer);

        return data;
    }

    private static BdfIO.Formats.Hfield.HfieldModel _pendingHfield = null!;

    // Holds the containers between BuildRawData and SaveRawDataAsset
    private static (BdfIO.Core.BdfContainer Hfield, BdfIO.Core.BdfContainer Costs,
                    BdfIO.Core.BdfContainer Collision2, BdfIO.Core.BdfContainer Decals) _pendingContainers;

    /// <summary>
    /// Copies every texture from the extracted .scd root into
    /// Assets/SC2Textures/, preserving the folder structure. Skips the
    /// uncompiled/ folder (duplicate lua).
    /// </summary>
    private static void CopyTexturesFromScd(string extractRoot)
    {
        var textureRoot = SC2TerrainMaterialFactory.TextureRoot;
        if (!Directory.Exists(textureRoot))
            Directory.CreateDirectory(textureRoot);

        // Mirrored into StreamingAssets too — see
        // SC2TerrainMaterialFactory.StreamingAssetsTextureRoot's own doc
        // comment: Resources/ (and this plain Assets/SC2Textures root) only
        // exist as loose files in the Editor, never in a real Build, so
        // LoadTextureUncached's own-decoder path (bypassing Unity's native
        // DDS import, confirmed to mis-decode embedded mips for real
        // non-power-of-two vanilla textures) needs a second, always-loose
        // copy to read raw bytes from in both contexts.
        var streamingTextureRoot = Path.Combine(Application.dataPath, "StreamingAssets",
            SC2TerrainMaterialFactory.StreamingAssetsTextureRoot.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(streamingTextureRoot))
            Directory.CreateDirectory(streamingTextureRoot);

        var imageExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".dds", ".png", ".tga", ".jpg", ".jpeg" };

        var skipDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "uncompiled" };

        var copiedCount = 0;
        foreach (var file in Directory.GetFiles(extractRoot, "*", SearchOption.AllDirectories))
        {
            if (!imageExts.Contains(Path.GetExtension(file))) continue;

            // Skip folders listed in skipDirs
            var rel = file.Substring(extractRoot.Length).Replace('\\', '/').TrimStart('/');
            var firstSegment = rel.Split('/')[0];
            if (skipDirs.Contains(firstSegment)) continue;

            // Compare against the real source's byte length, not just "does
            // a file already exist" — a same-named destination file whose
            // size doesn't match the current source is a stale/partial copy
            // from an earlier import and must be overwritten, or it
            // silently persists forever (see SC2TerrainBdfImporter's own
            // EnsureTexturesAvailable, fixed the same way).
            var sourceLength = new FileInfo(file).Length;

            var destPath = Path.Combine(textureRoot, rel.Replace('/', Path.DirectorySeparatorChar));
            var destDir = Path.GetDirectoryName(destPath)!;
            if (!Directory.Exists(destDir))
                Directory.CreateDirectory(destDir);
            if (!File.Exists(destPath) || new FileInfo(destPath).Length != sourceLength)
            {
                File.Copy(file, destPath, overwrite: true);
                copiedCount++;
            }

            var streamingDestPath = Path.Combine(streamingTextureRoot, rel.Replace('/', Path.DirectorySeparatorChar));
            var streamingDestDir = Path.GetDirectoryName(streamingDestPath)!;
            if (!Directory.Exists(streamingDestDir))
                Directory.CreateDirectory(streamingDestDir);
            if (!File.Exists(streamingDestPath) || new FileInfo(streamingDestPath).Length != sourceLength)
                File.Copy(file, streamingDestPath, overwrite: true);
        }

        Debug.Log($"[SC2Import] Copied {copiedCount} textures -> {textureRoot}/ (and into {streamingTextureRoot}/)");
    }

    /// <summary>
    /// Extracts the map/save/script paths from scenario.lua.
    /// '/maps/SC2_D3_crab/SC2_D3_crab.scmap' -> ScenarioMapPath etc.
    /// Delegates to SC2ScdEntryPointResolver so there is exactly one place
    /// that knows how to parse scenario.lua fields (it used to be duplicated
    /// here with its own regex, which could silently drift out of sync).
    /// </summary>
    private static void ParseScenarioPaths(string scenarioLua, out string mapPath, out string savePath, out string scriptPath)
    {
        var parsed = SC2ScdEntryPointResolver.ParseScenarioPaths(scenarioLua);
        mapPath = parsed.Map;
        savePath = parsed.Save;
        scriptPath = parsed.Script;
    }

    private static void SaveRawDataAsset(SC2MapRawData data, string mapName,
        BdfIO.Core.BdfContainer terrainContainer,
        BdfIO.Core.BdfContainer infoContainer)
    {
        var dir = $"Assets/SC2Maps/{mapName}";
        if (!AssetDatabase.IsValidFolder("Assets/SC2Maps"))
            AssetDatabase.CreateFolder("Assets", "SC2Maps");
        if (!AssetDatabase.IsValidFolder(dir))
            AssetDatabase.CreateFolder("Assets/SC2Maps", mapName);

        // Heightmap -> .raw (uint16 LE, mapX*mapY)
        var rawPath = $"{dir}/heightmap.raw";
        var hmBytes = new byte[_pendingHfield.Heightmap.Length * 2];
        System.Buffer.BlockCopy(_pendingHfield.Heightmap, 0, hmBytes, 0, hmBytes.Length);
        System.IO.File.WriteAllBytes(rawPath, hmBytes);

        // save.lua -> .bytes (UTF8)
        var saveLuaPath = $"{dir}/save_lua.bytes";
        System.IO.File.WriteAllBytes(saveLuaPath, System.Text.Encoding.UTF8.GetBytes(_pendingSaveLua));

        // .bytes files for each bdf
        data.TerrainBytes      = SaveBytes(dir, "terrain",       terrainContainer.Data);
        data.TerrainOtBytes    = SaveBytes(dir, "terrain_ot",    UintToBytes(terrainContainer.OffsetTable));
        data.InfoBytes         = SaveBytes(dir, "info",          infoContainer.Data);
        data.InfoOtBytes       = SaveBytes(dir, "info_ot",       UintToBytes(infoContainer.OffsetTable));
        data.HfieldBytes       = SaveBytes(dir, "hfield",        _pendingContainers.Hfield.Data);
        data.HfieldOtBytes     = SaveBytes(dir, "hfield_ot",     UintToBytes(_pendingContainers.Hfield.OffsetTable));
        data.CostsBytes        = SaveBytes(dir, "costs",         _pendingContainers.Costs.Data);
        data.CostsOtBytes      = SaveBytes(dir, "costs_ot",      UintToBytes(_pendingContainers.Costs.OffsetTable));
        data.Collision2Bytes   = SaveBytes(dir, "collision2",    _pendingContainers.Collision2.Data);
        data.Collision2OtBytes = SaveBytes(dir, "collision2_ot", UintToBytes(_pendingContainers.Collision2.OffsetTable));
        data.DecalsBytes       = SaveBytes(dir, "decals",        _pendingContainers.Decals.Data);
        data.DecalsOtBytes     = SaveBytes(dir, "decals_ot",     UintToBytes(_pendingContainers.Decals.OffsetTable));

        // Refresh so AssetDatabase picks up the .bytes and .raw files
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        data.SaveLuaBytes = AssetDatabase.LoadAssetAtPath<TextAsset>($"{dir}/save_lua.bytes");

        var assetPath = $"{dir}/{mapName}_RawData.asset";
        AssetDatabase.CreateAsset(data, assetPath);
        AssetDatabase.SaveAssets();
        Debug.Log($"[SC2Import] RawData -> {assetPath}");
    }

    private static string _pendingSaveLua = "";

    private static TextAsset SaveBytes(string dir, string name, byte[] bytes)
    {
        var path = $"{dir}/{name}.bytes";
        System.IO.File.WriteAllBytes(path, bytes);
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
        return AssetDatabase.LoadAssetAtPath<TextAsset>(path)!;
    }

    private static byte[] UintToBytes(uint[] arr)
    {
        var b = new byte[arr.Length * 4];
        System.Buffer.BlockCopy(arr, 0, b, 0, b.Length);
        return b;
    }

    private static int EstimateKb(SC2MapRawData d) =>
        (d.ScenarioLua.Length + d.ScriptLua.Length) / 1024;

    // ----------------------------------------------------------------
    // helpers
    // ----------------------------------------------------------------

    // ----------------------------------------------------------------
    // Heightmap visualization (mesh built from the heightfield)
    // ----------------------------------------------------------------
    private static void BuildHeightmapViz(HfieldModel hfield, Transform parent)
    {
        var go = new GameObject("Heightmap");
        go.transform.SetParent(parent, false);
        // Object is ACTIVE (so it's visible in the Hierarchy), but its MeshRenderer is off by default

        var w = (int)hfield.MapX - 1;
        var h = (int)hfield.MapY - 1;
        var scaleXZ = 1f;
        const float heightScale = 1f / 128f; // raw uint16 / 128 = world Y (matches terrain vertices)

        var verts = new Vector3[(w + 1) * (h + 1)];
        for (var y = 0; y <= h; y++)
        for (var x = 0; x <= w; x++)
        {
            var raw = hfield.Heightmap[y * (w + 1) + x];
            verts[y * (w + 1) + x] = new Vector3(x * scaleXZ, raw * heightScale, y * scaleXZ);
        }

        var tris = new int[w * h * 6];
        var t = 0;
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var i = y * (w + 1) + x;
            tris[t++] = i; tris[t++] = i + (w + 1); tris[t++] = i + 1;
            tris[t++] = i + 1; tris[t++] = i + (w + 1); tris[t++] = i + (w + 1) + 1;
        }

        var mesh = new Mesh { name = "Heightmap", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        mesh.SetVertices(verts);
        mesh.SetIndices(tris, MeshTopology.Triangles, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();

        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var mr = go.AddComponent<MeshRenderer>();
        var mat = new Material(Shader.Find("Standard") ?? Shader.Find("Universal Render Pipeline/Lit"))
            { name = "Heightmap", color = new Color(0.6f, 0.5f, 0.4f, 0.5f) };
        mat.SetFloat("_Mode", 3);
        mr.sharedMaterial = mat;
        mr.enabled = false; // hidden by default, but the object stays active in the Hierarchy
    }

    // ----------------------------------------------------------------
    // Collision2 visualization (CCW winding, +Y normals, no flip needed)
    // ----------------------------------------------------------------
    private static void BuildCollision2Viz(Collision2Model collision2, Transform parent)
    {
        var go = new GameObject("Collision2");
        go.transform.SetParent(parent, false);
        // Object stays active in the Hierarchy, MeshRenderer off by default

        var verts = collision2.Vertices.Select(v => new Vector3(v.X, v.Y, v.Z)).ToArray();
        var tris = new int[collision2.Triangles.Count * 3];
        for (var i = 0; i < collision2.Triangles.Count; i++)
        {
            // CCW winding confirmed: cross(AB,AC).Y > 0 for 100% of triangles — no flip needed
            tris[i * 3 + 0] = (int)collision2.Triangles[i].A;
            tris[i * 3 + 1] = (int)collision2.Triangles[i].B;
            tris[i * 3 + 2] = (int)collision2.Triangles[i].C;
        }

        var mesh = new Mesh { name = "Collision2", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        mesh.SetVertices(verts);
        mesh.SetIndices(tris, MeshTopology.Triangles, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();

        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterial = MakeTransparentMat("Collision2", new Color(1f, 0.2f, 0.2f, 0.35f));
        mr.enabled = false;
    }

    // ----------------------------------------------------------------
    // Costs visualization: 5 blocks (Land_Small/Medium/Large, Water_Small/Large),
    // each its own GameObject (toggle them on one at a time)
    // ----------------------------------------------------------------
    private static readonly string[] CostsBlockNames =
        { "Land_Small", "Land_Medium", "Land_Large", "Water_Small", "Water_Large" };

    private static readonly Color[] CostsBlockColors =
    {
        new Color(1f,   0.15f, 0.15f, 0.5f),
        new Color(1f,   0.5f,  0.1f,  0.5f),
        new Color(1f,   0.8f,  0f,    0.5f),
        new Color(0.2f, 0.4f,  1f,    0.5f),
        new Color(0f,   0.2f,  1f,    0.5f),
    };

    private static void BuildCostsViz(CostsModel costs, HfieldModel hfield, Transform parent)
    {
        if (costs.Blocks.Count == 0) return;

        // Costs are visualized via a 1024x1024 Texture2D over the terrain mesh.
        // No heavy meshes — just a texture over the terrain via the SC2/CostsOverlay shader.
        var costsRoot = new GameObject("Costs");
        costsRoot.transform.SetParent(parent, false);

        var terrainGo = parent.Find("terrain")?.gameObject;
        if (terrainGo == null) return;
        var terrainMf = terrainGo.GetComponent<MeshFilter>();
        if (terrainMf == null || terrainMf.sharedMesh == null) return;

        var shader = Shader.Find("SC2/CostsOverlay");
        if (shader == null)
        {
            Debug.LogWarning("[SC2Import] SC2/CostsOverlay not found — copy SC2CostsOverlay.shader into Assets/SC2/Shaders/");
            return;
        }

        var w = (int)hfield.MapX - 1;
        var h = (int)hfield.MapY - 1;

        for (var b = 0; b < costs.Blocks.Count; b++)
        {
            var navmesh   = costs.Blocks[b].Navmesh;
            var blockName = b < CostsBlockNames.Length ? CostsBlockNames[b] : $"Block_{b}";
            var tint      = b < CostsBlockColors.Length ? CostsBlockColors[b] : new Color(1,0,1,0.5f);

            // Create the 1024x1024 texture
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false, true)
                { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, name = blockName };
            var pixels = new Color[w * h];
            for (var i = 0; i < navmesh.Length && i < pixels.Length; i++)
            {
                pixels[i] = navmesh[i] == 255
                    ? tint
                    : navmesh[i] == 5
                        ? new Color(1f, 1f, 0f, 0.3f)
                        : Color.clear;
            }
            tex.SetPixels(pixels);
            tex.Apply();

            // Create a child object with the terrain mesh + overlay material
            var go = new GameObject(blockName);
            go.transform.SetParent(costsRoot.transform, false);
            go.transform.SetPositionAndRotation(terrainGo.transform.position, terrainGo.transform.rotation);
            go.transform.localScale = terrainGo.transform.localScale;

            go.AddComponent<MeshFilter>().sharedMesh = terrainMf.sharedMesh;
            var mat = new Material(shader) { name = blockName, mainTexture = tex };
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.enabled = (b == 0); // only Land_Small visible by default
        }
    }

    private static Material MakeTransparentMat(string name, Color color)
    {
        // Used only for Collision2/Heightmap
        var shader = Shader.Find("SC2/CostsOverlay")
                  ?? Shader.Find("Unlit/Color")
                  ?? Shader.Find("Standard");
        return new Material(shader!) { name = name, color = color };
    }

    /// <summary>Moves the Scene View camera top-down over the center of the map.</summary>
    private static void PositionSceneCamera(TerrainModel model)
    {
        if (SceneView.lastActiveSceneView == null) return;

        // Center and extent on XZ — coordinates map 1:1 from SC2
        float minX = float.MaxValue, maxX = float.MinValue;
        float minZ = float.MaxValue, maxZ = float.MinValue;
        float minY = float.MaxValue, maxY = float.MinValue;
        foreach (var v in model.Vertices)
        {
            if (v.PosX < minX) minX = v.PosX; if (v.PosX > maxX) maxX = v.PosX;
            if (v.PosZ < minZ) minZ = v.PosZ; if (v.PosZ > maxZ) maxZ = v.PosZ;
            if (v.PosY < minY) minY = v.PosY; if (v.PosY > maxY) maxY = v.PosY;
        }

        float cx = (minX + maxX) / 2f;
        float cy = (minY + maxY) / 2f;
        float cz = (minZ + maxZ) / 2f;
        float size = Mathf.Max(maxX - minX, maxZ - minZ) / 2f;

        var sv = SceneView.lastActiveSceneView;
        sv.LookAt(
            new Vector3(cx, cy, cz),
            Quaternion.Euler(90, 0, 0), // top-down
            size
        );
        sv.Repaint();
    }

    private static string ReadTextIfExists(string dir, string fileName)
    {
        var path = Path.Combine(dir, fileName);
        return File.Exists(path) ? File.ReadAllText(path) : "";
    }

    private static BdfContainer ReadContainer(string dir, string prefix, string kind, BdfPadding padding)
    {
        var path = Path.Combine(dir, $"{prefix}.{kind}.win.bdf");
        var raw = File.ReadAllBytes(path);
        return BdfContainer.Read(raw, padding);
    }

    private static T ReadFormat<T>(string dir, string prefix, string kind, BdfPadding padding, Func<BdfContainer, T> parse) =>
        parse(ReadContainer(dir, prefix, kind, padding));

    /// <summary>
    /// Like ReadContainer, but for optional/regeneratable formats (costs,
    /// collision2, decals): a missing file is expected (no warning — these are
    /// genuinely optional per the vanilla map format) and a parse failure on a
    /// file that DOES exist logs a warning; either way an empty container is
    /// returned instead of throwing, so one absent/broken optional BDF doesn't
    /// abort the whole import.
    /// </summary>
    private static BdfContainer ReadContainerOrEmpty(string dir, string prefix, string kind, BdfPadding padding)
    {
        var path = Path.Combine(dir, $"{prefix}.{kind}.win.bdf");
        if (!File.Exists(path))
            return new BdfContainer();

        try
        {
            return ReadContainer(dir, prefix, kind, padding);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[SC2Import] {kind}.bdf exists but failed to parse (recoverable, using empty container): {ex.Message}");
            return new BdfContainer();
        }
    }
}
#endif
