using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using BdfIO.Core;
using BdfIO.Formats.Terrain;
using SC2.MapEditor.Document;

/// <summary>
/// Imports a single standalone terrain.bdf file (not packaged inside a
/// .scd) directly into the scene as a fully-shaded mesh — for maps whose
/// materials, masks, texture paths and shader setup were already finalized
/// by the authoring pipeline (e.g. exported straight from Blender) and just
/// need to be dropped into Unity: positioned/scaled/rotated, and have their
/// material textures pointed at whatever's available in this project.
///
/// Reuses the same TerrainBdf.Parse / TerrainMeshBuilder pipeline already
/// used by the .scd importer (SC2RuntimeScdImporter) — just entered
/// directly from a loose .bdf file instead of extracting one from inside a
/// zip archive — with UseSC2TerrainShader=true so materials get real
/// textures via SC2TerrainMaterialFactory (the .scd path instead builds
/// flat placeholder-color materials, since that importer is about viewing
/// overall map structure, not shading).
///
/// Before building the mesh, EnsureTexturesAvailable walks every texture
/// path referenced by the parsed model and, if the game-data folder next to
/// the .bdf still has the source file, copies it into
/// Resources/SC2Textures/&lt;same relative path&gt;.&lt;dds|png|tga&gt; — the exact
/// path SC2TerrainMaterialFactory.LoadTexture already looks under
/// (ResourcesTextureRoot="SC2Textures", loaded via Resources.Load — note:
/// no "Assets/" prefix, unlike the AssetDatabase-style TextureRoot) — so
/// materials get their real textures automatically without a manual pick
/// through the runtime texture browser.
///
/// Lives outside any Editor/ folder (no UnityEditor dependency — BdfIO and
/// TerrainMeshBuilder are plain data/mesh code), so this works identically
/// in the Editor and in a standalone Build &amp; Run player. The texture
/// auto-copy itself is Editor/Play-mode-only in effect, though: Resources
/// content is packed into the build ahead of time, so files copied into
/// that folder after the game has already been built cannot be picked up
/// by Resources.Load in a shipped standalone player — see the warning this
/// logs in that case.
/// </summary>
public static class SC2TerrainBdfImporter
{
    // Slots consumed directly by SC2TerrainMaterialFactory.Create (Diffuse1/2,
    // Normal1/2, Mask1/2, Ramp) — kept in sync with that mapping.
    private static readonly int[] DirectSlots = { 0, 1, 2, 3, 6, 7, 8 };

    /// <summary>
    /// TextureAttempts records exactly what SC2TerrainMaterialFactory.Create
    /// actually tried for every texture slot on every material (found/not,
    /// and what resolved it) — needed because Debug.Log output isn't visible
    /// in a shipped standalone player, so this is surfaced in-UI instead
    /// (see SC2GameViewEditorUI's "Imported Terrain BDF" panel).
    /// </summary>
    public sealed class ImportResult
    {
        public GameObject Root;
        public List<SC2TerrainMaterialFactory.TextureLoadAttempt> TextureAttempts;

        /// <summary>
        /// The raw parsed model — kept around (not just the built GameObject)
        /// so a later "Export terrain.bdf" can merge this object's original
        /// Materials/Layers/Vertices/Triangles straight back into the
        /// exported map (see SC2TerrainExporter.MergeImportedTerrain),
        /// instead of having to reverse-engineer texture paths etc. from the
        /// live Unity Material/Texture2D objects on Root.
        /// </summary>
        public TerrainModel Model;

        /// <summary>The exact bytes this entry was parsed from — added so a
        /// caller (SC2GameViewEditorUI's imported-terrain tracking) can
        /// persist "what to re-parse" into SC2MapDocument.Terrain.
        /// ImportedEntries (as a content-addressed blob) instead of only a
        /// file path, which may not exist on the machine/session that later
        /// loads the saved map. See ImportFromBytes below for the matching
        /// reconstruction entry point.</summary>
        public byte[] RawBytes;
    }

    public static ImportResult Import(string bdfPath) => Import(bdfPath, null);

    /// <summary>archiveReader: the unified "Import Map" orchestrator's
    /// composite reader (Scripts/MapDocument/Domain/SC2SourceContainer.cs),
    /// used as a fallback texture source when a referenced texture isn't
    /// found loose on disk — a real game install ships terrain-diffuse/
    /// normal textures inside a SEPARATE textures.scd archive from the one
    /// carrying the map's own BDFs (confirmed against a real installed
    /// copy), so EnsureTexturesAvailable's loose-file search alone can never
    /// find them there. Null (the default, via the overload above) preserves
    /// this method's original loose-file-only behavior exactly.</summary>
    public static ImportResult Import(string bdfPath, ISC2SourceContainerReader archiveReader)
    {
        var bytes = File.ReadAllBytes(bdfPath);
        var container = BdfContainer.Read(bytes, TerrainBdf.Padding);
        var model = TerrainBdf.Parse(container);

        EnsureTexturesAvailable(model, bdfPath, archiveReader);

        return BuildFromModel(model, bytes, Path.GetFileNameWithoutExtension(bdfPath));
    }

    /// <summary>Rebuilds an imported terrain.bdf entry directly from its
    /// original file bytes, with no source file path on disk at all — the
    /// reverse-bridge entry point used when restoring SC2MapDocument.Terrain.
    /// ImportedEntries after a Load (SC2GameViewEditorUI.RestoreImportedTerrainsFromDocument):
    /// the only surviving source of truth at that point is the persisted
    /// blob, not the user's original file location, which may not even
    /// exist anymore on this machine/session. No sibling 'Textures' folder
    /// lookup is possible without a real path, so texture resolution relies
    /// entirely on whatever SC2TerrainMaterialFactory.LoadTexture can
    /// already find in Resources/SC2Textures or StreamingAssets — normally
    /// already populated by the original import that produced this blob in
    /// the first place. Caller is responsible for re-applying any persisted
    /// transform/material-property overrides on top of the freshly-built
    /// Root — this only reconstructs the as-imported baseline.</summary>
    public static ImportResult ImportFromBytes(byte[] bytes, string displayName)
    {
        var container = BdfContainer.Read(bytes, TerrainBdf.Padding);
        var model = TerrainBdf.Parse(container);
        return BuildFromModel(model, bytes, string.IsNullOrEmpty(displayName) ? "ImportedTerrain" : displayName);
    }

    private static ImportResult BuildFromModel(TerrainModel model, byte[] rawBytes, string displayName)
    {
        var attempts = SC2TerrainMaterialFactory.BeginDiagnosticCapture();
        var go = TerrainMeshBuilder.Build(model, null, new TerrainMeshBuilder.Options { UseSC2TerrainShader = true });
        SC2TerrainMaterialFactory.EndDiagnosticCapture();

        // Imported terrain.bdf geometry previously
        // had no Collider at all, so Objects height-sampling/placement
        // (SC2ObjectsShell.SampleHeight/GetSpawnPoint/Reproject*) and the
        // drag raycast (SC2TerrainShell.RaycastTerrainHeight) could only ever
        // see the live procedural terrain's analytic heightfield — a marker/
        // prop/unit placed on a fully-Blender-imported map silently landed
        // at the live default plane's flat height instead of the real
        // imported surface. A MeshCollider here is what those raycasts now
        // check (see SC2ObjectsShell.TryRaycastImportedTerrain).
        var meshFilter = go.GetComponent<MeshFilter>();
        if (meshFilter != null && meshFilter.sharedMesh != null)
            go.AddComponent<MeshCollider>().sharedMesh = meshFilter.sharedMesh;
        if (go.GetComponent<SC2DecalProjectionSurface>() == null)
            go.AddComponent<SC2DecalProjectionSurface>();

        // The object's origin from the file's layer headers: the mesh is kept relative to it and the transform sits on it, so the
        // object moves, turns and scales about its own origin instead of the editor's (0, 0, 0). Where the file records no origin
        // this is (0, 0, 0) and nothing changes.
        var pivot = SC2ImportedPivot.FromLayerCenters(model.Layers.Select(layer => layer.MapCenter));
        ApplyPivot(go, pivot);

        go.name = displayName;
        return new ImportResult { Root = go, TextureAttempts = attempts, Model = model, RawBytes = rawBytes };
    }

    /// <summary>Moves the origin of an imported object's mesh to <paramref name="pivot"/> (file coordinates): the vertices become
    /// relative to it, the transform is put on it (so every vertex stays where it was) and the pivot is remembered on the object.</summary>
    public static void ApplyPivot(GameObject go, Vector3 pivot)
    {
        var meshFilter = go.GetComponent<MeshFilter>();
        if (meshFilter != null && meshFilter.sharedMesh != null) ShiftMesh(meshFilter.sharedMesh, pivot);
        // The collider was baked from the vertices before they were shifted; without baking it again its surface sits one pivot
        // away from the visible mesh, and every height sampled from it (markers, props, units, areas) is wrong.
        var collider = go.GetComponent<MeshCollider>();
        if (collider != null && meshFilter != null)
        {
            collider.sharedMesh = null;
            collider.sharedMesh = meshFilter.sharedMesh;
        }
        var holder = go.GetComponent<SC2ImportedTerrainPivot>() ?? go.AddComponent<SC2ImportedTerrainPivot>();
        holder.Value = pivot;
        go.transform.position = pivot;
    }

    /// <summary>Makes a mesh built from file coordinates relative to <paramref name="pivot"/>.</summary>
    public static void ShiftMesh(Mesh mesh, Vector3 pivot)
    {
        if (pivot == Vector3.zero) return;
        var vertices = mesh.vertices;
        for (var i = 0; i < vertices.Length; i++) vertices[i] -= pivot;
        mesh.vertices = vertices;
        mesh.RecalculateBounds();
    }

    private static void EnsureTexturesAvailable(TerrainModel model, string bdfPath, ISC2SourceContainerReader archiveReader = null)
    {
        CopyReferencedTextures(CollectTexturePaths(model), bdfPath, archiveReader);
    }

    /// <summary>Shared "find + copy referenced textures next to a loose .bdf
    /// (or from a scanned archive) into Resources/SC2Textures + StreamingAssets"
    /// logic — factored out of the terrain-specific EnsureTexturesAvailable
    /// above so SC2DecalsBdfImporter can reuse the exact same resolution
    /// chain for a decals.bdf's DiffuseAtlasPath/NormalAtlasPath instead of
    /// its own bare Resources.Load-only lookup, which could never find a
    /// texture unless it happened to already be a Unity asset — confirmed
    /// real bug: a real map's decal atlas textures render as flat/untextured
    /// in the editor even though the same atlas paths resolve fine in the
    /// real game (which reads straight from the game's own data, not through
    /// this project's Resources/SC2Textures copy step at all).</summary>
    public static void CopyReferencedTextures(IEnumerable<string> texturePaths, string bdfPath, ISC2SourceContainerReader archiveReader = null)
    {
        var destRoot = Path.Combine(Application.dataPath, "SC2", "Resources",
            SC2TerrainMaterialFactory.ResourcesTextureRoot.Replace('/', Path.DirectorySeparatorChar));
        // Mirrored into StreamingAssets too (see SC2TerrainMaterialFactory.
        // StreamingAssetsTextureRoot's own doc comment) — Resources/ content
        // gets packed into the Player's data files at build time and is
        // never File.ReadAllBytes-accessible again, so LoadTextureUncached's
        // own-decoder path (bypassing Unity's native DDS import, which is
        // confirmed to mis-decode embedded mips for real non-power-of-two
        // vanilla textures) needs a second, always-loose-files copy to read
        // from, in both the Editor and a real Build.
        var streamingDestRoot = Path.Combine(Application.dataPath, "StreamingAssets",
            SC2TerrainMaterialFactory.StreamingAssetsTextureRoot.Replace('/', Path.DirectorySeparatorChar));

        var gameRoot = FindGameRoot(bdfPath);
        if (gameRoot == null && archiveReader == null)
        {
            // Still list every texture the model actually needs — a single
            // generic "skipped" line isn't actionable. Without a sibling
            // 'Textures' folder next to the .bdf (or a scanned archive to
            // fall back to — see below) there is nowhere to copy from, but
            // the user needs to know exactly which files (and at which
            // destination path) to place manually.
            var seenNoRoot = new HashSet<string>();
            var lines = new List<string>();
            foreach (var rawPath in texturePaths)
            {
                var basePath = SC2TerrainMaterialFactory.StripExtension(SC2TerrainMaterialFactory.NormalizePath(rawPath));
                if (!seenNoRoot.Add(basePath)) continue;
                lines.Add($"  '{basePath}.dds' -> {Path.Combine(destRoot, basePath.Replace('/', Path.DirectorySeparatorChar) + ".dds")}");
            }
            Debug.LogWarning($"[SC2TerrainBdfImporter] no 'Textures' folder found next to '{bdfPath}' (checked up to 6 parent folder levels) — " +
                              $"texture auto-copy skipped. The model references {lines.Count} texture(s); " +
                              "materials will only get them if files already exist at the paths below (place them there manually, " +
                              $"or put the source .dds files next to the .bdf in a 'Textures/' subfolder):\n{string.Join("\n", lines)}");
            return;
        }

        var sourceExtensions = new[] { ".win.dds", ".dds", ".png", ".tga" };
        var seen = new HashSet<string>();
        var copied = 0; var missing = 0; var alreadyPresent = 0; var copiedFromArchive = 0;

        foreach (var rawPath in texturePaths)
        {
            var normalized = SC2TerrainMaterialFactory.NormalizePath(rawPath);
            var basePath = SC2TerrainMaterialFactory.StripExtension(normalized);
            if (!seen.Add(basePath)) continue;

            var destBase = Path.Combine(destRoot, basePath.Replace('/', Path.DirectorySeparatorChar));
            var streamingDestBase = Path.Combine(streamingDestRoot, basePath.Replace('/', Path.DirectorySeparatorChar));

            string sourceFound = null;
            if (gameRoot != null)
            {
                foreach (var ext in sourceExtensions)
                {
                    var candidate = Path.Combine(gameRoot, basePath.Replace('/', Path.DirectorySeparatorChar) + ext);
                    if (File.Exists(candidate)) { sourceFound = candidate; break; }
                }
            }

            // Real game installs ship terrain-diffuse/normal
            // textures inside a SEPARATE textures.scd archive from the one
            // carrying the map's own BDFs (confirmed against a real
            // installed copy: maps.scd has the BDFs, textures.scd has
            // shared library textures at the same "Textures/..."-relative
            // path convention) — try the scanned archive reader before
            // giving up, same relative-path convention as the loose case.
            string archiveEntryPath = null; byte[] archiveBytes = null; string archiveExt = null;
            if (sourceFound == null && archiveReader != null)
            {
                foreach (var ext in sourceExtensions)
                {
                    var candidatePath = basePath + ext;
                    if (!archiveReader.Entries.Any(e => string.Equals(e.NormalizedPath, candidatePath, System.StringComparison.OrdinalIgnoreCase)))
                        continue;
                    using var stream = archiveReader.OpenEntry(candidatePath);
                    using var ms = new MemoryStream();
                    stream.CopyTo(ms);
                    archiveBytes = ms.ToArray();
                    archiveEntryPath = candidatePath;
                    archiveExt = ext;
                    break;
                }
            }

            if (sourceFound == null && archiveBytes == null)
            {
                // No real source to verify against — fall back to the old
                // "already have something, good enough" behavior rather than
                // treating this as missing when a prior import already
                // succeeded from a gameRoot that's no longer reachable.
                var resourcesPresentNoSource = File.Exists(destBase + ".dds") || File.Exists(destBase + ".png") || File.Exists(destBase + ".tga");
                var streamingPresentNoSource = File.Exists(streamingDestBase + ".dds") || File.Exists(streamingDestBase + ".png") || File.Exists(streamingDestBase + ".tga");
                if (resourcesPresentNoSource && streamingPresentNoSource) { alreadyPresent++; continue; }
                Debug.LogWarning($"[SC2TerrainBdfImporter] source texture not found: '{normalized}' — " +
                                  $"looked in '{gameRoot ?? "(no loose Textures folder)"}' and in {(archiveReader != null ? "the scanned archives" : "(no archives scanned)")} " +
                                  $"(extensions: {string.Join(", ", sourceExtensions)}).");
                missing++;
                continue;
            }

            // Normalize ".win.dds" source files down to a plain ".dds" destination —
            // LoadTexture's Resources.Load call resolves the extension itself.
            var resolvedExt = sourceFound != null ? sourceFound : archiveExt;
            var destExt = resolvedExt.EndsWith(".win.dds", System.StringComparison.OrdinalIgnoreCase) ? ".dds" : Path.GetExtension(resolvedExt);
            var sourceLength = sourceFound != null ? new FileInfo(sourceFound).Length : archiveBytes.Length;

            // Compare against the real source's byte length, not just
            // "does a file already exist at the destination" — a
            // same-named file whose size doesn't match the current source
            // is a stale/partial copy from an earlier import (e.g. before
            // this project's own decode fixes landed) and must be
            // overwritten, or it silently persists forever since nothing
            // else ever re-verifies it against the source.
            var destPath = destBase + destExt;
            var resourcesUpToDate = File.Exists(destPath) && new FileInfo(destPath).Length == sourceLength;
            if (!resourcesUpToDate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destPath));
                if (sourceFound != null) File.Copy(sourceFound, destPath, overwrite: true);
                else File.WriteAllBytes(destPath, archiveBytes);
                Debug.Log($"[SC2TerrainBdfImporter] texture copied: '{sourceFound ?? archiveEntryPath}' -> '{destPath}'" + (sourceFound == null ? " (from archive)" : ""));
            }
            var streamingDestPath = streamingDestBase + destExt;
            var streamingUpToDate = File.Exists(streamingDestPath) && new FileInfo(streamingDestPath).Length == sourceLength;
            if (!streamingUpToDate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(streamingDestPath));
                if (sourceFound != null) File.Copy(sourceFound, streamingDestPath, overwrite: true);
                else File.WriteAllBytes(streamingDestPath, archiveBytes);
            }
            if (resourcesUpToDate && streamingUpToDate) { alreadyPresent++; continue; }
            copied++;
            if (sourceFound == null) copiedFromArchive++;
        }

        Debug.Log($"[SC2TerrainBdfImporter] textures: copied {copied} ({copiedFromArchive} of those from an archive), already present {alreadyPresent}, " +
                  $"not found {missing}. Destination folder: '{destRoot}'.");

#if UNITY_EDITOR
        if (copied > 0)
        {
            UnityEditor.AssetDatabase.Refresh();
            RegenerateTextureManifest();
        }
#else
        if (copied > 0)
            Debug.LogWarning("[SC2TerrainBdfImporter] Copied textures are only available when running from the Editor " +
                              "(the Editor/Play mode imports them immediately) — in an already-built Build & Run player, " +
                              "the Resources folder's contents are fixed at build time, and files added after " +
                              "the build won't be seen by Resources.Load. For a finished build, textures must already be under " +
                              $"Resources/{SC2TerrainMaterialFactory.ResourcesTextureRoot} at the time the game is built.");
#endif
    }

    /// <summary>
    /// Rebuilds Resources/SC2TextureManifest.txt — one Resources-relative
    /// path (extension stripped the same way Resources.Load itself strips
    /// it — only the FINAL ".dds"/".png"/".tga", so "1_d.win.dds" becomes
    /// "SC2Textures/.../1_d.win") per line, for every texture currently
    /// under Resources/SC2Textures.
    ///
    /// This is the only way SC2TextureManifest (Scripts/SC2TextureManifest.cs)
    /// can do a reverse lookup (Texture2D -&gt; path) at runtime:
    /// Resources.LoadAll/FindObjectsOfTypeAll return bare objects with no
    /// path info at all, and Unity exposes no runtime API to list Resources
    /// folder contents directly — the manifest is a build-time-baked stand-in
    /// for that missing capability. It matters here specifically because this
    /// project's actual texture library has thousands of leaf-name collisions
    /// (numbered diffuse/normal/specular sets like "1_d"/"2_d" repeat across
    /// many different material subfolders by the source game's own naming
    /// convention — confirmed genuinely different content, not harmless
    /// duplicate copies) that a bare-name search can't tell apart.
    ///
    /// Called automatically after copying new textures (above); also exposed
    /// as a manual Editor menu entry (see SC2TextureManifestMenu) for a
    /// one-time backfill over textures that were already copied in before
    /// this manifest existed.
    /// </summary>
    public static void RegenerateTextureManifest()
    {
        var resourcesRoot = Path.Combine(Application.dataPath, "SC2", "Resources");
        var texturesRoot = Path.Combine(resourcesRoot,
            SC2TerrainMaterialFactory.ResourcesTextureRoot.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(texturesRoot))
            return;

        var knownExtensions = new[] { ".dds", ".png", ".tga" };
        var paths = new List<string>();
        foreach (var file in Directory.EnumerateFiles(texturesRoot, "*.*", SearchOption.AllDirectories))
        {
            var ext = Path.GetExtension(file);
            var isKnown = false;
            foreach (var known in knownExtensions)
                if (string.Equals(ext, known, System.StringComparison.OrdinalIgnoreCase)) { isKnown = true; break; }
            if (!isKnown)
                continue;

            var relativeToResources = Path.GetRelativePath(resourcesRoot, file).Replace(Path.DirectorySeparatorChar, '/');
            paths.Add(relativeToResources[..^ext.Length]);
        }

        paths.Sort(System.StringComparer.Ordinal);
        var manifestPath = Path.Combine(resourcesRoot, "SC2TextureManifest.txt");
        File.WriteAllLines(manifestPath, paths);
        Debug.Log($"[SC2TerrainBdfImporter] Texture manifest rebuilt: {paths.Count} entries -> '{manifestPath}'.");

#if UNITY_EDITOR
        UnityEditor.AssetDatabase.Refresh();
#endif
    }

    /// <summary>
    /// Walks up from the .bdf's directory looking for a sibling "Textures"
    /// folder — the vanilla SC2 game-data layout is
    /// &lt;gameRoot&gt;/maps/&lt;mapName&gt;/&lt;mapName&gt;.terrain.win.bdf next to
    /// &lt;gameRoot&gt;/Textures/, matching exactly what SC2TerrainExporter
    /// writes on export.
    /// </summary>
    private static string FindGameRoot(string bdfPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(bdfPath));
        for (var i = 0; i < 6 && dir != null; i++)
        {
            if (Directory.Exists(Path.Combine(dir, "Textures")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    /// <summary>
    /// Every texture path a model's materials actually reference (Diffuse1/2,
    /// Normal1/2, Mask1/2, Ramp, plus RNM mask base expanded into _0/_1/_2) —
    /// reused by SC2TerrainExporter.MergeImportedTerrain to copy the same
    /// texture files into the export output, not just here at import time.
    /// </summary>
    public static IEnumerable<string> CollectTexturePaths(TerrainModel model)
    {
        var result = new List<string>();
        foreach (var mat in model.Materials)
        {
            if (SC2HfieldProjectionService.IsWaterMaterial(mat)) continue; // separate, shader-aware lookup (SC2WaterMaterialFactory) — not handled here

            foreach (var slot in DirectSlots)
            {
                var p = mat.TexturePaths[slot];
                if (!string.IsNullOrEmpty(p)) result.Add(p);
            }

            var maskBase = mat.TexturePaths[10];
            if (!string.IsNullOrEmpty(maskBase))
            {
                var stripped = SC2TerrainMaterialFactory.StripExtension(SC2TerrainMaterialFactory.NormalizePath(maskBase));
                result.Add(stripped + "_0");
                result.Add(stripped + "_1");
                result.Add(stripped + "_2");
            }
        }
        return result;
    }
}
