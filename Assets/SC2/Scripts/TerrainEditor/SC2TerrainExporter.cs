using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using UnityEngine;
using UnityEditor;
using BdfIO.Core;
using BdfIO.Formats.Terrain;
using BdfIO.Formats.Hfield;
using BdfIO.Formats.Costs;
using BdfIO.Formats.Collision2;
using BdfIO.Formats.Info;
using BdfIO.Formats.MapObjs;
using SC2.MapEditor.Document;
using SC2.MapEditor.SmartExport;

/// <summary>
/// Exports SC2TerrainData to terrain.bdf.
/// Paths follow the real vanilla convention:
///   Textures/<Name>.dds            - diffuse, normal, mask
///   maps/<mapName>/lighting/<PairName>.dds - RNM masks (_0/_1/_2)
///   maps/<mapName>/<mapName>.terrain.win.bdf
/// </summary>
public static class SC2TerrainExporter
{
    // EditorUtility.DisplayDialog is a silent no-op in a standalone build
    // (UnityEditorRuntimeShim's version just returns true, shows nothing) —
    // every error/status message this used to communicate purely through
    // dialogs was completely invisible in Build & Run: an export that threw
    // partway through (e.g. inside the new imported-terrain merge step)
    // looked exactly like "nothing happened", no file, no error, nothing.
    // ExportCorrect now also returns the outcome as a plain string so the
    // caller (SC2ExportPanel) can show it inline in the panel — visible in
    // both the Editor and a build.
    /// <param name="session">Optional map session,
    /// currently consulted ONLY for Collision2.VerticalOffset, as a small
    /// end-to-end validation that Export CAN read from SC2MapDocument
    /// instead of SC2TerrainData for at least one field. Defaults to null so
    /// every existing caller (and any other field this exporter still reads
    /// straight from `data`) keeps behaving exactly as before — this does
    /// NOT make the document authoritative for export as a whole yet, see
    /// the status doc for the known limitation and what a full rollout
    /// still needs.</param>
    /// <param name="minimapDdsBytes">Already-encoded &lt;mapName&gt;.minimap.win.dds
    /// bytes (SC2DdsEncoder.BuildDxt1SingleMip), captured by the caller
    /// (SC2ExportPanel, via SC2MapPreviewCapture) before calling this method
    /// — this exporter never touches a Camera/RenderTexture itself, staying
    /// the same Editor/Build-agnostic, non-MonoBehaviour utility it always
    /// was. Null skips writing the file (every pre-existing caller keeps
    /// behaving exactly as before).</param>
    public static string ExportCorrect(SC2TerrainData data, string outputDir, List<SC2ImportedTerrainExportData> importedTerrains = null, bool debugSaveCostsImages = false, SC2MapSession session = null, bool exportAsArchives = false, byte[] minimapDdsBytes = null)
    {
        if (!data.IsInitialized || data.Pairs.Count == 0)
        {
            EditorUtility.DisplayDialog("Export", "No data.", "OK");
            return "Export failed: no data.";
        }
        SC2Log.Log("=== Export invoked ===");
        var archivePath = GetPerMapDestinationPath(outputDir, data, exportAsArchives);
        var perMapRoot = exportAsArchives ? CreateExportStagingDir() : archivePath;
        try
        {
            var result = DoExport(data, perMapRoot, importedTerrains, debugSaveCostsImages, session, minimapDdsBytes);
            if (exportAsArchives)
                result = PackStagingRootIntoSingleArchive(perMapRoot, archivePath, result);
            SC2Log.Log("=== Export completed ===");
            return result;
        }
        catch (Exception ex)
        {
            EditorUtility.ClearProgressBar();
            SC2Log.LogException(ex);
            EditorUtility.DisplayDialog("Error", ex.Message, "OK");
            return "Export failed: " + ex.Message;
        }
        finally
        {
            SC2Log.Flush(); // guarantee the log survives even if Unity becomes unresponsive right after
            if (exportAsArchives) TryDeleteStagingDir(perMapRoot);
        }
    }

    /// <summary>A real vanilla
    /// install keeps ONE per-map folder/archive named "&lt;something&gt;.scd",
    /// holding maps/, uncompiled/, Textures/ directly at ITS OWN root — NOT
    /// split into several topic-named buckets shared across every map
    /// (confirmed against a real installed gamedata/eclipse.scd: maps/
    /// SC2_D3_Eclipse/, uncompiled/maps/SC2_D3_Eclipse/, and textures/
    /// Terrain/SC2_D3_Eclipse/ all sit directly under that one eclipse.scd
    /// folder). BuildExportArtifacts/DoExport below write into the ordinary
    /// maps/&lt;mapName&gt;/, Textures/, uncompiled/maps/&lt;mapName&gt;/ layout as
    /// if outputDir were the final destination — the two callers here are
    /// what actually points outputDir at "&lt;mapName&gt;.scd" (folder mode) or
    /// a temp staging dir standing in for it (archive mode), then
    /// PackStagingRootIntoSingleArchive zips the WHOLE staging dir into one
    /// real "&lt;mapName&gt;.scd" archive file when the checkbox is on.</summary>
    private static string GetPerMapScdName(SC2TerrainData data) =>
        SanitizeMapName(string.IsNullOrEmpty(data.MapName) ? "map" : data.MapName) + ".scd";

    /// <summary>A folder-mode export and
    /// an archive-mode export of the same map used to target the exact same
    /// path ("outputDir/&lt;mapName&gt;.scd") — a real, previously-authored
    /// "&lt;mapName&gt;.scd" FOLDER (from a prior folder-mode export) sitting at
    /// that path made a later archive-mode export fail outright, since
    /// ZipFile.Open can't create a file where a directory of the same name
    /// already exists (and vice versa the other direction). Archive-mode
    /// output now goes into its own "Archives" subfolder instead, so a
    /// folder export and an archive export of the same map can coexist in
    /// the same output directory without ever colliding.</summary>
    private static string GetPerMapDestinationPath(string outputDir, SC2TerrainData data, bool exportAsArchives)
    {
        var scdName = GetPerMapScdName(data);
        return exportAsArchives ? Path.Combine(outputDir, "Archives", scdName) : Path.Combine(outputDir, scdName);
    }

    private static string CreateExportStagingDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "SC2ExportStaging_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDeleteStagingDir(string stagingRoot)
    {
        try { if (Directory.Exists(stagingRoot)) Directory.Delete(stagingRoot, recursive: true); }
        catch (Exception ex) { SC2Log.LogWarning($"[SC2Exporter] Could not clean up export staging folder '{stagingRoot}': {ex.Message}"); }
    }

    /// <summary>Zips everything under stagingRoot into ONE real archive file
    /// at archiveFilePath (overwriting any previous archive there), entries
    /// relative to stagingRoot itself (e.g. "maps/&lt;Name&gt;/&lt;Name&gt;.terrain.win.bdf"
    /// sits at the archive's own root, matching a real per-map .scd's own
    /// internal layout). sc2_export_report.md (Smart Export only) is
    /// deliberately excluded from the archive and copied out as a sibling
    /// file next to it instead — it's this editor's own diagnostic output,
    /// not part of the real game's .scd format. Returns resultMessage
    /// unchanged — this only repackages what DoExport/ExportCorrectSmart
    /// already wrote, it doesn't change the outcome text.</summary>
    private static string PackStagingRootIntoSingleArchive(string stagingRoot, string archiveFilePath, string resultMessage)
    {
        const string reportFileName = "sc2_export_report.md";
        var archiveDir = Path.GetDirectoryName(archiveFilePath);
        if (!string.IsNullOrEmpty(archiveDir)) Directory.CreateDirectory(archiveDir);
        if (File.Exists(archiveFilePath)) File.Delete(archiveFilePath);

        using (var archive = ZipFile.Open(archiveFilePath, ZipArchiveMode.Create))
        {
            foreach (var file in Directory.EnumerateFiles(stagingRoot, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(stagingRoot, file).Replace('\\', '/');
                if (string.Equals(relative, reportFileName, StringComparison.OrdinalIgnoreCase)) continue;
                archive.CreateEntryFromFile(file, relative, System.IO.Compression.CompressionLevel.Optimal);
            }
        }

        var reportPath = Path.Combine(stagingRoot, reportFileName);
        if (File.Exists(reportPath) && !string.IsNullOrEmpty(archiveDir))
            File.Copy(reportPath, Path.Combine(archiveDir, reportFileName), overwrite: true);

        return resultMessage;
    }

    /// <summary>Pure artifact-building result shared by
    /// both the legacy Export button (DoExport, writes each file directly to
    /// disk, unconditionally, unchanged since before this stage) and the new
    /// additive "Smart Export (new)" button (ExportCorrectSmart, routes the
    /// same bytes through SC2SmartExportPlanner/SC2SmartExportWriter
    /// instead). Extracting this was the only way to make the two buttons'
    /// outputs byte-IDENTICAL by construction (one piece of code produces
    /// the bytes) rather than by trusting two independently-written
    /// implementations to agree. ErrorMessage != null means every other
    /// field is unset/meaningless and the caller should return it
    /// immediately — matching every early-return the pre-refactor DoExport
    /// had (this method still shows the same dialogs at each of those
    /// points, since both buttons should surface the same guard failures).</summary>
    private sealed class SC2ExportArtifacts
    {
        public string ErrorMessage;
        public string MapName;
        public string MapDir;
        public byte[] TerrainBytes;
        public byte[] HfieldBytes;
        public byte[] CostsBytes;
        public byte[] Collision2Bytes;
        public byte[] InfoBytes;
        public byte[] MapObjsBytes;
        public byte[] DecalsBytes;
        public byte[] DecalDiffuseAtlasBytes;
        public byte[] DecalNormalAtlasBytes;
        public string SaveLuaText;
        public string ScenarioLuaText;
        public string ScriptLuaText;
        public int LayersCount;
        public int VerticesCount;
        public int TexturesCopied;
        public List<string> Warnings;
        public float Collision2HeightOffset;
        public string Collision2OffsetSource;
    }

    private static SC2ExportArtifacts BuildExportArtifacts(SC2TerrainData data, string outputDir, List<SC2ImportedTerrainExportData> importedTerrains, bool debugSaveCostsImages, SC2MapSession session)
    {
        SC2Log.Log($"[SC2Exporter] Export started. Pairs={data.Pairs.Count}, " +
                   $"TrianglePairIndex.Length={data.TrianglePairIndex?.Length ?? -1}");
        SC2Log.Log("[SC2Exporter] InvalidateGeometryCache()...");
        var swStage = System.Diagnostics.Stopwatch.StartNew();
        // Invalidate cached Vertices/Triangles so they rebuild from current Heights[]
        data.InvalidateGeometryCache();
        SC2Log.Log($"[SC2Exporter] InvalidateGeometryCache() done in {swStage.ElapsedMilliseconds}ms");
        if (data.TrianglePairIndex == null || data.TrianglePairIndex.Length == 0)
        {
            SC2Log.LogWarning("[SC2Exporter] Aborted: TrianglePairIndex is empty.");
            UnityEditor.EditorUtility.DisplayDialog("Export",
                "TrianglePairIndex is empty — go to Paint mode and paint pairs before exporting.", "OK");
            return new SC2ExportArtifacts { ErrorMessage = "Export failed: no pairs painted yet." };
        }
        var rawMapName  = string.IsNullOrEmpty(data.MapName) ? "map" : data.MapName;
        var mapName     = SanitizeMapName(rawMapName);
        if (!string.Equals(rawMapName, mapName, StringComparison.Ordinal))
            SC2Log.LogWarning($"[SC2Exporter] MapName sanitized: '{rawMapName}' -> '{mapName}'");
        // Paths follow the real vanilla convention. outputDir here is already the per-map root
        // (either the real "<mapName>.scd" folder, or a temp staging dir
        // standing in for it — see CreateExportStagingDir's doc comment on
        // ExportCorrect/ExportCorrectSmart), confirmed against a real
        // installed per-map .scd (gamedata/eclipse.scd: maps/, textures/,
        // uncompiled/ all sitting directly at that ONE folder's root) — so
        // no further "type" subfolder wrapping belongs here.
        var mapDir      = Path.Combine(outputDir, "maps", mapName);
        var texDir      = Path.Combine(outputDir, "Textures"); // /Textures/Name.dds
        var lightingDir = Path.Combine(mapDir, "lighting");          // /maps/<mapName>/lighting/
        Directory.CreateDirectory(mapDir);
        Directory.CreateDirectory(texDir);
        Directory.CreateDirectory(lightingDir);

        // Texture registry: Texture2D -> BDF path
        var texReg   = new Dictionary<Texture2D, string>();
        // Which Texture2D currently owns each OUTPUT filename — the base name
        // alone (AssetDatabase file name in the Editor, tex.name in a build)
        // is not guaranteed unique: two genuinely different textures living in
        // different source folders can share the same leaf name (e.g. a
        // "diffuse.dds" under both a Grass/ and a Rock/ material folder).
        // Without this check, the second one's File.Copy/TryExportAsDds
        // silently overwrote the first at the same destination path — both
        // materials' registry entries kept pointing at the same file, so
        // whichever texture "lost" the race had its bytes replaced by the
        // other's in the exported map. Detected and disambiguated with a
        // numeric suffix instead, with a warning surfaced in the export
        // result so the underlying naming collision in the source project is
        // visible, not just silently patched around.
        var fileNameOwner = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
        var copied   = 0;
        var warnings = new List<string>();

        string UniqueFileName(string desiredFileName, Texture2D tex)
        {
            if (!fileNameOwner.TryGetValue(desiredFileName, out var owner) || owner == tex)
            {
                fileNameOwner[desiredFileName] = tex;
                return desiredFileName;
            }

            var ext = Path.GetExtension(desiredFileName);
            var stem = desiredFileName[..^ext.Length];
            var suffix = 2;
            string candidate;
            do
            {
                candidate = $"{stem}_{suffix}{ext}";
                suffix++;
            } while (fileNameOwner.TryGetValue(candidate, out var candidateOwner) && candidateOwner != tex);

            warnings.Add($"Duplicate texture file name '{desiredFileName}' — two different textures share this name; exported the second as '{candidate}' instead of overwriting the first.");
            fileNameOwner[candidate] = tex;
            return candidate;
        }

        // Resolves a raw resource-path-convention
        // string ("file:<local path>" or "archive:<entry path>") into a
        // real, exported, byte-correct game path — by actually copying the
        // referenced bytes into this export's Textures folder, same as the
        // existing "file:" handling below already did. Returns null for
        // anything that ISN'T one of these two markers (a plain string is
        // assumed to already be a real, manually-typed in-game path — the
        // original, still-supported use case for DiffuseOverridePath etc.).
        // Keyed by the raw path itself (not by Texture2D, which may be null
        // for an override-only reference), so two materials referencing the
        // same archive entry only copy it once.
        var rawPathReg = new Dictionary<string, string>(StringComparer.Ordinal);
        // forceExport: the per-texture "copy into map on export" opt-in. Only
        // meaningful for an "archive:" marker — a texture owned by
        // textures.scd/z_dlc1.scd/z_dlc1_map_shared.scd is otherwise
        // referenced by its real game path with no copy
        // (SC2VanillaArchiveClassifier); a "file:" custom import and any
        // non-vanilla archive always copy.
        string ResolveRawResourcePath(string rawPath, Texture2D tex, bool forceExport = false)
        {
            if (string.IsNullOrEmpty(rawPath)) return null;
            // rawPathReg is keyed by the raw path alone. If an earlier slot
            // already resolved this entry to its bare game path (no copy) and
            // THIS slot asked for a real copy, drop the cached no-copy result
            // so the copy below actually happens.
            if (forceExport && rawPathReg.TryGetValue(rawPath, out var noCopy) && noCopy != null
                && !noCopy.StartsWith("/Textures/", StringComparison.Ordinal))
                rawPathReg.Remove(rawPath);
            if (rawPathReg.TryGetValue(rawPath, out var cached)) return cached;

            if (rawPath.StartsWith("file:", StringComparison.Ordinal))
            {
                var sourceFile = rawPath.Substring(5);
                if (!File.Exists(sourceFile)) return null;
                var customFileName = UniqueFileName(MakeSafe(Path.GetFileNameWithoutExtension(sourceFile)) + ".dds", tex);
                var customDestination = Path.Combine(texDir, customFileName);
                try { File.Copy(sourceFile, customDestination, true); copied++; }
                catch (Exception ex) { warnings.Add($"{sourceFile}: {ex.Message}"); return null; }
                return rawPathReg[rawPath] = $"/Textures/{customFileName}";
            }
            if (rawPath.StartsWith("archive:", StringComparison.Ordinal))
            {
                var entryPath = rawPath.Substring("archive:".Length);
                if (!SC2GameArchiveRegistry.TryGetReader(out var reader))
                {
                    warnings.Add($"{entryPath}: game archive not configured, cannot resolve for export.");
                    return null;
                }
                // Real archive filenames carry the platform infix
                // ("..._d.win.dds"), but no correctly-working texture path
                // anywhere in a real terrain.bdf ever does — confirmed
                // against this same file's own already-correct Water
                // material paths (/textures/Terrain/Water/foam_001.dds,
                // no ".win") — strip it here too, same as the
                // AssetDatabase/tex.name fallback below already does for
                // its own source filenames.
                // Vanilla-archive texture: the game already ships it, so the
                // exported bdf just needs its real path — no copy into
                // Textures/ (unless this texture's own "copy into map on
                // export" box is checked).
                if (SC2VanillaArchiveClassifier.ShouldSkipCopy(reader, entryPath, forceExport))
                    return rawPathReg[rawPath] = SC2VanillaArchiveClassifier.ToGamePath(entryPath);
                var bn = Path.GetFileNameWithoutExtension(entryPath);
                if (bn.EndsWith(".win", StringComparison.OrdinalIgnoreCase)) bn = bn[..^4];
                var customFileName = UniqueFileName(MakeSafe(bn) + ".dds", tex);
                var customDestination = Path.Combine(texDir, customFileName);
                try
                {
                    using var stream = reader.OpenEntry(entryPath);
                    using var outFile = File.Create(customDestination);
                    stream.CopyTo(outFile);
                    copied++;
                }
                catch (Exception ex) { warnings.Add($"{entryPath}: {ex.Message}"); return null; }
                return rawPathReg[rawPath] = $"/Textures/{customFileName}";
            }
            return null;
        }

        string RegTex(Texture2D tex, string overridePath = null, bool forceExport = false)
        {
            // A non-empty per-slot override path (SC2Material.
            // DiffuseOverridePath etc.) wins, regardless of whether "tex" is
            // null or locally resolvable — this is what lets the user point
            // at a real in-game path (e.g. a vanilla texture never extracted
            // into this project) that exports byte-correctly even though the
            // editor can't preview it.
            // It used to be returned VERBATIM here, no matter
            // what it contained — but the Texture Browser's "pick from game
            // archive" flow also writes into this same override-path field,
            // using its own internal loader syntax ("archive:<entryPath>")
            // rather than a real game path, since it's the same field that
            // otherwise holds a real manually-typed path. That internal
            // marker then leaked straight into the exported terrain.bdf,
            // which the real game engine has no idea how to resolve,
            // causing the map to fail to load. Route it
            // through the same resolver a plain texture reference already
            // uses below, and only fall back to verbatim pass-through when
            // it's neither marker (a real path, the original use case).
            if (!string.IsNullOrEmpty(overridePath))
            {
                var resolvedOverride = ResolveRawResourcePath(overridePath, tex, forceExport);
                return resolvedOverride ?? overridePath;
            }
            if (tex == null) return null;
            // A cached no-copy (bare game path) result must not satisfy a slot
            // that opted into a real copy.
            if (texReg.TryGetValue(tex, out var p) && (!forceExport || p == null || p.StartsWith("/Textures/", StringComparison.Ordinal))) return p;

            // NOTE: this used to short-circuit here for any texture
            // the catalog reported as SC2TextureSource.Vanilla — it wrote the
            // bare game path and copied nothing, on the assumption that the game
            // already ships that file. That assumption is unsafe: SC2TextureCatalog.
            // LoadLegacyManifest marks EVERY manifest entry as Vanilla, and
            // Resources/SC2Textures is also where ScdImportWindow.CopyTexturesFromScd
            // deposits textures extracted from an IMPORTED MAP's own .scd. Those are
            // map-local, not base-game: e.g. an imported SC2_D3_crab contributes
            // "Textures/Terrain/SC2_D3_CryMap01/Ice_s", and no SC2_D3_* folder
            // exists in the game at all. The exported material then referenced a
            // file that was never written anywhere, the engine fell back to its
            // default texture, and for a MASK that fallback reads as a high
            // specular level (maskColor.b ~ 1) instead of the intended value —
            // producing strong specular flares on slopes. It only reproduced with
            // a mask assigned AND specular enabled, because with no mask this
            // exporter writes /textures/Engine/black_d.dds, which really does
            // exist and is black (specular level 0).
            //
            // Since nothing here can reliably tell a base-game texture from a
            // map-local one without a configured game install path, every texture
            // is now copied into the export's Textures folder. Redundant for real
            // base-game art, but it makes the export self-contained and removes a
            // whole class of silently-broken references.
            // The "file:" (and now "archive:") case above already
            // handles this via ResolveRawResourcePath — this used to be a
            // second, separate copy of the same "file:" logic right here;
            // removed rather than kept as an unreachable duplicate.
            var registeredPath = SC2TerrainMaterialFactory.TryGetResourcePath(tex);
            var resolvedRegistered = ResolveRawResourcePath(registeredPath, tex, forceExport);
            if (resolvedRegistered != null) { texReg[tex] = resolvedRegistered; return resolvedRegistered; }

#if UNITY_EDITOR
            var ap  = AssetDatabase.GetAssetPath(tex);
            if (string.IsNullOrEmpty(ap)) { warnings.Add($"{tex.name}: not saved as an Asset"); return null; }
            // Check the DDS magic
            ValidateDds(ap, warnings);
            var bn  = Path.GetFileNameWithoutExtension(ap);
            if (bn.EndsWith(".win", StringComparison.OrdinalIgnoreCase)) bn = bn[..^4];
            var fn  = UniqueFileName(bn + ".dds", tex);
            var dst = Path.Combine(texDir, fn);
            try { File.Copy(ap, dst, overwrite: true); copied++; }
            catch (Exception ex) { warnings.Add($"{ap}: {ex.Message}"); return null; }
#else
            // No AssetDatabase in a standalone build (AssetDatabase.GetAssetPath
            // always returns "" there — see UnityEditorRuntimeShim), so there is
            // no source file path to copy. Re-encode the texture's live pixel
            // data directly to DDS instead — see SC2RuntimeTextureExporter.
            // Resources-loaded terrain textures are named after the on-disk file,
            // which carries the platform suffix ("1_d.win"). Strip it before
            // sanitizing, otherwise MakeSafe turns the dot into an underscore and
            // the export ends up with "1_d_win.dds" — same file, needlessly
            // divergent from the name the Editor path produces.
            var bn  = tex.name;
            if (bn.EndsWith(".win", StringComparison.OrdinalIgnoreCase)) bn = bn[..^4];
            bn  = MakeSafe(bn);
            var fn  = UniqueFileName(bn + ".dds", tex);
            var dst = Path.Combine(texDir, fn);
            if (!SC2RuntimeTextureExporter.TryExportAsDds(tex, dst, out var err))
            {
                warnings.Add(err);
                return null;
            }
            copied++;
#endif
            // Forward slash, not a backslash
            // — the game reads this as a real path string, and a literal
            // backslash inside it isn't a valid path separator there.
            texReg[tex] = $"/Textures/{fn}";
            return texReg[tex];
        }

        var model = new TerrainModel { Version = 6 };

        SC2Log.Log("[SC2Exporter] Building simplified export layers...");
        var swBuild = System.Diagnostics.Stopwatch.StartNew();
        // The map's own true outer perimeter must always survive
        // simplification intact — vertex-clustering decimation has no
        // inherent concept of "this is the map edge, don't touch it," only
        // of painted pair-to-pair boundaries (SC2TerrainSimplifier.
        // FindBoundaryVertices). Whenever the pair touching the map edge
        // has SimplifyRatio > 0 and that edge isn't ALSO a pair boundary
        // (the common case: one pair usually covers the whole border), the
        // outer ring had nothing locking it in place and could be
        // collapsed/removed like any interior vertex — confirmed real:
        // edge polygons could disappear entirely. Previously only computed
        // when BackgroundTerrainEnabled (that feature separately needs this
        // exact ring for its own seam height-copy), which left every other
        // map's edge unprotected; now unconditional; a map whose outer pair
        // ring is already fully raw (SimplifyRatio 0) sees no change since
        // there's nothing for it to protect there in the first place.
        var coreOuterLock = SC2TerrainSimplifier.FindOuterPerimeterVertices(data);
        var build = SC2TerrainLayerBuilder.Build(data, sortByTriangleCount: true, (stage, progress) =>
        {
            SC2Log.Log($"[SC2Exporter] Build stage: {stage}");
            EditorUtility.DisplayProgressBar("Export", stage, Mathf.Clamp01(progress * 0.75f));
        }, coreOuterLock);
        SC2Log.Log($"[SC2Exporter] Build simplified export layers done in {swBuild.ElapsedMilliseconds}ms");
        SC2Log.Log($"[SC2Exporter] {build.Layers.Count} pair(s) have assigned triangles. " +
                   $"Counts: {string.Join(", ", build.PairTriangleCounts)}");
        SC2Log.Log($"[SC2Exporter] Build stats: source={build.SourceTriangleCount:N0}, " +
                   $"built={build.BuiltTriangleCount:N0}, vertices={build.BuiltVertexCount:N0}, " +
                   $"locked={build.LockedVertexCount:N0}, components={build.ComponentCount:N0}, " +
                   $"rawComponents={build.SkippedComponentCount:N0}, rejected={build.ProblemTriangleCount:N0}");

        // ImportOnlyMode legitimately builds zero live layers
        // by design — MergeImportedTerrain hasn't run yet at this point in the
        // function, so "0 live layers" must NOT be treated as a failure when
        // there's imported terrain waiting to be folded in below. Only abort
        // when there is truly nothing to export either way.
        if (build.Layers.Count == 0 && (importedTerrains == null || importedTerrains.Count == 0))
        {
            SC2Log.LogWarning("[SC2Exporter] Aborted: no pair has any assigned triangles " +
                "(all TrianglePairIndex values are -1/unassigned, or stale from a previous session " +
                "after an editor crash) and no imported terrain is present. Paint pairs again, or " +
                "trigger a sync (switch to Paint mode) before exporting.");
            EditorUtility.DisplayDialog("Export",
                "No triangles are assigned to any pair and no terrain.bdf is imported — nothing to export.\n" +
                "Go to Paint mode and assign pairs, or import a terrain.bdf, then try again.", "OK");
            return new SC2ExportArtifacts { ErrorMessage = "Export failed: no triangles are assigned to any pair, and no imported terrain is present." };
        }

        if (build.BuiltTriangleCount > 350_000)
        {
            var continueHuge = EditorUtility.DisplayDialog(
                "Export",
                $"Export mesh still has {build.BuiltTriangleCount:N0} triangles after simplification.\n" +
                "BSP rebuild can take a long time and make Unity look frozen.\n\n" +
                "Continue anyway?",
                "Continue",
                "Cancel");
            if (!continueHuge)
            {
                SC2Log.LogWarning($"[SC2Exporter] Cancelled: built triangle count is {build.BuiltTriangleCount:N0}.");
                return new SC2ExportArtifacts { ErrorMessage = "Export cancelled: too many triangles." };
            }
        }

        // Cache: maskName → bdf path (export each unique mask file once)
        var rnmCache = new System.Collections.Generic.Dictionary<string, string>();

        // Built ONCE for the whole core terrain (not per pair — every pair
        // shares the same geometry, only its own UV1 tiling scale differs,
        // applied at Sample's call site below). See SC2TerrainUv1Unwrap's
        // own doc comment for why this replaces a flat pos.x/pos.z planar
        // projection, and why it's a per-AXIS profile (not a 2D grid) —
        // that's what keeps it safe from flipping triangle winding.
        SC2TerrainUv1Unwrap.BuildProfiles(data.Heights, data.GridWidth, data.GridHeight, data.CellSize,
            out var coreUv1ProfileU, out var coreUv1ProfileV, data.Uv1HeightWeight);

        // Core's own real world footprint for Uv2 (RNM/blend-mask lookup) —
        // MUST match SC2TerrainRuntime.RebuildChunk's own uv2arr convention
        // exactly (gx/GridWidth, gz/GridHeight — i.e. pos.x/coreSpanX here,
        // since pos.x = gx*CellSize), or a painted mask lands at a
        // different spot in-game than what the live preview showed. Was
        // hardcoded pos.x/1024f, which only ever happened to be correct for
        // an exactly-1024-unit map — any other size (this codebase already
        // explicitly supports 512/2048/etc., see hfieldMapX's own comment
        // below) sampled the wrong, too-small fraction of the mask, e.g. a
        // 512 map only ever reads its mask's own bottom-left QUARTER.
        var coreSpanX = Mathf.Max(1f, data.GridWidth * data.CellSize);
        var coreSpanZ = Mathf.Max(1f, data.GridHeight * data.CellSize);

        for (var si = 0; si < build.Layers.Count; si++)
        {
            var builtLayer = build.Layers[si];
            var pi   = builtLayer.PairIndex;
            var pair = data.Pairs[pi];
            EditorUtility.DisplayProgressBar("Export", $"Pair {si+1}/{build.Layers.Count}: {pair.Name}",
                (float)si / build.Layers.Count * 0.75f);

            var s = builtLayer.Mesh;
            SC2Log.Log($"[SC2Exporter] Pair {si+1}/{build.Layers.Count} '{pair.Name}': " +
                       $"{builtLayer.SourceTriangleCount:N0} -> {s.Triangles.Length/3:N0} tris, " +
                       $"ratio={pair.SimplifyRatio:F2}, components={s.ComponentCount}, raw={s.SkippedComponentCount}, " +
                       $"rejected={s.ProblemTriangleCount}");

            var layer = new BdfIO.Formats.Terrain.TerrainLayer
                { CumulativeVertices=model.Vertices.Count, CumulativeIndices=model.Triangles.Count*3 };
            model.Layers.Add(layer);

            // GlobalUvScale (when enabled) supersedes this pair's own
            // TilingScale — same shared resolver the live GPU preview LUT
            // and the background's own export/preview use, so the exported
            // file can never disagree with what was actually shown while
            // editing.
            var uv1Scale = data.EffectiveUvTilingScale(pair);
            var uv1U = new float[s.Vertices.Length];
            var uv1V = new float[s.Vertices.Length];
            for (var vi = 0; vi < s.Vertices.Length; vi++)
            {
                var uv = SC2TerrainUv1Unwrap.Sample(coreUv1ProfileU, coreUv1ProfileV, data.GridWidth, data.GridHeight, data.CellSize,
                    s.Vertices[vi].x, s.Vertices[vi].z);
                uv1U[vi] = uv.x / uv1Scale; uv1V[vi] = uv.y / uv1Scale;
            }
            var (layerTangents, layerBinormals) = BuildUv1Tbn(s.Vertices, s.Triangles, s.Normals, uv1U, uv1V);
            for (var vi = 0; vi < s.Vertices.Length; vi++)
            {
                var pos = s.Vertices[vi]; var n = s.Normals[vi];
                var tangent = layerTangents[vi];
                var binormal = layerBinormals[vi];
                model.Vertices.Add(new TerrainVertex
                {
                    PosX=pos.x, PosY=pos.y, PosZ=pos.z,
                    Uv1U=uv1U[vi], Uv1V=uv1V[vi],
                    Uv2U=pos.x/coreSpanX, Uv2V=pos.z/coreSpanZ,
                    NormalX=n.x, NormalY=n.y, NormalZ=n.z,
                    TangentX=tangent.x, TangentY=tangent.y, TangentZ=tangent.z,
                    BinormalX=binormal.x, BinormalY=binormal.y, BinormalZ=binormal.z,
                    // HasPackedTbn stays false — TerrainBdf.Serialize packs these
                    // three vectors into the real 10:10:10 layout the game reads.
                });
            }
            // model.Triangles vertex indices are LOCAL to their own layer
            // (0-based from layer.CumulativeVertices), matching the real
            // terrain.bdf format (TerrainBdf.Parse/Serialize both read/write
            // them raw with no per-layer offset) and every other consumer in
            // this codebase (MergeImportedTerrain keeps imported triangles
            // as-is for the same reason; SC2HfieldProjectionService.Project
            // and EdbTreeBuilder.Build both explicitly add
            // layer.CumulativeVertices themselves when resolving a triangle's
            // actual position). A prior revision of this loop added
            // layer.CumulativeVertices here to make indices GLOBAL — that
            // broke exactly this shared invariant: EdbTreeBuilder.Build's
            // localVerts[] is sized to just this layer's vertCount, so a
            // global index for any layer past the first threw
            // IndexOutOfRangeException inside RebuildAllBsp (confirmed via
            // the exported build's SC2TerrainEditor.log stack trace).
            // s.Triangles[i] is already the correct local index.
            for (var i = 0; i < s.Triangles.Length; i += 3)
                model.Triangles.Add(new TerrainTriangle
                    { V0=(uint)s.Triangles[i], V1=(uint)s.Triangles[i+1], V2=(uint)s.Triangles[i+2] });

            // Material
            var m1 = pair.Mat1Index >= 0 && pair.Mat1Index < data.Materials.Count ? data.Materials[pair.Mat1Index] : null;
            var m2 = pair.Mat2Index >= 0 && pair.Mat2Index < data.Materials.Count ? data.Materials[pair.Mat2Index] : null;

            var mat = new TerrainMaterial { Name = pair.Name };
            mat.TexturePaths[0]  = RegTex(m1?.Diffuse, m1?.DiffuseOverridePath, m1?.DiffuseForceExport ?? false);
            mat.TexturePaths[1]  = RegTex(m2?.Diffuse, m2?.DiffuseOverridePath, m2?.DiffuseForceExport ?? false);
            mat.TexturePaths[2]  = RegTex(m1?.Normal, m1?.NormalOverridePath, m1?.NormalForceExport ?? false);
            mat.TexturePaths[3]  = RegTex(m2?.Normal, m2?.NormalOverridePath, m2?.NormalForceExport ?? false);
            mat.TexturePaths[6]  = RegTex(m1?.Mask, m1?.MaskOverridePath, m1?.MaskForceExport ?? false) ?? "/textures/Engine/black_d.dds";
            mat.TexturePaths[7]  = RegTex(m2?.Mask, m2?.MaskOverridePath, m2?.MaskForceExport ?? false) ?? "/textures/Engine/black_d.dds";
            mat.TexturePaths[10] = ExportRnmMaskByName(pair.RnmMaskName, data, mapName, lightingDir, rnmCache);

            // Uses the flags from the pair's own settings (set in Pairs -> Shader Setup).
            // ALPHA_TEST's real stored value is its numeric threshold (56-map
            // corpus: observed 4..128), not "1" like every other flag — every
            // other flag's write-then-rebuild-from-fixed-list shape (never
            // append) keeps this safe from duplicate ShaderSetup entries even
            // once Presets (below) can overwrite a pair's flag values.
            if (pair.ShaderFlags != null && pair.ShaderFlags.Count > 0)
                foreach (var flag in pair.ShaderFlags)
                {
                    if (!flag.Value) continue;
                    var value = flag.Key == "ALPHA_TEST"
                        ? Mathf.RoundToInt(pair.AlphaTestThreshold).ToString()
                        : "1";
                    mat.ShaderSetup.Add(new ShaderSetupEntry(flag.Key, value));
                }
            else // fallback: standard set
                foreach (var (k,v) in new[]{
                    ("AMBIENT_LIGHTING","1"),("DIFFUSE_LIGHTING","1"),("SPECULAR_LIGHTING","1"),
                    ("RNM_LIGHTING","1"),("BLENDING","1"),("FOG","1"),
                    ("DYNAMIC_SHADOWS","1"),("REFLECT","1"),("CHROMATIC_DISPERSION","1")})
                    mat.ShaderSetup.Add(new ShaderSetupEntry(k, v));

            // Real TerrainMaterial fields the pair model previously had no
            // home for (56-map corpus survey) — "" State keeps mat.State
            // null (today's existing plain-opaque default). ShaderFile/
            // ShaderPrefix are only ever non-empty after applying the
            // "WATER" material preset (SC2MaterialPresets.cs) — every other
            // preset, and the pre-existing default, leaves both empty,
            // matching how almost every real vanilla material implicitly
            // uses the default terrain shader (56-map survey: only 2/724
            // materials set ShaderFile explicitly, both redundantly to the
            // default terrain.hlsl itself).
            mat.State = string.IsNullOrEmpty(pair.State) ? null : pair.State;
            mat.TextureScroll = new[] { pair.TextureScroll.x, pair.TextureScroll.y };
            if (!string.IsNullOrEmpty(pair.ShaderFile)) mat.ShaderFile = pair.ShaderFile;
            if (!string.IsNullOrEmpty(pair.ShaderPrefix)) mat.ShaderPrefix = pair.ShaderPrefix;

            model.Materials.Add(mat);
        }

        // Fold in every imported terrain.bdf object still in the scene —
        // their original Materials/Layers/Vertices/Triangles (parsed at
        // import time, kept on SC2ImportedTerrainExportData.Model) are
        // copied straight into this same model, transformed by whatever
        // position/rotation/scale the object currently has. Must happen
        // BEFORE RebuildAllBsp so the merged geometry gets real BSP trees
        // too, not just the painted pairs' geometry.
        if (importedTerrains != null)
        {
            foreach (var imported in importedTerrains)
            {
                SC2Log.Log($"[SC2Exporter] merging imported terrain '{imported.Name}': " +
                           $"{imported.Model.Materials.Count} materials, {imported.Model.Vertices.Count} verts, {imported.Model.Triangles.Count} tris");
                MergeImportedTerrain(model, imported, ResolveRawResourcePath);
                CopyImportedTextures(imported.Model, outputDir, warnings, CollectForceExportBasePaths(imported));
            }
        }

        // Decorative background terrain (optional) — a second, low-detail
        // SC2TerrainData surrounding the playable core, generated fresh
        // here and folded into this SAME merged model via the SAME
        // per-pair layer-writing shape as the loop above, just translated
        // into core world space by the background's own local-to-world
        // offset (SC2BackgroundTerrain.BuildForExportOrPreview) instead of
        // an arbitrary GameObject Transform — the background is always
        // generated directly against the core's own coordinate system, so
        // a plain vertex translate is all that's needed. Skipped entirely
        // when the user has substituted their own imported terrain.bdf for
        // this role (IsBackgroundTerrain) — that entry already merged in
        // via MergeImportedTerrain above. Must happen before RebuildAllBsp,
        // same reasoning as the imported-terrain merge above.
        var hasCustomBackground = importedTerrains != null && importedTerrains.Exists(t => t.IsBackgroundTerrain);
        if (data.BackgroundTerrainEnabled && !hasCustomBackground)
        {
            var bgBuild = SC2BackgroundTerrain.BuildForExportOrPreview(data, out var bgOffset);
            var bg = data.BackgroundTerrainData;
            // Same per-axis unwrap as the core loop above, built from the
            // BACKGROUND's own Heights/grid — bg's mesh vertices (s.Vertices
            // below, before +bgOffset) are already in this same background-
            // local frame, so no extra offset math is needed here.
            var bgUv1ProfileU = System.Array.Empty<float>();
            var bgUv1ProfileV = System.Array.Empty<float>();
            if (bg != null)
                SC2TerrainUv1Unwrap.BuildProfiles(bg.Heights, bg.GridWidth, bg.GridHeight, bg.CellSize,
                    out bgUv1ProfileU, out bgUv1ProfileV, data.Uv1HeightWeight);
            // Tracked so the seam skirt below can splice directly into
            // pair 0's own layer/material instead of registering a second
            // one, since it's visually the same decorative surface and there's
            // no reason for two separate material entries in the exported file.
            var pair0LayerIndex = -1;
            // Background-local normalized [0,1] bounds for UV2 (RNM/blend-
            // mask lookup) — MUST match SC2BackgroundTerrainPreview.Ensure's
            // own UV2 computation exactly, or a painted mask lands at a
            // different spot in-game than what the live preview showed.
            // Was hardcoded worldPos/1024f (the core's own convention,
            // fine there since the core's footprint IS ~1024) — wrong for
            // a background whose footprint routinely spans several
            // thousand world units.
            SC2BackgroundTerrain.ComputeBounds(data, data.BackgroundTerrainSize, out var bgMinX, out var bgMinZ, out var bgMaxX, out var bgMaxZ, out _);
            var bgSpanX = Mathf.Max(1f, bgMaxX - bgMinX);
            var bgSpanZ = Mathf.Max(1f, bgMaxZ - bgMinZ);
            if (bg != null && bgBuild.Layers.Count > 0)
            {
                SC2Log.Log($"[SC2Exporter] background terrain: {bgBuild.Layers.Count} layer(s), " +
                           $"{bgBuild.BuiltTriangleCount:N0} tris (offset {bgOffset}).");
                foreach (var builtLayer in bgBuild.Layers)
                {
                    var pi = builtLayer.PairIndex;
                    if (pi < 0 || pi >= bg.Pairs.Count) continue;
                    var pair = bg.Pairs[pi];
                    var s = builtLayer.Mesh;
                    // GlobalUvScale (data.EffectiveUvTilingScale, when
                    // enabled) supersedes this pair's own TilingScale — same
                    // shared resolver used everywhere else (core's live GPU
                    // preview LUT, core's own export loop above, the
                    // background's own live preview) so nothing can
                    // independently disagree about which value won. Falls
                    // back to per-pair TilingScale when the global override
                    // is off, matching the core's own convention and the
                    // background's live preview — was a single map-wide
                    // data.BackgroundTerrainUvScale (default 1024) covering
                    // every background pair uniformly with no relationship
                    // to the core's own tiling at all, producing a visible
                    // tiling-density jump right at the seam; removed.
                    var uvScale = data.EffectiveUvTilingScale(pair);

                    var layer = new BdfIO.Formats.Terrain.TerrainLayer
                        { CumulativeVertices = model.Vertices.Count, CumulativeIndices = model.Triangles.Count * 3 };
                    model.Layers.Add(layer);
                    if (pi == 0) pair0LayerIndex = model.Layers.Count - 1;

                    var bgUv1U = new float[s.Vertices.Length];
                    var bgUv1V = new float[s.Vertices.Length];
                    for (var vi = 0; vi < s.Vertices.Length; vi++)
                    {
                        var localPos = s.Vertices[vi];
                        var uv = SC2TerrainUv1Unwrap.Sample(bgUv1ProfileU, bgUv1ProfileV, bg.GridWidth, bg.GridHeight, bg.CellSize,
                            localPos.x, localPos.z);
                        bgUv1U[vi] = uv.x / uvScale; bgUv1V[vi] = uv.y / uvScale;
                    }
                    var (layerTangents, layerBinormals) = BuildUv1Tbn(s.Vertices, s.Triangles, s.Normals, bgUv1U, bgUv1V);
                    for (var vi = 0; vi < s.Vertices.Length; vi++)
                    {
                        var worldPos = s.Vertices[vi] + bgOffset;
                        var n = s.Normals[vi];
                        var tangent = layerTangents[vi];
                        var binormal = layerBinormals[vi];
                        model.Vertices.Add(new TerrainVertex
                        {
                            PosX = worldPos.x, PosY = worldPos.y, PosZ = worldPos.z,
                            Uv1U = bgUv1U[vi], Uv1V = bgUv1V[vi],
                            Uv2U = (worldPos.x - bgMinX) / bgSpanX, Uv2V = (worldPos.z - bgMinZ) / bgSpanZ,
                            NormalX = n.x, NormalY = n.y, NormalZ = n.z,
                            TangentX = tangent.x, TangentY = tangent.y, TangentZ = tangent.z,
                            BinormalX = binormal.x, BinormalY = binormal.y, BinormalZ = binormal.z,
                        });
                    }
                    for (var i = 0; i < s.Triangles.Length; i += 3)
                        model.Triangles.Add(new TerrainTriangle
                            { V0 = (uint)s.Triangles[i], V1 = (uint)s.Triangles[i + 1], V2 = (uint)s.Triangles[i + 2] });

                    var m1 = pair.Mat1Index >= 0 && pair.Mat1Index < bg.Materials.Count ? bg.Materials[pair.Mat1Index] : null;
                    var m2 = pair.Mat2Index >= 0 && pair.Mat2Index < bg.Materials.Count ? bg.Materials[pair.Mat2Index] : null;
                    var mat = new TerrainMaterial { Name = pair.Name };
                    mat.TexturePaths[0] = RegTex(m1?.Diffuse, m1?.DiffuseOverridePath, m1?.DiffuseForceExport ?? false);
                    mat.TexturePaths[1] = RegTex(m2?.Diffuse, m2?.DiffuseOverridePath, m2?.DiffuseForceExport ?? false);
                    mat.TexturePaths[2] = RegTex(m1?.Normal, m1?.NormalOverridePath, m1?.NormalForceExport ?? false);
                    mat.TexturePaths[3] = RegTex(m2?.Normal, m2?.NormalOverridePath, m2?.NormalForceExport ?? false);
                    mat.TexturePaths[6] = RegTex(m1?.Mask, m1?.MaskOverridePath, m1?.MaskForceExport ?? false) ?? "/textures/Engine/black_d.dds";
                    mat.TexturePaths[7] = RegTex(m2?.Mask, m2?.MaskOverridePath, m2?.MaskForceExport ?? false) ?? "/textures/Engine/black_d.dds";
                    // Real Paint-Blend mask (SC2PaintMode's own "Paint Blend"
                    // tool paints pair.RnmMaskBytes directly) — reuses
                    // ExportRnmMaskByName exactly like the core's own
                    // per-pair loop does, just against bg instead of data
                    // (the function is generic over WHICH SC2TerrainData's
                    // Pairs list it searches).
                    //
                    // "bg_" cacheKeyPrefix is load-bearing, not cosmetic:
                    // the background needs its own separate RNM mask, which
                    // can't be shared with the core area, since both a from-scratch
                    // core pair and EnsureBackgroundPair's own default
                    // background pair use the SAME default RnmMaskName
                    // ("mask01"/"background"), and ExportRnmMaskByName's
                    // cache (rnmCache, shared across this whole export —
                    // both the core's own per-pair loop above and this one
                    // write into it) early-returns the FIRST match for a
                    // given (sanitized) mask name — so whichever of
                    // core/background exports second silently got back the
                    // OTHER one's already-exported file path, and even
                    // without the cache, both would have written to the
                    // SAME lighting/<name>_0.win.dds path on disk.
                    //
                    // Passed as a SEPARATE parameter (not baked into
                    // pair.RnmMaskName itself, as an earlier version of this
                    // fix wrongly did, which caused RNM lighting to bake as
                    // flat gray for the background): that version compared the
                    // prefixed name against every pair's own UNPREFIXED
                    // RnmMaskName inside ExportRnmMaskByName's own pair-
                    // search, which never matched anything, silently falling
                    // back to the neutral 128-gray placeholder for every
                    // background mask). This way the prefix only
                    // disambiguates the cache key/output filename; the pair
                    // search itself still uses the real, unprefixed name.
                    mat.TexturePaths[10] = ExportRnmMaskByName(pair.RnmMaskName, bg, mapName, lightingDir, rnmCache, "bg_");
                    // Same fields the core's own per-pair loop writes
                    // (ALPHA_TEST's real numeric threshold, State,
                    // TextureScroll, ShaderFile/ShaderPrefix) — previously
                    // only ShaderFlags (and ALPHA_TEST as a hardcoded "1",
                    // not its real threshold) were exported for background
                    // pairs, even though the same SC2PairsMode UI already
                    // lets a user set all of these on a background pair.
                    if (pair.ShaderFlags != null && pair.ShaderFlags.Count > 0)
                        foreach (var flag in pair.ShaderFlags)
                        {
                            if (!flag.Value) continue;
                            var value = flag.Key == "ALPHA_TEST"
                                ? Mathf.RoundToInt(pair.AlphaTestThreshold).ToString()
                                : "1";
                            mat.ShaderSetup.Add(new ShaderSetupEntry(flag.Key, value));
                        }
                    mat.State = string.IsNullOrEmpty(pair.State) ? null : pair.State;
                    mat.TextureScroll = new[] { pair.TextureScroll.x, pair.TextureScroll.y };
                    if (!string.IsNullOrEmpty(pair.ShaderFile)) mat.ShaderFile = pair.ShaderFile;
                    if (!string.IsNullOrEmpty(pair.ShaderPrefix)) mat.ShaderPrefix = pair.ShaderPrefix;
                    model.Materials.Add(mat);
                }
            }

            // Seam-stitching skirt (SC2BackgroundTerrain.BuildSeamSkirt) —
            // fills the real crack a coarser-than-core background otherwise
            // leaves along the boundary, caused by the differing mesh
            // density between the two. Already in core world space, no
            // offset needed.
            // Merged directly into pair 0's OWN layer/material — spliced
            // into its vertex/triangle range, every later layer's
            // Cumulative offsets re-based, same technique
            // TerrainBdf.RebuildLayerBsp already uses when a layer's own
            // triangle/vertex count changes in place — rather than
            // registering a second layer+material, since it's visually the
            // same decorative surface, no reason
            // for two separate material entries in the exported file.
            SC2BackgroundTerrain.BuildSeamSkirt(data, out var skirtVerts, out var skirtNormals, out var skirtTris);
            if (skirtTris.Length > 0 && bg != null && pair0LayerIndex >= 0)
            {
                SC2Log.Log($"[SC2Exporter] background seam skirt: {skirtTris.Length / 3:N0} tris (merged into pair 0's own layer).");
                // Same GlobalUvScale-aware resolver the main loop above
                // uses — the skirt is merged into pair 0's own material, so
                // its UV1 must always agree with whatever pair 0 itself
                // resolves to. Uv2 rebased to the SAME [0,1] background-local
                // bounds as the main loop above so a painted RNM/blend mask
                // lines up correctly on skirt triangles too.
                var pair0 = bg.Pairs.Count > 0 ? bg.Pairs[0] : null;
                var uvScale = data.EffectiveUvTilingScale(pair0);
                // Skirt vertices are already in CORE world space (see this
                // block's own comment above), so they sample the core's own
                // coreUv1Profile* built earlier — not the background's.
                var skirtUv1U = new float[skirtVerts.Length];
                var skirtUv1V = new float[skirtVerts.Length];
                for (var vi = 0; vi < skirtVerts.Length; vi++)
                {
                    var uv = SC2TerrainUv1Unwrap.Sample(coreUv1ProfileU, coreUv1ProfileV, data.GridWidth, data.GridHeight, data.CellSize,
                        skirtVerts[vi].x, skirtVerts[vi].z);
                    skirtUv1U[vi] = uv.x / uvScale; skirtUv1V[vi] = uv.y / uvScale;
                }
                var (skirtTangents, skirtBinormals) = BuildUv1Tbn(skirtVerts, skirtTris, skirtNormals, skirtUv1U, skirtUv1V);

                var skirtVertexList = new List<TerrainVertex>(skirtVerts.Length);
                for (var vi = 0; vi < skirtVerts.Length; vi++)
                {
                    var worldPos = skirtVerts[vi];
                    var n = skirtNormals[vi];
                    var tangent = skirtTangents[vi];
                    var binormal = skirtBinormals[vi];
                    skirtVertexList.Add(new TerrainVertex
                    {
                        PosX = worldPos.x, PosY = worldPos.y, PosZ = worldPos.z,
                        Uv1U = skirtUv1U[vi], Uv1V = skirtUv1V[vi],
                        Uv2U = (worldPos.x - bgMinX) / bgSpanX, Uv2V = (worldPos.z - bgMinZ) / bgSpanZ,
                        NormalX = n.x, NormalY = n.y, NormalZ = n.z,
                        TangentX = tangent.x, TangentY = tangent.y, TangentZ = tangent.z,
                        BinormalX = binormal.x, BinormalY = binormal.y, BinormalZ = binormal.z,
                    });
                }

                // Insert right after pair 0's own EXISTING vertices/tris
                // (i.e. right before whatever layer currently follows it,
                // or at the very end if pair 0 is the last layer so far).
                var pair0Layer = model.Layers[pair0LayerIndex];
                var insertVertPos = pair0LayerIndex + 1 < model.Layers.Count
                    ? model.Layers[pair0LayerIndex + 1].CumulativeVertices
                    : model.Vertices.Count;
                var insertTriPos = pair0LayerIndex + 1 < model.Layers.Count
                    ? model.Layers[pair0LayerIndex + 1].CumulativeIndices / 3
                    : model.Triangles.Count;
                // Skirt triangle indices are LOCAL to the skirt's own
                // vertex array (0-based) — rebase them into pair 0's own
                // local index space, which now spans its existing
                // vertices FOLLOWED BY the skirt's.
                var pair0LocalVertCountBeforeSkirt = insertVertPos - pair0Layer.CumulativeVertices;

                var skirtTriangleList = new List<TerrainTriangle>(skirtTris.Length / 3);
                for (var i = 0; i < skirtTris.Length; i += 3)
                    skirtTriangleList.Add(new TerrainTriangle
                    {
                        V0 = (uint)(skirtTris[i] + pair0LocalVertCountBeforeSkirt),
                        V1 = (uint)(skirtTris[i + 1] + pair0LocalVertCountBeforeSkirt),
                        V2 = (uint)(skirtTris[i + 2] + pair0LocalVertCountBeforeSkirt),
                    });

                model.Vertices.InsertRange(insertVertPos, skirtVertexList);
                model.Triangles.InsertRange(insertTriPos, skirtTriangleList);

                for (var li = pair0LayerIndex + 1; li < model.Layers.Count; li++)
                {
                    model.Layers[li].CumulativeVertices += skirtVertexList.Count;
                    model.Layers[li].CumulativeIndices += skirtTriangleList.Count * 3;
                }
            }
        }

        // A real live water plane for from-scratch procedural
        // maps (EnsureDefaultMaterialsAndPair never creates a water
        // material - only "Default Grass"/"Default Rock" - so without this
        // a from-scratch map's info.bdf could say WaterEnabled=true with no
        // actual water geometry anywhere in terrain.bdf). Gated on
        // InfoModel.WaterEnabled specifically (the Environment tab's own
        // "should the decorative water surface render" toggle) - separate
        // from SC2TerrainData.PlayableWater (the Costs-only
        // "does this map have real navigable water for pathing" toggle);
        // SC2CostsShell.ApplyPlayableWaterToggle already turns this same
        // InfoModel flag on when the user enables PlayableWater, so the two
        // stay coherent without being the same field. Must happen before
        // RebuildAllBsp, same as MergeImportedTerrain above, so the water
        // layer gets a real BSP tree too - and before Hfield projection,
        // whose IsWaterMaterial check already excludes anything named
        // "Water" from height sampling.
        var infoModelForWater = BuildInfoModel(data, session);
        var alreadyHasWater = model.Materials.Exists(SC2HfieldProjectionService.IsWaterMaterial);
        if (infoModelForWater.WaterEnabled != 0 && !alreadyHasWater)
            SC2WaterPlane.AppendToExportModel(model, data, infoModelForWater.WaterElevation);

        // See SC2TerrainData.SingleBspNodePerLayer for the confirmed in-game
        // artifact that toggle exists for.
        //
        // terrain.bdf now ALWAYS uses the
        // reverse-engineered vanilla-candidate split (same as decals.bdf/
        // collision2.bdf already did unconditionally — see
        // DecalsSpatialBuilder.Rebuild/SC2Collision2Shell's own
        // useVanillaCandidateSplit:true call sites) instead of being gated
        // behind a user-facing toggle (SC2TerrainData.VanillaCandidateBspSplit,
        // removed from the Terrain tab UI — see git history). That field's
        // own default was already `true` and matched the corpus-measured
        // best choice (see TerrainBspBuilder.FindBestVanillaCandidateSplit's
        // own doc comment: mean node-count ratio 1.00 vs. real vanilla files,
        // vs. plain SAH under-splitting 24x worse by its own metric), so
        // this is a behavior no live map was actually exercising differently
        // — it just closes off the ability to turn it off. SingleBspNodePerLayer
        // still wins if it's on — there's nothing left to split once every
        // layer is forced to one leaf.
        var bspMinPolys = data.SingleBspNodePerLayer
            ? TerrainBdf.SingleLeafPerLayer
            : EdbTreeBuilder.DefaultMinPolysPerLeaf;
        var useVanillaSplit = !data.SingleBspNodePerLayer;
        var bspModeLabel = data.SingleBspNodePerLayer ? "single leaf per layer"
            : useVanillaSplit ? "vanilla-candidate" : "subdivided (SAH)";
        SC2Log.Log($"[SC2Exporter] RebuildAllBsp started. Tris={model.Triangles.Count:N0}, Verts={model.Vertices.Count:N0}, mode={bspModeLabel}");
        var swBsp = System.Diagnostics.Stopwatch.StartNew();
        EditorUtility.DisplayProgressBar("Export", $"BSP ({model.Triangles.Count:N0} tris)...", 0.85f);
        TerrainBdf.RebuildAllBsp(model, bspMinPolys, useVanillaSplit);
        var bspNodeCount = 0;
        foreach (var l in model.Layers) bspNodeCount += l.Nodes.Count;
        SC2Log.Log($"[SC2Exporter] RebuildAllBsp done in {swBsp.ElapsedMilliseconds}ms, nodes={bspNodeCount:N0}");

        EditorUtility.DisplayProgressBar("Export", "Writing...", 0.9f);
        var platform  = data.RawData != null ? data.RawData.Platform : BdfPlatform.Pc;
        var container = TerrainBdf.SerializeToContainer(model, platform);
        var terrainBytes = container.Write(TerrainBdf.Padding);

        // Hfield MUST be projected from this exact `model` — the same
        // simplified-and-merged mesh that was just serialized above, not the
        // raw pre-simplification SC2TerrainData mesh — otherwise the shipped
        // hfield.bdf (used for unit pathing/pathing height, camera bounds,
        // etc.) would desync from the terrain.bdf actually in the map, the
        // exact bug SC2HfieldProjectionService's doc comment warns about.
        EditorUtility.DisplayProgressBar("Export", "Projecting hfield...", 0.95f);
        // TerrainTypes: prefer what was hand-painted this session (SC2CostsShell
        // paints directly into data.TerrainTypes — see SC2GameViewEditorUI's
        // Costs tab) over whatever an originally-imported map's hfield.bdf had,
        // since the painted grid is the up-to-date source of truth once the
        // user has touched it. Falls back to the imported map's original
        // TerrainTypes (passthrough) only if nothing has been painted yet.
        //
        // SC2MapDocument.Hfield.
        // TerrainTypesUInt8 is tried FIRST, ahead of data.TerrainTypes —
        // SC2CostsShell.SyncDocumentFromLive now keeps it populated at load
        // time, same "prefer document, log source, fall back
        // to legacy" pattern already proven for Collision2/Environment/Scenario.
        // mapX/mapY now come from data.CostsGridSize
        // (a real per-map setting, see its own doc comment) instead of the
        // fixed SC2HfieldProjectionService.DefaultMapX/Y — real vanilla
        // hfield.bdf files aren't all 1025x1025 (measured: 513/1025/2049 all
        // occur), so a map built at a non-default size needs its hfield/
        // costs/collision2 all projected at that same size, not silently
        // forced back to 1024.
        var hfieldMapX = data.CostsGridSize + 1;
        var hfieldMapY = data.CostsGridSize + 1;
        var gridCellCount = data.CostsGridSize * data.CostsGridSize;
        var documentTerrainTypesBytes = TryResolveBlob(session?.BlobStore, session?.Document?.Hfield.TerrainTypesUInt8 ?? default);
        ushort[] documentTerrainTypes = null;
        if (documentTerrainTypesBytes != null && documentTerrainTypesBytes.Length == gridCellCount)
        {
            documentTerrainTypes = new ushort[gridCellCount];
            for (var i = 0; i < gridCellCount; i++) documentTerrainTypes[i] = documentTerrainTypesBytes[i];
        }
        var existingTerrainTypes = documentTerrainTypes
            ?? ((data.TerrainTypes != null && data.TerrainTypes.Length == gridCellCount) ? data.TerrainTypes : TryReadExistingTerrainTypes(data.RawData));
        SC2Log.Log($"[SC2Exporter] Hfield.TerrainTypes (source: {(existingTerrainTypes == documentTerrainTypes ? "SC2MapDocument" : "SC2TerrainData/legacy")})");
        // No material exclusion here - hfield.bdf must reflect every
        // material's real height; only Collision2's own separate
        // projection (built further below in BuildCollision2Model) skips
        // materials in data.Collision2ExcludedMaterialNames.
        var hfieldModel = SC2HfieldProjectionService.Project(model, existingTerrainTypes, hfieldMapX, hfieldMapY);
        var hfieldContainer = HfieldBdf.Create(hfieldModel, platform);
        var hfieldBytes = hfieldContainer.Write(HfieldBdf.Padding);
        SC2Log.Log($"[SC2Exporter] hfield.bdf projected from final exported mesh: " +
                   $"{hfieldModel.MapX}x{hfieldModel.MapY}, {model.Triangles.Count:N0} tris source.");

        // Costs: built from whatever the user painted into data.CostsBlocks via
        // SC2CostsShell (Costs tab) — EnsureCostsAndTerrainTypes() guarantees a
        // valid all-passable default even if the user never opened that tab, so
        // costs.bdf always exists and matches the 5-block vanilla layout
        // (Land_Small/Medium/Large, Water_Small/Large).
        EditorUtility.DisplayProgressBar("Export", "Building costs...", 0.97f);
        data.EnsureCostsAndTerrainTypes();
        var costsModel = BuildCostsModelFromPaintedData(data, session);
        var (costsData, costsOt) = CostsBdf.Serialize(costsModel, platform);
        var costsBytes = new BdfContainer { Platform = platform, Data = costsData, OffsetTable = costsOt, NumFiles = 1 }.Write(CostsBdf.Padding);
        SC2Log.Log($"[SC2Exporter] costs.bdf built from painted data: {costsModel.Blocks.Count} block(s).");

        if (debugSaveCostsImages)
            // existingTerrainTypes (not data.TerrainTypes directly) so the
            // debug dump reflects whatever actually went into hfield.bdf
            // above — which may now be SC2MapDocument's copy instead.
            SaveCostsDebugImages(costsModel, existingTerrainTypes, mapDir, data.CostsGridSize);

        // Collision2: auto-generated from the same hfieldModel just projected
        // above (never from the raw pre-simplification mesh — same
        // "must match what's actually shipped" invariant as hfield itself).
        // Always regenerated on export, same convention hfield/costs already
        // use here (this exporter doesn't consult SC2DirtyFlags at all yet —
        // that gap applies uniformly to hfield/costs/collision2, not
        // something new introduced here).
        //
        // Prefers SC2MapDocument.Collision2.Generation.
        // VerticalOffset over data.Collision2HeightOffset when a session is
        // available — SC2Collision2Shell keeps both in lockstep whenever its
        // own tab is drawn, so in the common case these are
        // already identical; this line is what actually proves Export CAN
        // read the value from the document rather than the legacy field.
        // KNOWN LIMITATION: if the Collision2 tab was never opened this
        // session, the document's copy is stale (still whatever a PREVIOUS
        // terrain/session left it at) — that sync gap is real and needs
        // every domain synced into the document at load time, not
        // only when its own tab is visited, to close fully.
        EditorUtility.DisplayProgressBar("Export", "Building collision2...", 0.98f);
        // Observability for the spike itself: with the offset already in
        // sync (the common case), the document and legacy paths produce an
        // IDENTICAL exported file — there is no way to tell which one
        // actually supplied the value just by looking at the output. Log
        // and surface it explicitly instead of asking anyone to trust that
        // the code took the branch it looks like it should have.
        var collision2UsedDocument = session?.Document != null;
        var collision2HeightOffset = collision2UsedDocument
            ? session.Document.Collision2.Generation.VerticalOffset
            : data.Collision2HeightOffset;
        var collision2PolygonBudget = collision2UsedDocument
            ? session.Document.Collision2.Generation.MaxPolygons
            : data.Collision2PolygonBudget;
        var collision2OffsetSource = collision2UsedDocument ? "SC2MapDocument" : "SC2TerrainData (legacy, no session)";
        SC2Log.Log($"[SC2Exporter] Collision2.VerticalOffset = {collision2HeightOffset} (source: {collision2OffsetSource}), MaxPolygons = {collision2PolygonBudget}");
        // Same WaterEnabled gate SC2WaterPlane.AppendToExportModel already
        // used above to decide whether this map even has a real water
        // plane - bake its top-projection quad into collision too, see
        // BuildCollision2Model's own doc comment for why.
        float? collision2WaterElevation = infoModelForWater.WaterEnabled != 0 ? infoModelForWater.WaterElevation : (float?)null;
        // Real fix for "Import Corrected Mesh"/"Import collision2.bdf..."
        // (SC2Collision2Shell) having no effect on the actual exported map —
        // see SC2TerrainData.UseCustomCollision2's own doc comment. When
        // set, the user's own already-serialized bytes are written verbatim
        // instead of calling BuildCollision2Model at all — every real
        // export used to silently discard a hand-corrected/imported
        // Collision2 mesh and regenerate a fresh one from the terrain
        // regardless of what the Collision2 tab's preview showed.
        byte[] collision2Bytes;
        if (data.UseCustomCollision2 && data.CustomCollision2Bytes is { Length: > 0 })
        {
            collision2Bytes = data.CustomCollision2Bytes;
            SC2Log.Log($"[SC2Exporter] collision2.bdf: using custom imported/corrected mesh verbatim ({collision2Bytes.Length} bytes) — UseCustomCollision2 is set.");
        }
        else
        {
            var collision2Model = BuildCollision2Model(model, existingTerrainTypes, hfieldMapX, hfieldMapY, collision2HeightOffset, data, collision2WaterElevation, collision2PolygonBudget);
            var (collision2Data, collision2Ot) = Collision2Bdf.Serialize(collision2Model, platform);
            collision2Bytes = new BdfContainer { Platform = platform, Data = collision2Data, OffsetTable = collision2Ot, NumFiles = 1 }.Write(Collision2Bdf.Padding);
            SC2Log.Log($"[SC2Exporter] collision2.bdf built from hfield: {collision2Model.Vertices.Count} verts, {collision2Model.Triangles.Count} tris.");
        }

        // Environment: from whatever was painted this session (SC2EnvironmentShell
        // writes directly into data.EnvironmentHeader/EnvironmentMap/etc. — see
        // its own doc comment), falling back to an imported map's original
        // info.bdf as a passthrough only if nothing has been touched yet
        // (same "prefer live-edited, fall back to imported" reasoning already
        // used for TerrainTypes above).
        EditorUtility.DisplayProgressBar("Export", "Building info.bdf...", 0.99f);
        var infoModel = BuildInfoModel(data, session);

        // Environment textures are referenced by their real game path and
        // NOT copied into the map by default. A slot whose per-texture "copy
        // into map on export" box is checked (SC2EnvironmentTextureExport) is
        // copied through the same ResolveRawResourcePath the terrain materials
        // use, and its path is rewritten to the local copy. The stored tag is
        // only honored while it still describes the slot's current path — a
        // path that was edited/undone/reset since the pick has a stale tag.
        {
            var envDocument = session?.Document?.Environment;
            var envHeader = envDocument?.WorkingHeader;
            var envUsesDocument = envHeader != null && envHeader.Length == 508
                && !System.Array.TrueForAll(envHeader, b => b == 0);
            var envOptions = envUsesDocument && envDocument.TextureExport != null
                ? envDocument.TextureExport
                : new SC2EnvironmentTextureExport
                {
                    SourceTags = data.EnvironmentTextureSourceTags,
                    ForceExport = data.EnvironmentTextureForceExport,
                };
            string ApplyEnvTextureExport(SC2EnvironmentTextureSlot slot, string currentPath)
            {
                if (!envOptions.GetForce(slot)) return currentPath;
                var tag = envOptions.GetTag(slot);
                if (!SC2VanillaArchiveClassifier.TagMatchesGamePath(tag, currentPath)) return currentPath;
                return ResolveRawResourcePath(tag, null, true) ?? currentPath;
            }
            infoModel.EnvironmentMap = ApplyEnvTextureExport(SC2EnvironmentTextureSlot.EnvironmentMap, infoModel.EnvironmentMap);
            infoModel.SkyMap = ApplyEnvTextureExport(SC2EnvironmentTextureSlot.SkyMap, infoModel.SkyMap);
            infoModel.ReservedTexturePath = ApplyEnvTextureExport(SC2EnvironmentTextureSlot.TexturePath3, infoModel.ReservedTexturePath);
            infoModel.ColorCorrectionCube = ApplyEnvTextureExport(SC2EnvironmentTextureSlot.ColorCorrectionCube, infoModel.ColorCorrectionCube);
            infoModel.FogOfWarCorrectionCube = ApplyEnvTextureExport(SC2EnvironmentTextureSlot.FogOfWarCorrectionCube, infoModel.FogOfWarCorrectionCube);
        }

        // Requested directly: auto-generate the real per-map waterDepth
        // texture (Mask1, WaterTexturePaths[6]) instead of leaving whatever
        // manual/blank path was configured — see
        // SC2WaterDepthTextureGenerator's own doc comment for the exact
        // format (RGB = configured water color, alpha = land/water +
        // shoreline foam-falloff gradient) and why nothing in this project
        // ever actually produced that file before (SC2WaterPlane.
        // BuildWaterMaterial already defaulted the path to it when blank).
        // Gated on the same WaterEnabled check AppendToExportModel used
        // above, and on a Water material actually existing in the exported
        // model (from AppendToExportModel just above, or from an imported
        // terrain.bdf that already carried real water geometry).
        if (data.WaterDepthAutoGenerate && infoModelForWater.WaterEnabled != 0)
        {
            var waterMaterial = model.Materials.Find(SC2HfieldProjectionService.IsWaterMaterial);
            if (waterMaterial != null)
            {
                float SampleHeightForWaterDepth(float x, float z)
                {
                    var mapX = (int)hfieldModel.MapX;
                    var mapY = (int)hfieldModel.MapY;
                    if (hfieldModel.Heightmap == null || mapX <= 0 || mapY <= 0) return 0f;
                    var gx = Mathf.Clamp(x, 0f, mapX - 1);
                    var gz = Mathf.Clamp(z, 0f, mapY - 1);
                    var x0 = Mathf.FloorToInt(gx); var z0 = Mathf.FloorToInt(gz);
                    var x1 = Mathf.Min(mapX - 1, x0 + 1); var z1 = Mathf.Min(mapY - 1, z0 + 1);
                    var tx = gx - x0; var tz = gz - z0;
                    float H(int gxi, int gzi) => hfieldModel.Heightmap[gzi * mapX + gxi] / 128f;
                    return Mathf.Lerp(Mathf.Lerp(H(x0, z0), H(x1, z0), tx), Mathf.Lerp(H(x0, z1), H(x1, z1), tx), tz);
                }

                var waterColor = new Color(infoModelForWater.WaterColorR, infoModelForWater.WaterColorG, infoModelForWater.WaterColorB, 1f);
                SC2WaterDepthTextureGenerator.Generate(data, SampleHeightForWaterDepth, infoModelForWater.WaterElevation,
                    waterColor, out var wdRgb, out var wdAlpha, out var wdTexSize);
                File.WriteAllBytes(Path.Combine(mapDir, $"{mapName}.waterDepth.dds"),
                    SC2DdsEncoder.BuildDxt5(wdTexSize, wdTexSize, wdRgb, wdAlpha));
                waterMaterial.TexturePaths[6] = $"/maps/{mapName}/{mapName}.waterDepth.dds";

                // The shader (_WaterDepthArea) projects Mask1Tex onto this
                // same world-space rect the generator just rasterized —
                // world (0,0) to (CostsGridSize, CostsGridSize), the same
                // 1:1 world/grid convention Costs/Hfield/decals already use
                // — auto-fill it here rather than leaving WaterDepthOriginX/
                // Y/BoundsSizeX/Y at whatever was manually authored (often
                // 0, which SC2WaterMaterialFactory's hasRealDepthArea guard
                // treats as "no real mask" and disables shore foam
                // entirely).
                infoModel.WaterDepthOriginX = 0f;
                infoModel.WaterDepthOriginY = 0f;
                infoModel.WaterDepthBoundsSizeX = data.CostsGridSize;
                infoModel.WaterDepthBoundsSizeY = data.CostsGridSize;

                SC2Log.Log($"[SC2Exporter] waterDepth.dds generated: {wdTexSize}x{wdTexSize}, margin={data.WaterDepthMargin}, falloffPower={data.WaterDepthFalloffPower}.");
            }
            else
            {
                SC2Log.LogWarning("[SC2Exporter] WaterDepthAutoGenerate is on but no Water material exists in the exported model — skipped.");
            }
        }

        var (infoData, infoOt) = InfoBdf.Serialize(infoModel, platform);
        var infoBytes = new BdfContainer { Platform = platform, Data = infoData, OffsetTable = infoOt, NumFiles = 1 }.Write(InfoBdf.Padding);
        SC2Log.Log("[SC2Exporter] info.bdf built.");

        // Objects: mapobjs.bdf (Props/Markers/Path Graphs) built from
        // data.Obj* (SC2ObjectsShell, the Objects tab) — same "always
        // regenerated on export" convention as costs/collision2/info above.
        // _save.lua/_scenario.lua are patched (or, for a from-scratch
        // procedural map with no captured original text, minimally
        // generated) so only the Markers/Areas/Armies/Chains/StartPositions
        // blocks this editor manages are touched — see SC2ObjectsExport's
        // doc comment for exactly which parts of that text are verified
        // against real files vs. best-effort.
        EditorUtility.DisplayProgressBar("Export", "Building mapobjs.bdf...", 0.995f);
        var mapObjsModel = SC2ObjectsExport.BuildMapObjsModel(data, session?.Document?.Environment?.WorkingHeader);
        var (mapObjsData, mapObjsOt) = MapObjsBdf.Serialize(mapObjsModel, platform);
        var mapObjsBytes = new BdfContainer { Platform = platform, Data = mapObjsData, OffsetTable = mapObjsOt, NumFiles = 1 }.Write(MapObjsBdf.Padding);
        SC2Log.Log($"[SC2Exporter] mapobjs.bdf built: {mapObjsModel.Props.Count} props, {mapObjsModel.Markers.Count} markers, {mapObjsModel.Graphs.Count} graphs.");

        // SC2MapDocument.Objects.SaveLuaTextOriginal/
        // ScenarioLuaTextOriginal are kept in sync by SC2ObjectsShell.
        // EnsureObjectsDocumentSynced (called at load time regardless of
        // whether the Objects tab is ever drawn) — safe to prefer over
        // data.ObjSaveLuaTextOriginal/ObjScenarioLuaTextOriginal because
        // neither is ever mutated after import (see that sync method's own
        // comment), unlike Props/Markers/etc., which is exactly why THOSE
        // aren't sourced from the document yet: too many live Objects edit
        // paths still bypass ISC2DocumentCommand for the document's copy to
        // be trusted as current.
        var documentObjects = session?.Document?.Objects;
        var saveLuaSource = !string.IsNullOrEmpty(documentObjects?.SaveLuaTextOriginal) ? "SC2MapDocument" : "SC2TerrainData/legacy";
        var scenarioLuaSource = !string.IsNullOrEmpty(documentObjects?.ScenarioLuaTextOriginal) ? "SC2MapDocument" : "SC2TerrainData/legacy";
        SC2Log.Log($"[SC2Exporter] _save.lua original text (source: {saveLuaSource}), _scenario.lua original text (source: {scenarioLuaSource})");
        var newSaveLua = SC2ObjectsExport.RegenerateSaveLua(data, documentObjects?.SaveLuaTextOriginal, session?.Document?.Environment?.WorkingHeader);
        var newScenarioLua = SC2ObjectsExport.RegenerateScenarioLua(data, mapName, documentObjects?.ScenarioLuaTextOriginal);
        var newScriptLua = SC2ObjectsExport.RegenerateScriptLua(data);
        SC2Log.Log("[SC2Exporter] _save.lua/_scenario.lua/_script.lua text built (Objects blocks regenerated).");

        // Same live-height fallback SC2DecalsShell already wires for
        // interactive placement (see SC2DecalSurfaceProvider's own doc
        // comment) — reused here as a one-shot bilinear sampler over the
        // hfieldModel this function already projected above, so a decal
        // placed on the live procedural terrain (no queryable Collider)
        // still gets real clip-target geometry at export time instead of
        // silently exporting with none.
        float SampleLiveHeightForDecals(float x, float z)
        {
            var mapX = (int)hfieldModel.MapX;
            var mapY = (int)hfieldModel.MapY;
            if (hfieldModel.Heightmap == null || mapX <= 0 || mapY <= 0) return 0f;
            var gx = Mathf.Clamp(x, 0f, mapX - 1);
            var gz = Mathf.Clamp(z, 0f, mapY - 1);
            var x0 = Mathf.FloorToInt(gx); var z0 = Mathf.FloorToInt(gz);
            var x1 = Mathf.Min(mapX - 1, x0 + 1); var z1 = Mathf.Min(mapY - 1, z0 + 1);
            var tx = gx - x0; var tz = gz - z0;
            float H(int gxi, int gzi) => hfieldModel.Heightmap[gzi * mapX + gxi] / 128f;
            return Mathf.Lerp(Mathf.Lerp(H(x0, z0), H(x1, z0), tx), Mathf.Lerp(H(x0, z1), H(x1, z1), tx), tz);
        }
        var decalsBytes = SC2DecalsExportService.Build(session, mapName, out var decalDiffuseAtlasBytes, out var decalNormalAtlasBytes, SampleLiveHeightForDecals);

        // Stale textures from a PREVIOUS export of this same map (e.g. a
        // material's Diffuse got replaced, or a texture got removed
        // entirely) used to linger in texDir forever - nothing here ever
        // deleted anything, only ever added/overwrote. texDir is this one
        // map's own exclusive output folder (outputDir is already the
        // per-map root - see this function's own opening comment), never
        // shared with another map's export, so it's safe to remove
        // whatever the CURRENT export didn't just write or keep:
        // fileNameOwner already holds the complete, authoritative set of
        // filenames this run actually needs (every write into texDir goes
        // through UniqueFileName, which populates it) - only cleaned up
        // once export has fully succeeded (this is the success return
        // path), so a failed/aborted export never wipes an existing map's
        // textures based on a partial fileNameOwner set.
        try
        {
            foreach (var existing in Directory.GetFiles(texDir))
            {
                if (!fileNameOwner.ContainsKey(Path.GetFileName(existing)))
                    File.Delete(existing);
            }
        }
        catch (Exception ex) { warnings.Add($"Textures cleanup: {ex.Message}"); }

        // Same stale-file problem, same fix, for lightingDir: a renamed/
        // removed RnmMaskName (or a pair deleted entirely) used to leave its
        // old "{safe}_0/1/2.win.dds" trio behind forever. rnmCache's keys
        // are the complete set of mask-name stems ExportRnmMaskByName
        // actually wrote/kept this run.
        try
        {
            foreach (var existing in Directory.GetFiles(lightingDir))
            {
                var name = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(existing)); // strip .dds then .win
                var underscoreAt = name.LastIndexOf('_');
                var safe = underscoreAt > 0 ? name[..underscoreAt] : name;
                if (!rnmCache.ContainsKey(safe))
                    File.Delete(existing);
            }
        }
        catch (Exception ex) { warnings.Add($"Lighting cleanup: {ex.Message}"); }

        EditorUtility.ClearProgressBar();
        return new SC2ExportArtifacts
        {
            MapName = mapName,
            MapDir = mapDir,
            TerrainBytes = terrainBytes,
            HfieldBytes = hfieldBytes,
            CostsBytes = costsBytes,
            Collision2Bytes = collision2Bytes,
            InfoBytes = infoBytes,
            MapObjsBytes = mapObjsBytes,
            DecalsBytes = decalsBytes,
            DecalDiffuseAtlasBytes = decalDiffuseAtlasBytes,
            DecalNormalAtlasBytes = decalNormalAtlasBytes,
            SaveLuaText = newSaveLua,
            ScenarioLuaText = newScenarioLua,
            ScriptLuaText = newScriptLua,
            LayersCount = build.Layers.Count,
            VerticesCount = model.Vertices.Count,
            TexturesCopied = copied,
            Warnings = warnings,
            Collision2HeightOffset = collision2HeightOffset,
            Collision2OffsetSource = collision2OffsetSource,
        };
    }

    private static string DoExport(SC2TerrainData data, string outputDir, List<SC2ImportedTerrainExportData> importedTerrains = null, bool debugSaveCostsImages = false, SC2MapSession session = null, byte[] minimapDdsBytes = null)
    {
        var artifacts = BuildExportArtifacts(data, outputDir, importedTerrains, debugSaveCostsImages, session);
        if (artifacts.ErrorMessage != null) return artifacts.ErrorMessage;

        var mapName = artifacts.MapName;
        var mapDir = artifacts.MapDir;
        // Minimap: freshly captured by the caller for every export (see
        // SC2ExportPanel/SC2MapPreviewCapture) — real vanilla convention,
        // confirmed against actual game files. Mapshot: captured once by the
        // user (Export tab's "Capture Preview" button) and stored directly
        // on SC2TerrainData, re-used at every export until re-captured — no
        // live render needed here at all.
        if (minimapDdsBytes != null)
            File.WriteAllBytes(Path.Combine(mapDir, $"{mapName}.minimap.win.dds"), minimapDdsBytes);
        if (data.MapShotBgraPixels != null && data.MapShotBgraPixels.Length == SC2MapPreviewCapture.MapShotWidth * SC2MapPreviewCapture.MapShotHeight * 4)
        {
            // MapShotBgraPixels is stored in Unity's own natural (unflipped)
            // row orientation (so the Export tab's own preview thumbnail
            // displays right-side up) — flip only at the point of writing
            // the DDS file itself. See SC2MapPreviewCapture.
            // FlipRowsVertically's own doc comment.
            var ddsBgra = SC2MapPreviewCapture.FlipRowsVertically(data.MapShotBgraPixels, SC2MapPreviewCapture.MapShotWidth, SC2MapPreviewCapture.MapShotHeight, 4);
            // Re-stamped at export so a preview captured before the frame
            // matched vanilla still ships with the vanilla frame.
            SC2MapShotFrameMask.ApplyToBgra(ddsBgra, SC2MapPreviewCapture.MapShotWidth, SC2MapPreviewCapture.MapShotHeight, bottomRowFirst: false);
            File.WriteAllBytes(Path.Combine(mapDir, $"{mapName}_PC_mapshot.dds"),
                SC2DdsEncoder.BuildBgra32(SC2MapPreviewCapture.MapShotWidth, SC2MapPreviewCapture.MapShotHeight, ddsBgra));
        }
        File.WriteAllBytes(Path.Combine(mapDir, $"{mapName}.terrain.win.bdf"), artifacts.TerrainBytes);
        File.WriteAllBytes(Path.Combine(mapDir, $"{mapName}.hfield.win.bdf"), artifacts.HfieldBytes);
        File.WriteAllBytes(Path.Combine(mapDir, $"{mapName}.costs.win.bdf"), artifacts.CostsBytes);
        File.WriteAllBytes(Path.Combine(mapDir, $"{mapName}.collision2.win.bdf"), artifacts.Collision2Bytes);
        File.WriteAllBytes(Path.Combine(mapDir, $"{mapName}.info.win.bdf"), artifacts.InfoBytes);
        File.WriteAllBytes(Path.Combine(mapDir, $"{mapName}.mapobjs.win.bdf"), artifacts.MapObjsBytes);
        // Each of these three files used to only ever get WRITTEN when its
        // bytes were non-null, never removed when they went back to null
        // (e.g. every decal was deleted from the map) — the previous
        // export's now-stale decals.bdf/atlas files stayed on disk forever,
        // silently still referencing textures/geometry no longer part of
        // the map. Delete explicitly in the else branch instead of only
        // ever adding.
        var decalsBdfPath = Path.Combine(mapDir, $"{mapName}.decals.win.bdf");
        if (artifacts.DecalsBytes != null)
            File.WriteAllBytes(decalsBdfPath, artifacts.DecalsBytes);
        else if (File.Exists(decalsBdfPath))
            File.Delete(decalsBdfPath);
        if (artifacts.DecalDiffuseAtlasBytes != null || artifacts.DecalNormalAtlasBytes != null)
            Directory.CreateDirectory(Path.Combine(mapDir, "decals"));
        var decalDiffusePath = Path.Combine(mapDir, "decals", "decals_diffuse.win.dds");
        if (artifacts.DecalDiffuseAtlasBytes != null)
            File.WriteAllBytes(decalDiffusePath, artifacts.DecalDiffuseAtlasBytes);
        else if (File.Exists(decalDiffusePath))
            File.Delete(decalDiffusePath);
        var decalNormalPath = Path.Combine(mapDir, "decals", "decals_normal.win.dds");
        if (artifacts.DecalNormalAtlasBytes != null)
            File.WriteAllBytes(decalNormalPath, artifacts.DecalNormalAtlasBytes);
        else if (File.Exists(decalNormalPath))
            File.Delete(decalNormalPath);
        var luaEncoding = new System.Text.UTF8Encoding(false);
        // Compiled-slot lua lives right alongside the bdf files in maps/
        // <mapName>/, matching a real per-map .scd (confirmed against a real
        // installed gamedata/eclipse.scd). This editor never compiles Lua to
        // bytecode, so this is plain text, same as the uncompiled copy below.
        File.WriteAllText(Path.Combine(mapDir, $"{mapName}_save.lua"), artifacts.SaveLuaText, luaEncoding);
        File.WriteAllText(Path.Combine(mapDir, $"{mapName}_scenario.lua"), artifacts.ScenarioLuaText, luaEncoding);
        File.WriteAllText(Path.Combine(mapDir, $"{mapName}_script.lua"), artifacts.ScriptLuaText, luaEncoding);

        // Real per-map .scd installs also carry the same _save/_scenario/
        // _script.lua text a second time, as plain text under a sibling
        // uncompiled/maps/<Name>/ subfolder — confirmed against the same
        // real gamedata/eclipse.scd example.
        var uncompiledDir = Path.Combine(outputDir, "uncompiled", "maps", mapName);
        Directory.CreateDirectory(uncompiledDir);
        File.WriteAllText(Path.Combine(uncompiledDir, $"{mapName}_save.lua"), artifacts.SaveLuaText, luaEncoding);
        File.WriteAllText(Path.Combine(uncompiledDir, $"{mapName}_scenario.lua"), artifacts.ScenarioLuaText, luaEncoding);
        File.WriteAllText(Path.Combine(uncompiledDir, $"{mapName}_script.lua"), artifacts.ScriptLuaText, luaEncoding);
        SC2Log.Log("[SC2Exporter] terrain/hfield/costs/collision2/info/mapobjs.bdf + _save.lua/_scenario.lua/_script.lua written (incl. uncompiled/ copy).");

        var msg = $"terrain.bdf → maps/{mapName}/\nPairs: {artifacts.LayersCount}  Vertices: {artifacts.VerticesCount}\n" +
                  $"Textures: {artifacts.TexturesCopied}" + (artifacts.Warnings.Count>0 ? $"\n⚠ {artifacts.Warnings.Count} warnings (see Console)" : "");
        msg += $"\nCollision2 offset: {artifacts.Collision2HeightOffset} (source: {artifacts.Collision2OffsetSource})";
        foreach (var w in artifacts.Warnings) Debug.LogWarning($"[SC2Exporter] {w}");
        Debug.Log($"[SC2Exporter] {msg}");
        EditorUtility.DisplayDialog("Done", msg, "OK");
        return msg;
    }

    /// <summary>An additive "Smart Export (new)" entry
    /// point, sitting beside ExportCorrect/DoExport rather than replacing
    /// them. Shares BuildExportArtifacts above with the legacy exporter so
    /// both buttons produce byte-IDENTICAL bytes for every domain — the only
    /// difference is HOW those bytes reach disk: DoExport writes each file
    /// directly and unconditionally (as it always has); this path wraps
    /// every domain in a SC2ExportFileState and delegates the actual write
    /// to SC2SmartExportWriter, which also emits sc2_export_report.md.
    /// session.PendingDirtyFlags is real and accumulating, and is
    /// now genuinely consulted — but only ScriptLua
    /// actually varies based on it today; every other domain is still
    /// force-included in effectiveDirtyFlags below, and Decals is always
    /// included for an unrelated reason (its own export service already
    /// resolves original-vs-regenerated internally). See
    /// effectiveDirtyFlags's own doc comment further down for the precise,
    /// per-domain reasoning — a real audit found several live
    /// edit paths (Terrain brush sculpting most notably) that still mutate
    /// SC2TerrainData with zero signal to the session, so trusting
    /// PendingDirtyFlags for those would silently export stale bytes.
    /// ScriptLua is the one domain confirmed safe: nothing ever marks it
    /// dirty (no live-edit UI exists for it at all) and a real original-
    /// bytes blob is now wired into its MakeFile call below, so it correctly
    /// resolves to real original-passthrough (protecting an imported map's
    /// actual mission logic from being overwritten by the generic skeleton
    /// generator on every export) whenever nothing has touched it.</summary>
    /// <summary>The plan built by the most recent ExportCorrectSmart OR
    /// PreviewSmartExportPlan call — a real per-file breakdown (Kind/Mode/
    /// IsStale/Warnings/Errors), previously built and then immediately
    /// discarded once SC2SmartExportWriter had written it to
    /// sc2_export_report.md on disk. Exposed here (same "static, single-map-
    /// editor-at-a-time" pattern already used by e.g. ScdImportWindow.
    /// LastImportSummary) so the Export &gt; Report tab can show it in-UI
    /// instead of only as a file the user has to go find afterward.</summary>
    public static SC2SmartExportPlan LastSmartExportPlan { get; private set; }

    /// <summary>Everything ExportCorrectSmart needs up to (but not
    /// including) the actual disk write — shared by ExportCorrectSmart
    /// itself and PreviewSmartExportPlan (the Export &gt; Report tab's
    /// "Preview (dry run)" button), so a dry run and a real export are
    /// guaranteed to plan identically since they're the same code path.
    /// Returns null (with errorMessage set) if BuildExportArtifacts itself
    /// failed before a plan could even be built.</summary>
    private static SC2SmartExportPlan BuildSmartExportPlanInternal(SC2TerrainData data, string outputDir, List<SC2ImportedTerrainExportData> importedTerrains, SC2MapSession session, out string errorMessage, out SC2ExportArtifacts artifacts)
    {
        errorMessage = null;
        artifacts = BuildExportArtifacts(data, outputDir, importedTerrains, false, session);
        if (artifacts.ErrorMessage != null) { errorMessage = artifacts.ErrorMessage; return null; }

        // Same relative-path convention DoExport writes above — outputDir is
        // already the per-map root (real "<mapName>.scd" folder, or a temp
        // staging dir standing in for it).
        var mapRel = $"maps/{artifacts.MapName}";
        var uncompiledRel = $"uncompiled/maps/{artifacts.MapName}";
        var masks = SC2ExportDependencyGraph.CreateDefaultFileMasks();

        SC2ExportFileState MakeFile(SC2ExportFileKind kind, string relativePath, byte[] outputBytes, byte[] originalBytes = null)
        {
            masks.TryGetValue(kind, out var mask);
            var state = new SC2ExportFileState(kind, relativePath, originalBytes, mask);
            if (outputBytes != null) state.SetOutput(outputBytes, SC2ExportFileMode.Regenerated);
            return state;
        }

        // Real original-bytes source for _script.lua specifically
        // — the one domain confirmed safe for this (see the dirtyFlags
        // constant's own doc comment below for why the other 11 aren't yet).
        // SC2ScenarioLegacyBridge already populates this at import via
        // SC2MapImportOrchestrator; same blob-read pattern
        // SC2DecalsExportService.ReadBlob already uses for Decals.
        byte[] ReadOriginalBlob(SC2BlobReference reference)
        {
            if (session?.BlobStore == null || reference.IsEmpty) return null;
            using var stream = session.BlobStore.OpenRead(reference.ContentHash);
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return memory.ToArray();
        }

        // Real original bytes for the MAIN _script.lua slot only
        // (never the uncompiled/ mirror below — that one exists specifically
        // to hold human-readable plain text, and a real vanilla original is
        // frequently compiled Lua 5.1 bytecode, so passing the same bytes
        // into the mirror would defeat its whole purpose). _script.lua is
        // real, hand-authored mission-logic code no generator can safely
        // reproduce — see the dirtyFlags constant below for why this is the
        // one domain where "prefer the untouched original when nothing
        // marked it dirty" is the CORRECT default, not just a safe one.
        var originalScriptLua = session != null ? ReadOriginalBlob(session.Document?.Scenario?.OriginalScriptLua ?? default) : null;
        // Real original bytes for costs.bdf — Costs is real,
        // hand-painted navmesh data (unlike Hfield's heightmap half or
        // Collision2's mesh, both always regenerated from geometry BY
        // DESIGN, never a "the original" concept at all — see
        // effectiveDirtyFlags's own comment below for why those two stay
        // forced-dirty regardless of this), so genuine original-passthrough
        // is meaningful here whenever nothing has actually touched it.
        var originalCostsBdf = session != null ? ReadOriginalBlob(session.Document?.Costs?.OriginalBdf ?? default) : null;
        // Real original bytes for mapobjs.bdf — Objects (Props/
        // Markers/Units/Zones/Chains/Armies/Graphs/SymmetryGroups) is real,
        // hand-authored placement data, same "genuine original to preserve
        // when untouched" shape as Costs/ScriptLua above. Meaningful now
        // that SC2ObjectsShell's MarkObjectsDirty() sweep covers
        // every real mutation site — the gap effectiveDirtyFlags's own
        // comment below used to cite (deletes, field-setter inspectors,
        // batch/bulk edits, graph/chain/zone edits) is closed.
        var originalMapObjsBdf = session != null ? ReadOriginalBlob(session.Document?.Objects?.OriginalBdf ?? default) : null;

        var utf8NoBom = new System.Text.UTF8Encoding(false);
        var files = new List<SC2ExportFileState>
        {
            MakeFile(SC2ExportFileKind.TerrainBdf, $"{mapRel}/{artifacts.MapName}.terrain.win.bdf", artifacts.TerrainBytes),
            MakeFile(SC2ExportFileKind.HfieldBdf, $"{mapRel}/{artifacts.MapName}.hfield.win.bdf", artifacts.HfieldBytes),
            MakeFile(SC2ExportFileKind.CostsBdf, $"{mapRel}/{artifacts.MapName}.costs.win.bdf", artifacts.CostsBytes, originalCostsBdf),
            MakeFile(SC2ExportFileKind.Collision2Bdf, $"{mapRel}/{artifacts.MapName}.collision2.win.bdf", artifacts.Collision2Bytes),
            MakeFile(SC2ExportFileKind.InfoBdf, $"{mapRel}/{artifacts.MapName}.info.win.bdf", artifacts.InfoBytes),
            MakeFile(SC2ExportFileKind.MapObjsBdf, $"{mapRel}/{artifacts.MapName}.mapobjs.win.bdf", artifacts.MapObjsBytes, originalMapObjsBdf),
            MakeFile(SC2ExportFileKind.SaveLua, $"{mapRel}/{artifacts.MapName}_save.lua", utf8NoBom.GetBytes(artifacts.SaveLuaText)),
            MakeFile(SC2ExportFileKind.ScenarioLua, $"{mapRel}/{artifacts.MapName}_scenario.lua", utf8NoBom.GetBytes(artifacts.ScenarioLuaText)),
            MakeFile(SC2ExportFileKind.ScriptLua, $"{mapRel}/{artifacts.MapName}_script.lua", utf8NoBom.GetBytes(artifacts.ScriptLuaText), originalScriptLua),
            // Same three files, plain text, mirrored under a sibling
            // uncompiled/ subfolder — see the matching comment in DoExport above.
            MakeFile(SC2ExportFileKind.SaveLua, $"{uncompiledRel}/{artifacts.MapName}_save.lua", utf8NoBom.GetBytes(artifacts.SaveLuaText)),
            MakeFile(SC2ExportFileKind.ScenarioLua, $"{uncompiledRel}/{artifacts.MapName}_scenario.lua", utf8NoBom.GetBytes(artifacts.ScenarioLuaText)),
            MakeFile(SC2ExportFileKind.ScriptLua, $"{uncompiledRel}/{artifacts.MapName}_script.lua", utf8NoBom.GetBytes(artifacts.ScriptLuaText)),
        };

        // A map with zero placed decals still gets a real decals.win.bdf —
        // SC2DecalsExportService.Build now always returns bytes (either the
        // preserved original, or a genuine empty-DecalsModel stub when
        // there's neither an original nor any placements — confirmed by
        // byte-level inspection of a real vanilla reference file;
        // its absence previously caused the file to be silently omitted).
        // Atlas bytes stay genuinely optional (null when the map has no
        // atlas texture to export), so this null-check still matters for
        // the two atlas entries below even though DecalsBytes itself is
        // effectively never null anymore.
        if (artifacts.DecalsBytes != null)
            files.Add(MakeFile(SC2ExportFileKind.DecalsBdf, $"{mapRel}/{artifacts.MapName}.decals.win.bdf", artifacts.DecalsBytes));
        if (artifacts.DecalDiffuseAtlasBytes != null)
            files.Add(MakeFile(SC2ExportFileKind.DecalDiffuseAtlas, $"{mapRel}/decals/decals_diffuse.win.dds", artifacts.DecalDiffuseAtlasBytes));
        if (artifacts.DecalNormalAtlasBytes != null)
            files.Add(MakeFile(SC2ExportFileKind.DecalNormalAtlas, $"{mapRel}/decals/decals_normal.win.dds", artifacts.DecalNormalAtlasBytes));

        // session.
        // PendingDirtyFlags is real, accumulated ground truth for domains
        // touched through an actual ISC2DocumentCommand since the last
        // export — now genuinely consulted (unioned into effectiveDirty
        // below), not just logged. It is NOT yet trusted as the SOLE source
        // of "is this domain dirty" for most domains, though: a real,
        // file-by-file audit found several live edit paths that
        // still never raise Document.Changed at all. Objects USED to be
        // chief among them (delete via DeleteMarkersById/DeletePropsById/
        // DeleteUnitsById, plus dozens of other scattered mutation sites
        // across SC2ObjectsShell.cs) — fixed the same day via a systematic
        // MarkObjectsDirty() sweep (see effectiveDirtyFlags's own comment
        // below), so Objects is now excluded from alwaysRegenerate. For the
        // domains still listed there, "not in PendingDirtyFlags" cannot be
        // trusted to mean "truly unchanged" — trusting it would silently skip writing a
        // file the user actually changed.
        //
        // Terrain brush sculpting/painting (SculptBrush/SculptSetHeight/
        // PaintPair/PaintBlend) used to be exactly this same class of gap —
        // fixed via SC2MapSession.MarkDirty, called directly from
        // SC2GeometryMode/SC2PaintMode's own MouseUp handlers (their undo
        // mechanism is entirely separate from Session.History, so this is a
        // deliberate escape hatch, not a missed opportunity to route through
        // a real command). TerrainGeometry/TerrainMaterials themselves STAY
        // in alwaysRegenerate below regardless — terrain.bdf still has no
        // original-bytes source at all (nothing populates session.Document.
        // Terrain at import) — but the fix matters for what it unblocks
        // downstream: SC2ExportDependencyGraph.ExpandStaleFlags cascades
        // TerrainGeometry into HfieldHeightmap/Costs/Collision2, so before
        // this fix those three were ALSO unconditionally forced dirty every
        // export no matter what their own tracking said, regardless of
        // whether they were individually excluded here.
        //
        // Costs is the one direct beneficiary: real per-stroke tracking
        // already existed (SC2CostsShell.SyncDocumentFromLive fires a real
        // command on every paint MouseUp) and a real original-bytes blob is
        // now wired in too (session.Document.Costs.OriginalBdf, populated at
        // import — see the MakeFile call above), so it's excluded from
        // alwaysRegenerate below: with the TerrainGeometry cascade gap
        // closed, "Costs not in PendingDirtyFlags" now genuinely means
        // untouched since import, and correctly falls back to the real
        // original costs.bdf bytes.
        //
        // HfieldHeightmap/HfieldTerrainTypes and Collision2 stay forced-
        // dirty despite the same cascade fix, for a DIFFERENT reason than
        // Info's below: both are architecturally always-regenerated, not
        // just missing an original-bytes wire-up. Hfield's heightmap half is
        // deliberately never imported at all (re-derived fresh from the
        // merged terrain mesh every export, by design — see
        // SC2MapImportOrchestrator's own comment on why only TerrainTypes
        // gets imported), and Collision2's mesh is documented as "generated
        // from hfield," never treated as an original to preserve. Since
        // hfield.bdf is ONE file covering both the heightmap and
        // TerrainTypes halves (see CreateDefaultFileMasks), and the
        // heightmap half can never have a valid "this is the untouched
        // original" answer, the file as a whole can't safely support real
        // passthrough even now that TerrainTypes' own tracking is reliable —
        // closing this would need hfield.bdf split into two independently-
        // tracked outputs, out of scope here.
        //
        // Info similarly stays forced-dirty: live-edit tracking IS reliable
        // (SC2EnvironmentShell.MaybeSnapshot fires a real command on every
        // change), but SC2EnvironmentDocument.OriginalHeader is only the
        // 508-byte payload, not the full original .info.win.bdf container
        // bytes — using it directly would mean re-serializing rather than
        // true byte-for-byte passthrough, a different (and riskier) claim
        // than what ScriptLua/Costs now do.
        //
        // ScriptLua is the one domain audited clean on BOTH counts: nothing
        // anywhere ever marks it dirty (there's no live-edit UI for
        // _script.lua at all — it's real hand-authored mission-logic code,
        // never generator output the user tweaks), and a real original-bytes
        // blob (session.Document.Scenario.OriginalScriptLua, populated at
        // import) already exists and is now wired into the main file's
        // MakeFile call above. So it's excluded from alwaysRegenerate on
        // purpose: with nothing ever setting its PendingDirtyFlags bit, it
        // naturally resolves to SC2SmartExportPlanner's not-dirty branch —
        // SetPassthrough() then prefers the real original (an imported map's
        // actual mission logic, never overwritten by the generic skeleton
        // generator) and falls back to the freshly-generated skeleton only
        // when there's no original at all (a from-scratch map), per its own
        // fixed "keep already-computed output instead of wiping to Missing"
        // behavior (SC2ExportFileState.SetPassthrough).
        //
        // Decals is excluded from alwaysRegenerate for a different reason:
        // SC2DecalsExportService.Build already resolves original-vs-
        // regenerated-vs-merged internally (via document.Placements.Count
        // and KeepOriginalBakedLayer), independent of dirty-flag tracking —
        // artifacts.DecalsBytes is already exactly correct either way, so
        // there is no correctness reason to gate it here; it's simply always
        // included when SC2DecalsExportService actually produced bytes (see
        // the conditional Add() calls above).
        //
        // Objects is the newest domain excluded: unlike
        // Terrain's clean per-tool MouseUp handlers, SC2ObjectsShell.cs has
        // no single choke point — ~40+ scattered mutation sites (Add/Remove
        // on Props/Markers/Units/Zones/Chains/Graphs/Armies/SymmetryGroups,
        // per-field inspectors, batch/bulk-edit panels, graph edges, chain
        // waypoints). A systematic full-coverage sweep (grepping directly for
        // every Data.Obj*.Add/Remove/RemoveAll/Insert/Clear( call site, not
        // just the shell's own method-name list) added a MarkObjectsDirty()
        // call at every one of them, calling the same SC2MapSession.MarkDirty
        // escape hatch TerrainGeometry/TerrainMaterials already use. A real
        // original-bytes blob (session.Document.Objects.OriginalBdf,
        // populated at import — see the MakeFile call above) now backs it,
        // so it's excluded from alwaysRegenerate: with the sweep closing the
        // "not in PendingDirtyFlags could still mean silently unchanged" gap
        // this constant's own comment used to warn about, "Objects not in
        // PendingDirtyFlags" now genuinely means untouched since import.
        const SC2DirtyFlags alwaysRegenerate =
            SC2DirtyFlags.TerrainGeometry | SC2DirtyFlags.TerrainMaterials |
            SC2DirtyFlags.HfieldHeightmap | SC2DirtyFlags.HfieldTerrainTypes |
            SC2DirtyFlags.Collision2 |
            SC2DirtyFlags.SaveLua | SC2DirtyFlags.ScenarioLua | SC2DirtyFlags.Info |
            SC2DirtyFlags.Decals;
        var effectiveDirtyFlags = alwaysRegenerate | (session?.PendingDirtyFlags ?? SC2DirtyFlags.None);
        if (session != null)
            SC2Log.Log($"[SC2Exporter] session.PendingDirtyFlags since last export: {session.PendingDirtyFlags} (now consulted for ScriptLua, Costs and Objects; every other domain still forced dirty — see effectiveDirtyFlags's own comment).");

        return SC2SmartExportPlanner.BuildPlan(files, effectiveDirtyFlags);
    }

    /// <summary>Builds the same plan ExportCorrectSmart would, WITHOUT
    /// writing anything to disk or calling session.MarkExported() — a real
    /// preview, for the Export &gt; Report tab's "Preview (dry run)" button.
    /// Always sets LastSmartExportPlan (even when blocked) so the Report tab
    /// can show exactly why.</summary>
    public static string PreviewSmartExportPlan(SC2TerrainData data, string outputDir, List<SC2ImportedTerrainExportData> importedTerrains = null, SC2MapSession session = null)
    {
        if (!data.IsInitialized || data.Pairs.Count == 0)
            return "Preview failed: no data.";
        SC2Log.Log("=== Smart Export preview (dry run) invoked ===");
        try
        {
            var plan = BuildSmartExportPlanInternal(data, outputDir, importedTerrains, session, out var errorMessage, out _);
            if (plan == null) return errorMessage;
            LastSmartExportPlan = plan;
            var errCount = 0; var warnCount = 0;
            foreach (var f in plan.Files) { errCount += f.Errors.Count; warnCount += f.Warnings.Count; }
            return $"Preview built: {plan.Files.Count} file(s), {errCount} error(s), {warnCount} warning(s). Nothing written to disk.";
        }
        catch (Exception ex)
        {
            SC2Log.LogException(ex);
            return "Preview failed: " + ex.Message;
        }
        finally
        {
            SC2Log.Flush();
        }
    }

    public static string ExportCorrectSmart(SC2TerrainData data, string outputDir, List<SC2ImportedTerrainExportData> importedTerrains = null, SC2MapSession session = null, bool exportAsArchives = false)
    {
        if (!data.IsInitialized || data.Pairs.Count == 0)
        {
            EditorUtility.DisplayDialog("Export", "No data.", "OK");
            return "Export failed: no data.";
        }
        SC2Log.Log("=== Smart Export invoked ===");
        var archivePath = GetPerMapDestinationPath(outputDir, data, exportAsArchives);
        var perMapRoot = exportAsArchives ? CreateExportStagingDir() : archivePath;
        try
        {
            var plan = BuildSmartExportPlanInternal(data, perMapRoot, importedTerrains, session, out var buildError, out var artifacts);
            if (plan == null) return buildError;
            LastSmartExportPlan = plan;

            if (plan.HasBlockingErrors())
            {
                EditorUtility.ClearProgressBar();
                var errParts = new List<string>();
                foreach (var f in plan.Files)
                    foreach (var e in f.Errors)
                        errParts.Add($"{f.RelativePath}: {e}");
                var errMsg = "Smart Export blocked: " + string.Join("; ", errParts);
                EditorUtility.DisplayDialog("Error", errMsg, "OK");
                return errMsg;
            }

            SC2SmartExportWriter.WriteToFolder(plan, perMapRoot);
            // Successful write consumes whatever was pending — the next
            // accumulation starts
            // clean rather than compounding across exports.
            session?.MarkExported();

            EditorUtility.ClearProgressBar();
            var msg = $"[Smart Export] terrain.bdf → maps/{artifacts.MapName}/\nPairs: {artifacts.LayersCount}  Vertices: {artifacts.VerticesCount}\n" +
                      $"Textures: {artifacts.TexturesCopied}" + (artifacts.Warnings.Count > 0 ? $"\n⚠ {artifacts.Warnings.Count} warnings (see Console)" : "");
            msg += $"\nCollision2 offset: {artifacts.Collision2HeightOffset} (source: {artifacts.Collision2OffsetSource})";
            foreach (var w in artifacts.Warnings) Debug.LogWarning($"[SC2Exporter] {w}");
            if (exportAsArchives)
                msg = PackStagingRootIntoSingleArchive(perMapRoot, archivePath, msg);
            Debug.Log($"[SC2Exporter] {msg}");
            EditorUtility.DisplayDialog("Done (Smart Export)", msg, "OK");
            return msg;
        }
        catch (Exception ex)
        {
            EditorUtility.ClearProgressBar();
            SC2Log.LogException(ex);
            EditorUtility.DisplayDialog("Error", ex.Message, "OK");
            return "Export failed: " + ex.Message;
        }
        finally
        {
            SC2Log.Flush();
            if (exportAsArchives) TryDeleteStagingDir(perMapRoot);
        }
    }

    // ── RNM DDS export ────────────────────────────────────────────────────────

    /// <summary>
    /// Appends one imported terrain.bdf object's original Materials/Layers/
    /// Vertices/Triangles into <paramref name="model"/> — vertex positions
    /// and normals are baked through the object's current Transform
    /// (position/rotation/scale as edited in the Imported panel), UVs are
    /// left untouched. BSP nodes are intentionally NOT copied/rebuilt here:
    /// TerrainBdf.RebuildAllBsp(model), called once after every imported
    /// terrain has been merged in, rebuilds them for every layer including
    /// these new ones from the (now-merged) Vertices/Triangles.
    /// </summary>
    // internal (not private): reused by SC2RnmBakeService to build the same
    // merged geometry the real export uses, without duplicating this transform
    // + layer/triangle-offset logic a second time.
    /// <summary>
    /// Area-weighted smooth vertex normals for an imported terrain.bdf,
    /// computed from its actual triangle geometry in the source model's own
    /// local space (the caller transforms them afterwards, exactly like it
    /// already does for the stored normals).
    ///
    /// Two details that matter and are easy to get wrong:
    ///  - Triangle indices in TerrainModel are LAYER-LOCAL (offset by each
    ///    layer's CumulativeVertices), so accumulation has to walk layers,
    ///    not just the flat triangle list.
    ///  - Accumulation is welded BY POSITION across layer boundaries. A
    ///    terrain.bdf stores separate vertex records per layer, so a vertex
    ///    where two material layers meet exists once per layer; accumulating
    ///    per-record would give each copy a normal built from only its own
    ///    layer's triangles, producing a visible lighting seam along every
    ///    layer boundary. This is the same class of bug already fixed for the
    ///    live/painted path in SC2TerrainSimplifier.ComputeGlobalNormals.
    /// </summary>
    private static (Vector3[] Normals, Vector3[] Tangents, Vector3[] Binormals) ComputeImportedVertexTbn(TerrainModel source)
    {
        var count = source.Vertices.Count;
        var normals = new Vector3[count];
        var tangents = new Vector3[count];
        var binormals = new Vector3[count];
        if (count == 0) return (normals, tangents, binormals);

        // Weld key: positions quantized to 1e-3 world units. Vertices that
        // are meant to be shared across layers are written from the same
        // authoring-tool value, so they match exactly; the quantization only
        // guards against float round-trip noise.
        var weld = new System.Collections.Generic.Dictionary<(long, long, long), int>(count);
        var weldIndex = new int[count];
        var accum = new System.Collections.Generic.List<Vector3>(count);
        var accumT = new System.Collections.Generic.List<Vector3>(count);
        var accumB = new System.Collections.Generic.List<Vector3>(count);
        for (var i = 0; i < count; i++)
        {
            var v = source.Vertices[i];
            var key = ((long)Mathf.RoundToInt(v.PosX * 1000f),
                       (long)Mathf.RoundToInt(v.PosY * 1000f),
                       (long)Mathf.RoundToInt(v.PosZ * 1000f));
            if (!weld.TryGetValue(key, out var wi))
            {
                wi = accum.Count;
                weld[key] = wi;
                accum.Add(Vector3.zero);
                accumT.Add(Vector3.zero);
                accumB.Add(Vector3.zero);
            }
            weldIndex[i] = wi;
        }

        for (var li = 0; li < source.Layers.Count; li++)
        {
            var vertBase = source.Layers[li].CumulativeVertices;
            var triStart = source.Layers[li].CumulativeIndices / 3;
            var triEnd = li + 1 < source.Layers.Count
                ? source.Layers[li + 1].CumulativeIndices / 3
                : source.Triangles.Count;
            for (var t = triStart; t < triEnd && t < source.Triangles.Count; t++)
            {
                var tri = source.Triangles[t];
                int i0 = vertBase + (int)tri.V0, i1 = vertBase + (int)tri.V1, i2 = vertBase + (int)tri.V2;
                if (i0 < 0 || i1 < 0 || i2 < 0 || i0 >= count || i1 >= count || i2 >= count) continue;
                var a = source.Vertices[i0];
                var b = source.Vertices[i1];
                var c = source.Vertices[i2];
                var pa = new Vector3(a.PosX, a.PosY, a.PosZ);
                var pb = new Vector3(b.PosX, b.PosY, b.PosZ);
                var pc = new Vector3(c.PosX, c.PosY, c.PosZ);
                // Left unnormalized on purpose: the cross product's magnitude
                // is twice the triangle area, which is exactly the weighting
                // a smooth normal should use.
                var e1 = pb - pa;
                var e2 = pc - pa;
                var face = Vector3.Cross(e1, e2);
                accum[weldIndex[i0]] += face;
                accum[weldIndex[i1]] += face;
                accum[weldIndex[i2]] += face;

                // Tangent frame from the REAL UV1 gradient. Unlike the live
                // path's BuildUv1Tbn (which may shortcut, because there UV1 is
                // a known planar x/z projection), an imported mesh carries
                // arbitrary authored UV1, so the general form is required.
                float du1 = b.Uv1U - a.Uv1U, dv1 = b.Uv1V - a.Uv1V;
                float du2 = c.Uv1U - a.Uv1U, dv2 = c.Uv1V - a.Uv1V;
                var den = du1 * dv2 - du2 * dv1;
                if (Mathf.Abs(den) < 1e-12f) continue; // degenerate in UV space — contributes nothing
                var f = 1f / den;
                var tan = new Vector3(f * (dv2 * e1.x - dv1 * e2.x),
                                      f * (dv2 * e1.y - dv1 * e2.y),
                                      f * (dv2 * e1.z - dv1 * e2.z));
                var bit = new Vector3(f * (du1 * e2.x - du2 * e1.x),
                                      f * (du1 * e2.y - du2 * e1.y),
                                      f * (du1 * e2.z - du2 * e1.z));
                accumT[weldIndex[i0]] += tan; accumT[weldIndex[i1]] += tan; accumT[weldIndex[i2]] += tan;
                accumB[weldIndex[i0]] += bit; accumB[weldIndex[i1]] += bit; accumB[weldIndex[i2]] += bit;
            }
        }

        for (var i = 0; i < count; i++)
        {
            var wi = weldIndex[i];
            var n = accum[wi];
            // Degenerate (no incident triangle area) — leave zero and let the
            // caller keep that vertex's original stored frame instead.
            if (n.sqrMagnitude <= 1e-12f) { normals[i] = Vector3.zero; continue; }
            var nn = n.normalized;
            normals[i] = nn;

            // Gram-Schmidt the averaged tangent against the final normal (the
            // two averages are not exactly perpendicular).
            var t = accumT[wi];
            t -= nn * Vector3.Dot(t, nn);
            tangents[i] = t.sqrMagnitude > 1e-12f
                ? t.normalized
                : Vector3.Cross(Vector3.forward, nn).normalized; // no usable UV anywhere on this vertex
            // Stored binormal runs against +V — the DirectX-style −V convention
            // this format uses, measured at 99.62% on real vanilla maps (see
            // BuildUv1Tbn's own doc comment for that measurement).
            binormals[i] = accumB[wi].sqrMagnitude > 1e-12f
                ? (-accumB[wi]).normalized
                : Vector3.Cross(nn, tangents[i]).normalized;
        }
        return (normals, tangents, binormals);
    }

    internal static void MergeImportedTerrain(TerrainModel model, SC2ImportedTerrainExportData imported, Func<string, Texture2D, bool, string> resolveRawResourcePath = null)
    {
        var source = imported.Model;
        var t = imported.Transform;
        var vertexBase = model.Vertices.Count;

        // Default (see SC2ImportedTerrainExportData.RecomputeNormals for the
        // measurements behind it): rebuild the whole tangent frame, not just
        // the normal — the same Blender exporter that writes a constant
        // straight-up normal also writes a single constant tangent for the
        // entire mesh, which breaks every normal-mapped surface and decal.
        var haveRecomputed = imported.RecomputeNormals;
        var (recomputedN, recomputedT, recomputedB) = haveRecomputed
            ? ComputeImportedVertexTbn(source)
            : (null, null, null);

        for (var vi = 0; vi < source.Vertices.Count; vi++)
        {
            var v = source.Vertices[vi];
            var pos = t != null ? t.TransformPoint(new Vector3(v.PosX, v.PosY, v.PosZ)) : new Vector3(v.PosX, v.PosY, v.PosZ);
            // Tangent/Binormal must be carried across the same way the Normal
            // already was — they are real per-vertex data in terrain.bdf's
            // packed [+24]/[+28] words, not something the game recomputes (see
            // TerrainVertex's comment). Dropping them here would have written a
            // zero tangent frame for every imported vertex.
            Vector3 nrm = new(v.NormalX, v.NormalY, v.NormalZ);
            Vector3 tan = new(v.TangentX, v.TangentY, v.TangentZ);
            Vector3 bin = new(v.BinormalX, v.BinormalY, v.BinormalZ);
            var keepRaw = v.HasPackedTbn;
            if (haveRecomputed && recomputedN[vi].sqrMagnitude > 1e-12f)
            {
                // The whole frame is rebuilt from geometry + real UV1, so the
                // source's own tangent (constant across the entire mesh on a
                // Blender export) is deliberately NOT carried over.
                nrm = recomputedN[vi];
                tan = recomputedT[vi];
                bin = recomputedB[vi];
                // These floats no longer match the source's packed words,
                // so they must be re-packed rather than copied verbatim.
                keepRaw = false;
            }
            if (t != null)
            {
                nrm = t.TransformDirection(nrm).normalized;
                tan = t.TransformDirection(tan).normalized;
                bin = t.TransformDirection(bin).normalized;
                // Rotated/scaled vectors no longer match the source words, so
                // they have to be re-packed from these transformed values.
                keepRaw = false;
            }
            model.Vertices.Add(new TerrainVertex
            {
                PosX = pos.x, PosY = pos.y, PosZ = pos.z,
                Uv1U = v.Uv1U, Uv1V = v.Uv1V,
                Uv2U = v.Uv2U, Uv2V = v.Uv2V,
                NormalX = nrm.x, NormalY = nrm.y, NormalZ = nrm.z,
                TangentX = tan.x, TangentY = tan.y, TangentZ = tan.z,
                BinormalX = bin.x, BinormalY = bin.y, BinormalZ = bin.z,
                PackedNormal = v.PackedNormal,
                PackedTangent = v.PackedTangent,
                PackedBinormal = v.PackedBinormal,
                HasPackedTbn = keepRaw,
            });
        }

        for (var i = 0; i < source.Layers.Count; i++)
        {
            var srcLayer = source.Layers[i];
            model.Layers.Add(new BdfIO.Formats.Terrain.TerrainLayer
            {
                BspVersion1 = srcLayer.BspVersion1,
                BspVersion2 = srcLayer.BspVersion2,
                Hc = srcLayer.Hc,
                CumulativeVertices = vertexBase + srcLayer.CumulativeVertices,
                CumulativeIndices = model.Triangles.Count * 3 + srcLayer.CumulativeIndices,
                MapCenter = (float[])srcLayer.MapCenter.Clone(),
            });
        }

        // Triangle indices in TerrainModel.Triangles are LAYER-LOCAL
        // (0..layerVertCount-1 within that layer's own vertex range, per
        // TerrainBspBuilder's format assumption — NOT direct/global indices
        // into the flattened model.Vertices list). The layer's own
        // CumulativeVertices/CumulativeIndices (set above) already carries
        // the absolute offset, so triangle indices must be copied AS-IS,
        // unmodified — exactly like the original per-pair loop above does
        // for s.Triangles[i]. Offsetting them by vertexBase here (an
        // earlier version of this code did) made every index point far
        // outside the local per-layer array TerrainBspBuilder.Rebuild
        // builds, throwing "Index was outside the bounds of the array."
        foreach (var tri in source.Triangles)
            model.Triangles.Add(tri);

        // Real write-back for the "Imported" tab's Shader Setup/State/
        // Texture Scroll UI (SC2GameViewEditorUI.cs) — before this, editing
        // those toggles only ever changed the live in-memory Material,
        // never what gets exported here (source.Materials is parsed once at
        // import time and, until now, copied through completely unchanged).
        // Same ApplyFromScene this project's legacy ExportWindow path
        // already used for texture-path/TextureScroll write-back, now also
        // covering ShaderSetup/State — safe to call unconditionally even
        // when the user made no edits at all, since SC2TerrainMaterialFactory.
        // Create already set every one of these live properties FROM
        // source.Materials' own original ShaderSetup/State at import time,
        // so rebuilding from them is a faithful round-trip, not a
        // destructive overwrite.
        if (t != null)
        {
            var renderer = t.GetComponent<MeshRenderer>();
            if (renderer != null)
                SC2TerrainMaterialExporter.ApplyFromScene(source.Materials, renderer.sharedMaterials, imported.AuthoredEnableRnm, imported.TexturePathOverrides, resolveRawResourcePath, imported.TexturePathForceExport);
        }

        foreach (var mat in source.Materials)
            model.Materials.Add(mat);
    }

    /// <summary>
    /// Copies the actual texture files an imported terrain.bdf's materials
    /// reference into the export output — MergeImportedTerrain only copies
    /// the TerrainMaterial.TexturePaths STRINGS (unchanged, preserving their
    /// original nested layout, e.g. "/textures/Terrain/CA_C03/..."), it
    /// never touched the actual .dds files, so the exported terrain.bdf
    /// referenced textures that simply weren't present anywhere in the
    /// output folder.
    ///
    /// Unlike RegTex above (which flattens painted-pair textures into
    /// "/Textures/<Name>.dds", matching the SC2_D3_crab reference
    /// convention), imported-object textures keep their ORIGINAL path
    /// string as-is in the exported material, so the physical file must be
    /// placed at that exact same relative path under outputDir — not
    /// flattened — for the two to still match up.
    ///
    /// The source bytes come from wherever SC2TerrainBdfImporter.
    /// EnsureTexturesAvailable copied them at import time
    /// (Assets/SC2/Resources/SC2Textures/&lt;path&gt;.dds) — the only copy of
    /// the original file this project necessarily still has access to
    /// (Editor-only: that's a real Unity project path, not something a
    /// build can read either, so this step is a no-op there — same
    /// Editor/Play-mode-only limitation as the import-time copy).
    /// </summary>
    /// <summary>The set of imported-terrain texture base paths (extension
    /// stripped, same normalization CopyImportedTextures uses) whose per-slot
    /// "copy into map on export" box is checked — read from the model AFTER
    /// MergeImportedTerrain/ApplyFromScene ran, so a slot's value is whatever
    /// path actually got exported for it.</summary>
    private static HashSet<string> CollectForceExportBasePaths(SC2ImportedTerrainExportData imported)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (imported?.TexturePathForceExport == null || imported.Model == null) return result;
        foreach (var kv in imported.TexturePathForceExport)
        {
            if (!kv.Value) continue;
            var colon = kv.Key.IndexOf(':');
            if (colon <= 0 || !int.TryParse(kv.Key.Substring(0, colon), out var materialIndex)) continue;
            if (!SC2TerrainMaterialExporter.PathSlotByShaderProp.TryGetValue(kv.Key.Substring(colon + 1), out var slot)) continue;
            if (materialIndex < 0 || materialIndex >= imported.Model.Materials.Count) continue;
            var paths = imported.Model.Materials[materialIndex].TexturePaths;
            if (paths == null || slot >= paths.Length || string.IsNullOrEmpty(paths[slot])) continue;
            var normalized = SC2TerrainMaterialFactory.NormalizePath(paths[slot]);
            result.Add(SC2TerrainMaterialFactory.StripExtension(normalized));
        }
        return result;
    }

    private static void CopyImportedTextures(TerrainModel source, string outputDir, List<string> warnings, HashSet<string> forceExportBasePaths = null)
    {
#if UNITY_EDITOR
        var resourcesRoot = Path.Combine(Application.dataPath, "SC2", "Resources",
            SC2TerrainMaterialFactory.ResourcesTextureRoot.Replace('/', Path.DirectorySeparatorChar));
        var sourceExtensions = new[] { ".dds", ".png", ".tga" };
        var seen = new System.Collections.Generic.HashSet<string>();
        var copied = 0;

        foreach (var rawPath in SC2TerrainBdfImporter.CollectTexturePaths(source))
        {
            var normalized = SC2TerrainMaterialFactory.NormalizePath(rawPath);
            // A texture-slot override (SC2GameViewEditorUI.DrawMaterialTextureSlot)
            // already went through MergeImportedTerrain -> SC2TerrainMaterialExporter.
            // ApplyFromScene -> ResolveRawResourcePath just above, in this same
            // BuildExportArtifacts call, which already copied its real file into
            // this flat "/Textures/<Name>.dds" destination — re-resolving it
            // here against the ORIGINAL imported-map's own nested Resources
            // cache would always miss (it was never a pre-extracted vanilla
            // path to begin with) and log a spurious "not found" warning for a
            // texture that's actually already present and correctly referenced.
            if (normalized.StartsWith("textures/", StringComparison.OrdinalIgnoreCase) &&
                normalized.IndexOf('/', "textures/".Length) < 0)
                continue;
            var basePath = SC2TerrainMaterialFactory.StripExtension(normalized);
            if (!seen.Add(basePath)) continue;

            // A texture served by one of the vanilla game archives is already
            // present in every install — the exported bdf keeps its original
            // game path and nothing is copied, unless this slot's own "copy
            // into map on export" box is checked. Anything not found in a
            // vanilla archive (map-local textures from the imported map's own
            // .scd, or no game install configured) copies exactly as before.
            var forceCopy = forceExportBasePaths != null && forceExportBasePaths.Contains(basePath);
            if (!forceCopy && SC2VanillaArchiveClassifier.IsVanillaGamePath(normalized)) continue;

            string sourceFound = null;
            foreach (var ext in sourceExtensions)
            {
                var candidate = Path.Combine(resourcesRoot, basePath.Replace('/', Path.DirectorySeparatorChar) + ext);
                if (File.Exists(candidate)) { sourceFound = candidate; break; }
            }

            if (sourceFound == null)
            {
                warnings.Add($"imported texture not found for copy: '{normalized}' (looked under {resourcesRoot})");
                continue;
            }

            var destPath = Path.Combine(outputDir, basePath.Replace('/', Path.DirectorySeparatorChar) + Path.GetExtension(sourceFound));
            Directory.CreateDirectory(Path.GetDirectoryName(destPath));
            File.Copy(sourceFound, destPath, overwrite: true);
            copied++;
        }

        SC2Log.Log($"[SC2Exporter] imported textures copied: {copied}");
#else
        warnings.Add("imported-object textures were not copied — Assets/ isn't readable in a standalone build (Editor/Play-mode-only step).");
#endif
    }

    // Export RNM mask files grouped by RnmMaskName — multiple pairs with the
    // same mask name share one set of _0/_1/_2 files.
    //
    // cacheKeyPrefix fixes a real regression where RNM lighting baked as
    // flat gray for the background: the
    // background's own call site passes "bg_" here so its exported mask
    // file/cache-key can never collide with a core pair sharing the same
    // raw RnmMaskName (both commonly land on the class default, "mask01").
    // The PREVIOUS fix baked that prefix directly into `maskName` itself —
    // which also fed the pair-search below, so it was comparing
    // "bg_background" against every pair's own UNPREFIXED RnmMaskName
    // ("background") and never matching anything. maskPair stayed null for
    // every background export, silently falling back to the neutral
    // 128-gray/opaque placeholder for BOTH baked lighting and hand-painted
    // blend alike. Splitting the prefix out fixes this: the search below
    // always uses the real, unprefixed mask name; only the cache key and
    // on-disk filename get the disambiguating prefix.
    private static string ExportRnmMaskByName(
        string maskName,
        SC2TerrainData data,
        string mapName,
        string lightingDir,
        System.Collections.Generic.Dictionary<string,string> cache,
        string cacheKeyPrefix = "")
    {
        var rawSafe = MakeSafe(string.IsNullOrEmpty(maskName) ? "mask01" : maskName);
        var safe = cacheKeyPrefix + rawSafe;
        if (cache.TryGetValue(safe, out var cached)) return cached; // already exported

        // Find first pair that uses this mask name to get its RnmMaskBytes /
        // baked-lighting fields.
        SC2MaterialPair maskPair = null;
        byte[] maskBytes = null;
        int mw = 2048, mh = 2048;
        foreach (var p in data.Pairs)
        {
            if (MakeSafe(p.RnmMaskName ?? "mask01") == rawSafe && p.RnmMaskBytes.Length == p.MaskWidth*p.MaskHeight*4)
            {
                maskPair = p;
                maskBytes = p.RnmMaskBytes;
                mw = p.MaskWidth; mh = p.MaskHeight;
                break;
            }
        }
        // A pair can have a real baked lighting result even if its own
        // RnmMaskBytes (hand-painted blend) is missing/mis-sized — search
        // again by mask name alone for baked data if the loop above found no
        // pair with valid RnmMaskBytes.
        if (maskPair == null)
            foreach (var p in data.Pairs)
                if (MakeSafe(p.RnmMaskName ?? "mask01") == rawSafe) { maskPair = p; mw = p.MaskWidth; mh = p.MaskHeight; break; }

        var n = mw * mh;
        var neutralRgb = new byte[n*3];
        var blendA = new byte[n];
        var fullA = new byte[n];
        for (var i = 0; i < n; i++)
        {
            // Neutral placeholder RGB (128) — used whenever "Bake Lighting"
            // (SC2RnmBakeService) hasn't run yet for this mask. This editor
            // never authors RNM normal data by hand — the paint brush
            // (SC2PaintBrush.shader, _PaintMode==2) only ever lerps toward
            // _PaintColor=(0.5,0.5,0.5,alpha), and BlendRT is cleared to
            // RGB=0.5, so an un-baked mask's RGB is constant 128 and the
            // blend lives entirely in the alpha channel. Reading RGB back
            // from the mask (as this used to) meant that if ANYTHING
            // transient ever ended up in BlendRT's colour channels — e.g. a
            // frame of the editor UI left there by a stray active render
            // target — it got baked straight into the exported _0/_1/_2
            // files, which is exactly the "mask looks like a screenshot of
            // the editor" bug. Writing a constant 128 here is both correct
            // for this editor and immune to any such corruption, regardless
            // of its source.
            neutralRgb[i*3] = neutralRgb[i*3+1] = neutralRgb[i*3+2] = 128;
            blendA[i] = maskBytes != null ? maskBytes[i*4+3] : (byte)0;
            fullA[i]  = 255;
        }

        // Prefers real baked lighting when SC2RnmBakeService has
        // populated it (correctly sized for THIS mask's current resolution) —
        // fall back to the neutral/opaque placeholder above otherwise, so an
        // un-baked map still exports exactly as it always has.
        var rgb0 = maskPair?.RnmBakedRgb0 != null && maskPair.RnmBakedRgb0.Length == n*3 ? maskPair.RnmBakedRgb0 : neutralRgb;
        var rgb1 = maskPair?.RnmBakedRgb1 != null && maskPair.RnmBakedRgb1.Length == n*3 ? maskPair.RnmBakedRgb1 : neutralRgb;
        var rgb2 = maskPair?.RnmBakedRgb2 != null && maskPair.RnmBakedRgb2.Length == n*3 ? maskPair.RnmBakedRgb2 : neutralRgb;
        var shadowAlpha = maskPair?.RnmBakedShadowAlpha != null && maskPair.RnmBakedShadowAlpha.Length == n ? maskPair.RnmBakedShadowAlpha : fullA;

        File.WriteAllBytes(Path.Combine(lightingDir, $"{safe}_0.win.dds"), SC2DdsEncoder.BuildDxt5(mw,mh,rgb0,shadowAlpha));
        File.WriteAllBytes(Path.Combine(lightingDir, $"{safe}_1.win.dds"), SC2DdsEncoder.BuildDxt5(mw,mh,rgb1,blendA));
        File.WriteAllBytes(Path.Combine(lightingDir, $"{safe}_2.win.dds"), SC2DdsEncoder.BuildDxt5(mw,mh,rgb2,fullA));

        var bdfPath = $"/maps/{mapName}/lighting/{safe}.dds";
        cache[safe] = bdfPath;
        return bdfPath;
    }

    /// <summary>Internal (not private) so SC2WaterPlane.BuildWaterMaterial
    /// can sanitize its own default waterDepth.dds path reference the exact
    /// same way every real exported file/folder name already is — see that
    /// method's own doc comment for the bug this fixes.</summary>
    internal static string SanitizeMapName(string s)
    {
        var safe = MakeSafe(s);
        while (safe.IndexOf("..", StringComparison.Ordinal) >= 0) safe = safe.Replace("..", "_");
        safe = safe.Trim('.', ' ', '_');
        if (string.IsNullOrEmpty(safe)) safe = "map";

        switch (safe.ToUpperInvariant())
        {
            case "CON":
            case "PRN":
            case "AUX":
            case "NUL":
            case "COM1":
            case "COM2":
            case "COM3":
            case "COM4":
            case "COM5":
            case "COM6":
            case "COM7":
            case "COM8":
            case "COM9":
            case "LPT1":
            case "LPT2":
            case "LPT3":
            case "LPT4":
            case "LPT5":
            case "LPT6":
            case "LPT7":
            case "LPT8":
            case "LPT9":
                safe = "_" + safe;
                break;
        }

        return safe;
    }

    /// <summary>
    /// Per-vertex Tangent/Binormal for terrain.bdf's packed [+24]/[+28] words
    /// (see TerrainVertex's comment for how that format was established),
    /// computed from the real UV1 derivatives via Lengyel's formula and
    /// accumulated per triangle — the same thing the vanilla data contains:
    /// measured across all 56 vanilla maps, the stored Tangent agrees with the
    /// UV1-derivative tangent on 99.94% of vertices, and the stored Binormal is
    /// the UV1-derivative bitangent NEGATED on 99.62% (a DirectX-style −V
    /// convention).
    ///
    /// A previous revision took a shortcut here: it projected world +X onto the
    /// tangent plane (worldX − N·dot(worldX,N)). That is wrong on any slope
    /// that is not axis-aligned. With this planar UV1 (U = x/TilingScale,
    /// V = z/TilingScale), moving along +U means moving with V — and therefore
    /// world z — held constant, so the tangent's z component must be exactly 0;
    /// the projection instead yields Tz = −Nx·Nz, non-zero on diagonally
    /// oriented slopes. That mis-oriented frame is what made specific mountain
    /// faces flare under specular. The UV-derivative form gets Tz = 0 exactly,
    /// by construction.
    ///
    /// Triangles that are degenerate in UV space (zero-area after
    /// simplification, or a wall where UV1 genuinely does not vary) simply
    /// contribute nothing, so such vertices inherit a frame from their
    /// non-degenerate neighbours instead of snapping to an arbitrary fallback
    /// axis.
    /// </summary>
    private static (Vector3[] tangents, Vector3[] binormals) BuildUv1Tbn(
        Vector3[] verts, int[] tris, Vector3[] normals, float[] uv1U, float[] uv1V)
    {
        var accT = new Vector3[verts.Length];
        var accB = new Vector3[verts.Length];

        for (var i = 0; i + 2 < tris.Length; i += 3)
        {
            int i0 = tris[i], i1 = tris[i + 1], i2 = tris[i + 2];
            if (i0 >= verts.Length || i1 >= verts.Length || i2 >= verts.Length) continue;
            var a = verts[i0];
            var e1 = verts[i1] - a;
            var e2 = verts[i2] - a;

            // UV1 is now SC2TerrainUv1Unwrap's per-axis stretch profile, a
            // genuinely non-uniform mapping (that's the whole point — it
            // stretches proportionally on slopes), so the tangent basis must
            // be derived from the ACTUAL assigned UV1 values per vertex.
            float du1 = uv1U[i1] - uv1U[i0], dv1 = uv1V[i1] - uv1V[i0];
            float du2 = uv1U[i2] - uv1U[i0], dv2 = uv1V[i2] - uv1V[i0];
            var den = du1 * dv2 - du2 * dv1;
            if (Mathf.Abs(den) < 1e-12f) continue;
            var f = 1f / den;

            var t = new Vector3(f * (dv2 * e1.x - dv1 * e2.x),
                                f * (dv2 * e1.y - dv1 * e2.y),
                                f * (dv2 * e1.z - dv1 * e2.z));
            var b = new Vector3(f * (du1 * e2.x - du2 * e1.x),
                                f * (du1 * e2.y - du2 * e1.y),
                                f * (du1 * e2.z - du2 * e1.z));

            accT[i0] += t; accT[i1] += t; accT[i2] += t;
            accB[i0] += b; accB[i1] += b; accB[i2] += b;
        }

        for (var i = 0; i < verts.Length; i++)
        {
            var n = normals[i];
            var t = accT[i];
            // Gram-Schmidt: the averaged tangent is not exactly perpendicular
            // to the averaged normal.
            t -= n * Vector3.Dot(t, n);
            accT[i] = t.sqrMagnitude > 1e-12f
                ? t.normalized
                : Vector3.Cross(Vector3.forward, n).normalized; // no usable UV here at all
            // Stored binormal runs against +V (measured 99.62%).
            accB[i] = accB[i].sqrMagnitude > 1e-12f
                ? (-accB[i]).normalized
                : Vector3.Cross(n, accT[i]).normalized;
        }

        return (accT, accB);
    }

    private static string MakeSafe(string s) =>
        System.Text.RegularExpressions.Regex.Replace(s ?? "", @"[^A-Za-z0-9_\-]", "_");


    private static void ValidateDds(string path, List<string> warns)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var magic = new byte[4]; fs.Read(magic, 0, 4);
            if (magic[0]!=(byte)'D'||magic[1]!=(byte)'D'||magic[2]!=(byte)'S'||magic[3]!=(byte)' ')
                warns.Add($"{Path.GetFileName(path)} — not a DDS! SC2 will not load it. Convert to DXT1/DXT5.");
        }
        catch { }
    }

    /// <summary>
    /// If this SC2TerrainData originated from (or is still linked to) an
    /// imported map, its original hfield.bdf's TerrainTypes are passed
    /// through unchanged — only Heightmap is recomputed from the exported
    /// mesh. Returns null for a from-scratch map with no source hfield,
    /// which SC2HfieldProjectionService.Project treats as "use an all-zero
    /// TerrainTypes array" (nothing meaningful to carry over yet).
    /// </summary>
    private static ushort[] TryReadExistingTerrainTypes(SC2MapRawData rawData)
    {
        if (rawData == null) return null;
        var bytes = rawData.GetHfieldData();
        if (bytes == null || bytes.Length == 0) return null;

        try
        {
            var container = new BdfContainer { Platform = rawData.Platform, Data = bytes, OffsetTable = rawData.GetHfieldOt(), NumFiles = 1 };
            return HfieldBdf.Parse(container).TerrainTypes;
        }
        catch (Exception ex)
        {
            SC2Log.LogWarning($"[SC2Exporter] Could not parse existing hfield for TerrainTypes passthrough: {ex.Message}");
            return null;
        }
    }

    /// <summary>Resolves a blob-backed
    /// document field's bytes, or null if the reference is empty/unset (a
    /// domain the document was never synced for) or no blob store is wired.
    /// Shared by the Costs/Hfield read-from-document paths below — both are
    /// SC2BlobReference-backed (unlike Environment/Collision2/Scenario's
    /// plain scalar/string fields), so reading them back means resolving
    /// through ISC2BlobStore rather than just comparing values directly.</summary>
    private static byte[] TryResolveBlob(ISC2BlobStore blobStore, SC2BlobReference reference)
    {
        if (blobStore == null || reference.IsEmpty)
            return null;
        using var stream = blobStore.OpenRead(reference.ContentHash);
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Builds a costs.bdf CostsModel from SC2TerrainData.CostsBlocks — the
    /// navmesh arrays SC2CostsShell paints into live (see the GameView UI's
    /// Costs tab). Recomputes the border ring (value 5) and islandmap/
    /// islandList from the painted 1/255 binary data, same as
    /// CostsPainterWindow.RecomputeAllIslands and SC2CostsTerrainTypesEditor
    /// do for the existing-map workflow, so this doesn't drift into a third,
    /// slightly-different implementation of the same island logic.
    /// </summary>
    /// <param name="session">Per block,
    /// prefer SC2MapDocument.Costs's FinalGrid blob when SC2CostsShell.
    /// SyncDocumentFromLive has actually populated one for this block's Id —
    /// falls back to data.CostsBlocks[i].Navmesh exactly as before otherwise
    /// (no session, no blob store, or that specific block never synced).</param>
    private static CostsModel BuildCostsModelFromPaintedData(SC2TerrainData data, SC2MapSession session = null)
    {
        var gridSize = data.CostsGridSize;
        var model = new CostsModel();
        for (var i = 0; i < data.CostsBlocks.Count; i++)
        {
            var block = data.CostsBlocks[i];
            var documentBytes = TryResolveBlob(session?.BlobStore, session?.Document?.Costs.Find(block.Name)?.FinalGrid ?? default);
            var navmeshSource = documentBytes != null && documentBytes.Length == gridSize * gridSize
                ? documentBytes
                : block.Navmesh;
            SC2Log.Log($"[SC2Exporter] Costs block '{block.Name}' navmesh (source: " +
                       $"{(navmeshSource == documentBytes ? "SC2MapDocument" : "SC2TerrainData (legacy)")})");
            var navmesh = navmeshSource != null && navmeshSource.Length == gridSize * gridSize
                ? (byte[])navmeshSource.Clone()
                : new byte[gridSize * gridSize];
            if (navmeshSource == null || navmeshSource.Length != gridSize * gridSize)
                for (var c = 0; c < navmesh.Length; c++) navmesh[c] = 1;

            var impassable = SC2CostsAutoGen.NavmeshToImpassable(navmesh);
            SC2CostsAutoGen.ApplyImpassableToNavmesh(impassable, navmesh, gridSize, gridSize);
            // 8-bit index map (0=impassable, 1..254=island), per CostsBdf.cs's
            // verified format comment on CostsBlock.Islandmap — this used to
            // be a uint32-per-cell RGBA color array (4x the real byte size),
            // see SC2IslandMap.cs's own updated doc comment for the history.
            var (islandmap, islandListU32) = SC2IslandMap.Compute(navmesh, gridSize, gridSize);

            model.Blocks.Add(new CostsBlock
            {
                BlockIndex = (uint)i,
                Navmesh = navmesh,
                Islandmap = islandmap,
                LongInts = islandListU32
            });
        }
        return model;
    }

    /// <summary>
    /// Builds a collision2.bdf Collision2Model from the hfield just projected
    /// for this export — never from the raw pre-simplification mesh, same
    /// "must match what's actually shipped" invariant hfield itself follows
    /// (see SC2HfieldProjectionService's doc comment).
    ///
    /// Mesh generation is an error-adaptive Delaunay TIN
    /// (SC2Collision2MeshBuilder), not a uniform downsample grid — a uniform
    /// grid spends the configured triangle budget (default 2500,
    /// user-adjustable 2500-10000) the same everywhere,
    /// which is the wrong tradeoff for a mesh camera collision and air unit
    /// height limits rely on: it gives the worst accuracy exactly where a
    /// player is likely to zoom in (a steep ridge next to a playable area),
    /// no matter how small that feature is relative to the whole map.
    ///
    /// The EDB forest IS a real BSP tree, built via the SAME EdbTreeBuilder
    /// terrain.bdf uses (see its class doc comment for the byte-level
    /// evidence this is the right shared algorithm) — an earlier version of
    /// this method emitted a single flat leaf node instead, based on 3
    /// sample files that turned out to be community re-exports rather than
    /// genuine game data. Checked against 56 real vanilla collision2.bdf
    /// files: every one builds a genuine multi-level tree
    /// (depth 8-16, up to 2248 nodes), just with a much smaller per-leaf
    /// threshold than terrain.bdf's own 200 (real median leaf size is 7,
    /// P90=18) — hence the smaller MinPolysPerLeaf passed below.
    /// </summary>
    /// <param name="model">The FINAL merged terrain model (same one hfield.bdf
    /// was just projected from) — Collision2 projects its OWN second
    /// heightmap from this, separate from the shared hfieldModel every
    /// other consumer (hfield.bdf, Costs, RNM bake) uses, specifically so
    /// data.Collision2ExcludedMaterialNames can drop a decorative material's
    /// complexity from ONLY the collision mesh without also removing it
    /// from hfield.bdf/Costs — see that field's own doc comment.</param>
    /// <param name="existingTerrainTypes">Passed straight through to the
    /// internal projection — has no effect on the resulting mesh (Collision2
    /// doesn't carry TerrainTypes), only kept so the same already-resolved
    /// array callers pass to the main hfield Project call can be reused
    /// as-is here without re-deriving it.</param>
    /// <param name="mapX">Must match the main hfield projection's own mapX
    /// (data.CostsGridSize + 1) so the two heightmaps agree everywhere
    /// except the excluded material's footprint.</param>
    /// <param name="mapY">See mapX.</param>
    /// <param name="heightOffset">SC2TerrainData.Collision2HeightOffset —
    /// uniform vertical shift added to every vertex, e.g. to keep camera/air
    /// collision from clipping the visual terrain at seams (default 0.25).</param>
    /// <param name="data">Supplies Collision2ExcludedMaterialNames (this
    /// method's own filtered projection) and, together with
    /// waterTopElevation, sizes the water quad below (CostsGridSize) — pass
    /// null to skip both (matches the old no-data behavior).</param>
    /// <param name="waterTopElevation">When set, a flat 4-vertex/2-triangle
    /// quad at this Y is baked into the collision mesh across the map's own
    /// playable extent (0,0)-(CostsGridSize,CostsGridSize) — deliberately
    /// NOT the same rectangle as SC2WaterPlane.AppendToExportModel's exported
    /// water material geometry, which is sized by the independent
    /// WaterPlaneSize setting and centered on the map (see that method's own
    /// doc comment) rather than anchored at world origin; this collision
    /// quad only needs to cover the real playable area, not the whole
    /// decorative plane. Ground Hfield/Collision2
    /// TIN generation deliberately still excludes water
    /// (SC2HfieldProjectionService.IsWaterMaterial) so the real ground
    /// beneath it isn't distorted by a flat water plane overwriting its
    /// height — this quad is purely ADDITIVE collision geometry on top of
    /// that, not a height-source change, so naval/amphibious units and
    /// camera collision actually see a collidable water surface instead of
    /// none at all.</param>
    public static Collision2Model BuildCollision2Model(TerrainModel model, ushort[] existingTerrainTypes, int mapX, int mapY, float heightOffset, SC2TerrainData data = null, float? waterTopElevation = null, int maxPolygons = 2500)
    {
        // Collision2's OWN heightmap - deliberately a separate call from
        // the shared hfieldModel every other consumer uses, so
        // Collision2ExcludedMaterialNames only ever affects this mesh (see
        // this method's own param doc / the field's own doc comment for
        // why "excluded from Collision2" must not mean "excluded from
        // hfield.bdf/Costs" too).
        // Getting the INTERIOR of the map's water right took two more
        // iterations - see git history for the water-footprint-mask attempt
        // this replaced. Collision2 now gets its OWN heightmap with water
        // already baked in as real height data, built entirely inside
        // SC2HfieldProjectionService.Project (its bakeWaterElevation param)
        // BEFORE that function's FillGaps hole-filler runs, not as a patch
        // applied afterward here. This matters because FillGaps was written
        // for small rasterization gaps, not entire material-excluded
        // regions: its single left-to-right sweep can propagate one
        // boundary cell's value across an ENTIRE hole in one pass, so a
        // large lake whose low-index edge happens to touch dry shore would
        // otherwise get that shore's height smeared across the whole lake
        // before any later height check could catch it. Baking water first
        // means FillGaps only ever sees genuinely small gaps again.
        // hfield.bdf/Costs/RNM-bake all call Project without this param and
        // are completely unaffected - their heightmap keeps showing every
        // material's real, unflooded height exactly as before.
        //
        // The +10% edge margin computed below is a SEPARATE, unconditional
        // concern, independent of water entirely: it exists so naval/air
        // unit and camera collision doesn't hit an abrupt wall/gap exactly
        // at the map boundary, by extending the mesh a little past it. An
        // earlier version only added this margin when water was involved
        // and filled it with a flat plane at the water's own height — which
        // both skipped the extension for water-less maps entirely, and (for
        // a map edge that isn't itself flat/at water level) baked a visible
        // cliff/step right at the boundary instead of a smooth continuation.
        // Filling the margin by clamping to the real heightmap's own
        // nearest border cell (extending each edge's actual height outward)
        // fixes both: it always runs, and it never disagrees with the real
        // terrain it's extending.
        var bakeWater = waterTopElevation.HasValue && data != null;
        var collisionHfield = SC2HfieldProjectionService.Project(model, existingTerrainTypes, mapX, mapY, data?.Collision2ExcludedMaterialNames, bakeWater ? waterTopElevation : null);

        var meshMapX = (int)collisionHfield.MapX;
        var meshMapY = (int)collisionHfield.MapY;
        // 5% margin on each side = 10% total extra size, matching the
        // requested "map size (1024/2048/etc.) + 10%" cap. Unconditional —
        // independent of water/bakeWater entirely, which only affects
        // whether SC2HfieldProjectionService.Project above baked a flat
        // water surface into the map's INTERIOR height data (a separate
        // concern from this margin). The margin ring is filled by clamping
        // to the real heightmap's own nearest border cell (extending the
        // terrain's own edge height outward), not a flat plane at some
        // unrelated level — a real map's edge is rarely flat, and a
        // mismatched flat shelf would show a cliff/step right at the map
        // boundary instead of a smooth continuation.
        var marginCells = Mathf.Max(1, Mathf.RoundToInt((meshMapX - 1) * 0.05f));
        var paddedSize = (meshMapX - 1) + marginCells * 2 + 1;
        var padded = new ushort[paddedSize * paddedSize];
        for (var gz = 0; gz < paddedSize; gz++)
        {
            var srcZ = Mathf.Clamp(gz - marginCells, 0, meshMapY - 1);
            for (var gx = 0; gx < paddedSize; gx++)
            {
                var srcX = Mathf.Clamp(gx - marginCells, 0, meshMapX - 1);
                padded[gz * paddedSize + gx] = collisionHfield.Heightmap[srcZ * meshMapX + srcX];
            }
        }
        var heightmapForMesh = padded;
        meshMapX = paddedSize;
        meshMapY = paddedSize;

        // maxPolygons is user-adjustable (2500-10000, see SC2Collision2Shell's
        // slider) — 2500 is only the vanilla-observed convention, not a
        // format limit: Collision2Bdf.Serialize writes vertex/triangle
        // counts as plain UInt32 fields with no fixed-size ceiling.
        var raw = SC2Collision2MeshBuilder.Build(heightmapForMesh, meshMapX, meshMapY, maxTriangles: maxPolygons, heightOffset: heightOffset);

        // Undo the padding's world-space shift so the real terrain realigns
        // exactly with world (0,0) again (SC2Collision2MeshBuilder always
        // places grid index 0 at world 0) - the extended edge then correctly
        // reaches into negative territory on the low edges and
        // mapSize+margin on the high edges.
        for (var i = 0; i < raw.Vertices.Count; i++)
        {
            var v = raw.Vertices[i];
            raw.Vertices[i] = (v.X - marginCells, v.Y, v.Z - marginCells);
        }

        var flatVerts = new TerrainVertex[raw.Vertices.Count];
        for (var i = 0; i < raw.Vertices.Count; i++)
        {
            var v = raw.Vertices[i];
            flatVerts[i] = new TerrainVertex { PosX = v.X, PosY = v.Y, PosZ = v.Z };
        }
        var flatTris = new TerrainTriangle[raw.Triangles.Count];
        for (var i = 0; i < raw.Triangles.Count; i++)
        {
            var t = raw.Triangles[i];
            flatTris[i] = new TerrainTriangle { V0 = (uint)t.A, V1 = (uint)t.B, V2 = (uint)t.C };
        }

        // Real collision2.bdf leaves have a median of just 7 triangles
        // (P90=18) across 56 sampled vanilla maps — nothing like
        // terrain.bdf's own 200 convention, so pass a much smaller leaf
        // threshold here even though the split ALGORITHM is identical.
        //
        // useVanillaCandidateSplit: true — per real EXE
        // disassembly, the true vanilla split selector is 9 fixed candidates
        // (X/Y/Z × 0.50/0.25/0.75) scored by abs(lowCount-highCount), not a
        // SAH surface-area sweep — that same research explicitly measured the
        // SAH-sweep default at only ~36% real accuracy and rejected it as the
        // vanilla selector. terrain.bdf's own per-layer BSP always uses this
        // splitter too unconditionally now — see
        // BuildExportArtifacts' own comment on the removed
        // VanillaCandidateBspSplit toggle); this keeps Collision2 on the
        // same, better-validated algorithm instead of the rejected SAH mode.
        // (SC2TerrainBspBuilder.FindBestSahSplit itself is untouched and
        // still used by the legacy Editor/TerrainMeshEditorWindow.cs tool's
        // own default — this change only affects the real export path.)
        const int collision2MinPolysPerLeaf = 8;
        var built = EdbTreeBuilder.Build(flatVerts, flatTris, 0, 0, flatVerts.Length, flatTris.Length, collision2MinPolysPerLeaf, useVanillaCandidateSplit: true);

        var result = new Collision2Model();
        foreach (var v in built.Vertices) result.Vertices.Add((v.PosX, v.PosY, v.PosZ));
        result.Triangles.AddRange(built.Triangles);
        result.Nodes.AddRange(built.Nodes);

        // EdbTreeBuilder never duplicates triangles (verified byte-for-byte
        // against real files — see its own doc comment), so this count is
        // exactly raw.Triangles.Count; kept as a defense-in-depth guard
        // against the CONFIGURED budget, not a hardcoded format ceiling.
        if (result.Triangles.Count > maxPolygons)
            throw new InvalidOperationException($"Collision2 mesh has {result.Triangles.Count} triangles, exceeding the configured budget of {maxPolygons}.");

        var bmin = new float[] { float.MaxValue, float.MaxValue, float.MaxValue };
        var bmax = new float[] { float.MinValue, float.MinValue, float.MinValue };
        foreach (var (x, y, z) in result.Vertices)
        {
            if (x < bmin[0]) bmin[0] = x; if (x > bmax[0]) bmax[0] = x;
            if (y < bmin[1]) bmin[1] = y; if (y > bmax[1]) bmax[1] = y;
            if (z < bmin[2]) bmin[2] = z; if (z > bmax[2]) bmax[2] = z;
        }

        // MapCenter: checked against real vanilla collision2.bdf files —
        // NOT a universal constant (values observed: (256,98,256) on two maps,
        // (512,390,512) on a third, none matching a fixed "map size / 2, y=0"
        // assumption). Its exact runtime semantics are unconfirmed, so the
        // safest choice is the actual generated mesh's own bbox center rather
        // than guessing a hardcoded value that doesn't match any real sample.
        result.MapCenter = result.Vertices.Count > 0
            ? new float[] { (bmin[0] + bmax[0]) / 2f, (bmin[1] + bmax[1]) / 2f, (bmin[2] + bmax[2]) / 2f }
            : new float[] { 512f, 0f, 512f };

        return result;
    }

    /// <summary>
    /// Builds an info.bdf InfoModel from the procedural SC2TerrainData's
    /// EnvironmentHeader/texture-path fields (see SC2EnvironmentShell and
    /// SC2TerrainData.EnvironmentHeader's own doc comments for how those get
    /// populated — a plain 508-byte buffer, byte-identical to InfoModel's own
    /// Header layout, so no field-by-field mapping is needed here).
    ///
    /// SC2MapDocument.Environment.WorkingHeader is
    /// tried FIRST, ahead of data.EnvironmentHeader — SC2EnvironmentShell.
    /// Load()/MaybeSnapshot keep it in lockstep with `data` whenever the
    /// Environment tab has been drawn at least once this session (same
    /// "prefer document, log source, fall back to legacy" pattern already
    /// proven for Collision2/Costs/Hfield). A document header is only
    /// trusted when it's not still the all-zero SC2EnvironmentDocument
    /// default — that's the signal the tab was never opened this session,
    /// in which case the document is simply stale/unpopulated rather than
    /// a genuine "environment reset to zero" edit, so falling back to
    /// data.EnvironmentHeader (and from there to an imported map's original
    /// info.bdf, exactly as before) is the correct behavior.
    ///
    /// Falls back to an imported map's original info.bdf (if data.RawData is
    /// set and the procedural EnvironmentHeader was never touched — still
    /// all-zero) as a passthrough, same "prefer live-edited data, fall back
    /// to whatever an imported map already had" reasoning already used for
    /// TerrainTypes in DoExport above. An all-zero header with no RawData to
    /// fall back to just exports as literally all-zero — same as if a real
    /// info.bdf's EnvironmentHeader happened to be all-zero; nothing invents
    /// non-zero defaults on the exporter's behalf.
    /// </summary>
    /// <param name="quiet">Skips the "info.bdf Environment (source: ...)"
    /// log line — real exports want it (one-shot, diagnostic), but callers
    /// that build an InfoModel every frame for a live preview (e.g.
    /// SC2WaterPreview.Ensure) don't: without this, calling the normal path
    /// every tick spammed the console into an unusable, infinitely-scrolling
    /// log — confirmed a real regression, not just noise.</param>
    public static InfoModel BuildInfoModel(SC2TerrainData data, SC2MapSession session = null, bool quiet = false)
    {
        data.EnsureEnvironmentHeader();

        var documentHeader = session?.Document?.Environment.WorkingHeader;
        var documentHasRealData = documentHeader != null && documentHeader.Length == 508
            && !System.Array.TrueForAll(documentHeader, b => b == 0);
        if (documentHasRealData)
        {
            if (!quiet) SC2Log.Log("[SC2Exporter] info.bdf Environment (source: SC2MapDocument)");
            var env = session.Document.Environment;
            var docModel = new InfoModel();
            System.Array.Copy(documentHeader, docModel.Header, 508);
            docModel.EnvironmentMap = env.EnvironmentMap;
            docModel.SkyMap = env.SkyMap;
            docModel.ReservedTexturePath = env.TexturePath3;
            docModel.ColorCorrectionCube = env.ColorCorrectionCube;
            docModel.FogOfWarCorrectionCube = env.FogOfWarCorrectionCube;
            return docModel;
        }
        if (!quiet) SC2Log.Log("[SC2Exporter] info.bdf Environment (source: SC2TerrainData/legacy)");

        var isUntouched = System.Array.TrueForAll(data.EnvironmentHeader, b => b == 0);
        if (isUntouched && data.RawData != null)
        {
            var rawInfoBytes = data.RawData.GetInfoData();
            if (rawInfoBytes.Length > 0)
            {
                var container = new BdfContainer { Platform = data.RawData.Platform, Data = rawInfoBytes, OffsetTable = data.RawData.GetInfoOt(), NumFiles = 1 };
                return InfoBdf.Parse(container);
            }
        }

        var model = new InfoModel();
        System.Array.Copy(data.EnvironmentHeader, model.Header, 508);
        model.EnvironmentMap = data.EnvironmentMap;
        model.SkyMap = data.SkyMap;
        model.ReservedTexturePath = data.TexturePath3;
        model.ColorCorrectionCube = data.ColorCorrectionCube;
        model.FogOfWarCorrectionCube = data.FogOfWarCorrectionCube;
        return model;
    }

    /// <summary>
    /// Lightweight, geometry-only terrain model for a LIVE preview (Costs
    /// tab-style "Generate" button in the GameView UI, or the Editor
    /// window) — builds the same simplified per-pair meshes
    /// SC2TerrainLayerBuilder.Build already produces, but skips everything
    /// export-only that a preview doesn't need and that would otherwise be
    /// slow or have disk side effects: texture registration/DDS copying,
    /// RNM mask export, and BSP rebuild (hfield/collision2 generation never
    /// reads a layer's BSP nodes — see SC2HfieldProjectionService.Project,
    /// which only walks Vertices/Triangles). Materials only carry the pair
    /// Name (used by SC2HfieldProjectionService.IsWaterMaterial's
    /// name-substring check) — no texture paths.
    ///
    /// NOT suitable for a real terrain.bdf export — use DoExport for that.
    ///
    /// importedTerrains, when supplied, IS now
    /// folded in the same way DoExport/BuildExportArtifacts does (via
    /// MergeImportedTerrain) — a fully-Blender-imported map
    /// (SC2TerrainData.ImportOnlyMode) has SC2TerrainLayerBuilder.Build
    /// return an empty result for the live pairs loop below, so without this
    /// the preview (and therefore SC2Collision2Shell's live "Generate"
    /// button/viewport preview) would silently be empty/wrong for such a
    /// map even though the real final export already correctly includes the
    /// imported geometry. Callers that don't pass importedTerrains keep the
    /// old live-painted-terrain-only behavior.
    /// </summary>
    /// <param name="skipSimplification">Forwarded to SC2TerrainLayerBuilder.
    /// Build — see that parameter's own doc comment. Real callers (the
    /// actual export, Collision2 preview) need the genuine simplified mesh
    /// and must leave this false; SC2LiveHeightGrid's rebuild is the only
    /// caller that only needs accurate triangle positions and opts in.</param>
    public static TerrainModel BuildPreviewGeometryModel(SC2TerrainData data, List<SC2ImportedTerrainExportData> importedTerrains = null, SC2MapSession session = null, bool skipSimplification = false)
    {
        var model = new TerrainModel { Version = 6 };
        if (data == null)
            return model;

        // SC2TerrainLayerBuilder.Build already returns an empty Layers list
        // on its own for every case that should contribute zero live
        // geometry (not initialized, ImportOnlyMode, no pairs painted) - no
        // separate early-return needed here, the loop below just won't run.
        var build = SC2TerrainLayerBuilder.Build(data, sortByTriangleCount: true, null, SC2TerrainSimplifier.FindOuterPerimeterVertices(data), skipSimplification);
        // Pre-size instead of letting List<T>.Add grow these by repeated
        // doubling — build.BuiltVertexCount/BuiltTriangleCount already give
        // the EXACT final count before a single element is added, so this
        // is one correctly-sized allocation instead of ~log2(n) of them,
        // each copying everything added so far. Measured as a real
        // contributor to a large map's per-stroke GC pressure (confirmed
        // via SC2LiveHeightGrid's own GC.CollectionCount instrumentation —
        // see that class's own doc comment on why skipSimplification alone
        // wasn't enough to keep the background rebuild's allocation burst
        // from still stalling the whole process via GC pauses).
        model.Vertices.Capacity = Mathf.Max(model.Vertices.Capacity, build.BuiltVertexCount);
        model.Triangles.Capacity = Mathf.Max(model.Triangles.Capacity, build.BuiltTriangleCount);
        foreach (var builtLayer in build.Layers)
        {
            var pi = builtLayer.PairIndex;
            var pair = data.Pairs[pi];
            var s = builtLayer.Mesh;

            var layer = new BdfIO.Formats.Terrain.TerrainLayer
                { CumulativeVertices = model.Vertices.Count, CumulativeIndices = model.Triangles.Count * 3 };
            model.Layers.Add(layer);

            for (var vi = 0; vi < s.Vertices.Length; vi++)
            {
                var pos = s.Vertices[vi]; var n = s.Normals[vi];
                model.Vertices.Add(new TerrainVertex { PosX = pos.x, PosY = pos.y, PosZ = pos.z, NormalX = n.x, NormalY = n.y, NormalZ = n.z });
            }
            // Local per layer, same as DoExport's main loop — see its comment.
            // SC2HfieldProjectionService.Project (the only consumer of this
            // preview model, via BuildCollision2Preview) already adds
            // layer.CumulativeVertices itself when resolving a triangle's
            // vertex position, so a global index here would double-offset.
            for (var i = 0; i < s.Triangles.Length; i += 3)
                model.Triangles.Add(new TerrainTriangle { V0 = (uint)s.Triangles[i], V1 = (uint)s.Triangles[i + 1], V2 = (uint)s.Triangles[i + 2] });

            model.Materials.Add(new TerrainMaterial { Name = pair.Name });
        }

        // Same merge DoExport/BuildExportArtifacts uses for the real
        // export - see this method's own doc comment above.
        if (importedTerrains != null)
            foreach (var imported in importedTerrains)
                MergeImportedTerrain(model, imported);

        // Without this, the live "Generate" preview's model
        // never had real water geometry at all for a from-scratch/procedural
        // map (only BuildExportArtifacts' real-export path called
        // SC2WaterPlane.AppendToExportModel) - so Collision2's live preview
        // showed no water-driven geometry whatsoever (not even the outer-
        // margin padding's water triangles to build a mask from), while the
        // actual exported collision2.bdf was already correct. Mirrors the
        // real export's own gate (BuildExportArtifacts, ~line 424-427)
        // exactly, so the preview and the real export agree.
        //
        // A follow-up fix: read via BuildInfoModel(data, session),
        // not data.EnvironmentHeader directly - that legacy field is only
        // ever updated when the user clicks "Save Terrain" in the
        // Environment tab (SC2EnvironmentShell.Commit()); a live, uncommitted
        // WaterEnabled/WaterElevation edit lives in session.Document.
        // Environment.WorkingHeader instead (synced every frame by
        // MaybeSnapshot()) and was being silently ignored here, exactly the
        // same class of bug SC2WaterPreview.Ensure already avoids by calling
        // this same BuildInfoModel helper instead of reading the raw field.
        var infoModelForWater = BuildInfoModel(data, session, quiet: true);
        var alreadyHasWater = model.Materials.Exists(SC2HfieldProjectionService.IsWaterMaterial);
        if (infoModelForWater.WaterEnabled != 0 && !alreadyHasWater)
            SC2WaterPlane.AppendToExportModel(model, data, infoModelForWater.WaterElevation);

        return model;
    }

    /// <summary>
    /// End-to-end live preview: painted procedural terrain -> lightweight
    /// geometry model -> projected hfield -> Collision2Model, all without
    /// touching disk. Used by the build-compatible Collision2 shell
    /// (Scripts/Unified/SC2Collision2Shell.cs) for its "Generate" button and
    /// in-viewport preview mesh.
    ///
    /// importedTerrains, when supplied, is
    /// folded into the geometry model the same way the real export does —
    /// see BuildPreviewGeometryModel's own doc comment for why this was
    /// missing before (a fully-Blender-imported map's live "Generate"
    /// button/preview mesh used to be empty/wrong, even though the actual
    /// exported collision2.bdf was always correct).
    /// </summary>
    public static (Collision2Model Model, HfieldModel Hfield) BuildCollision2Preview(SC2TerrainData data, List<SC2ImportedTerrainExportData> importedTerrains = null, SC2MapSession session = null)
    {
        var geomModel = BuildPreviewGeometryModel(data, importedTerrains, session);
        var existingTerrainTypes = data.TerrainTypes != null && data.TerrainTypes.Length == data.CostsGridSize * data.CostsGridSize
            ? data.TerrainTypes
            : null;
        // No material exclusion here - Costs painting/auto-generation and
        // the returned Hfield must see every material's real height; only
        // Collision2's own separate projection inside BuildCollision2Model
        // respects data.Collision2ExcludedMaterialNames.
        var hfieldModel = SC2HfieldProjectionService.Project(geomModel, existingTerrainTypes, data.CostsGridSize + 1, data.CostsGridSize + 1);

        // Read via BuildInfoModel(data, session), not data.EnvironmentHeader
        // directly - see BuildPreviewGeometryModel's own follow-up
        // fix comment above for why: the legacy field only updates on
        // "Save Terrain," so a live, uncommitted WaterElevation edit was
        // being silently ignored here.
        var infoModelForWater = BuildInfoModel(data, session, quiet: true);
        float? waterElevation = infoModelForWater.WaterEnabled != 0 ? infoModelForWater.WaterElevation : (float?)null;

        var collision2Model = BuildCollision2Model(geomModel, existingTerrainTypes, data.CostsGridSize + 1, data.CostsGridSize + 1, data.Collision2HeightOffset, data, waterElevation, data.Collision2PolygonBudget);
        return (collision2Model, hfieldModel);
    }

    /// <summary>
    /// Debug-only PNG dump of exactly what was just serialized into
    /// costs.bdf/hfield.bdf's TerrainTypes — see SC2CostsDebugImageWriter and
    /// SC2CostsTerrainTypesEditor.SaveDebugImages (the equivalent for the
    /// existing-map/Mode A workflow). The palette here is a generic cycling
    /// HSV ramp keyed by id, NOT the same preset labels/colors the GameView
    /// Costs tab shows while painting (that's transient UI-only state, not
    /// persisted on SC2TerrainData) — good enough to tell "which ids are
    /// where", not meant to match the in-viewport overlay pixel-for-pixel.
    /// </summary>
    private static void SaveCostsDebugImages(CostsModel costsModel, ushort[] terrainTypes, string mapDir, int gridSize)
    {
        var outputDir = Path.Combine(mapDir, "CostsDebug");

        var navBlocks = new List<(string, byte[])>();
        var islandBlocks = new List<(string, byte[])>();
        for (var i = 0; i < costsModel.Blocks.Count; i++)
        {
            var name = $"Block_{i}";
            navBlocks.Add((name, costsModel.Blocks[i].Navmesh));
            islandBlocks.Add((name, costsModel.Blocks[i].Islandmap));
        }
        SC2CostsDebugImageWriter.WriteNavmeshPngs(navBlocks, gridSize, gridSize, outputDir);
        SC2CostsDebugImageWriter.WriteIslandmapPngs(islandBlocks, gridSize, gridSize, outputDir);

        if (terrainTypes != null && terrainTypes.Length == gridSize * gridSize)
        {
            SC2CostsDebugImageWriter.WriteTerrainTypesPng(terrainTypes, gridSize, gridSize, outputDir);
        }

        SC2Log.Log($"[SC2Exporter] Costs debug images written to {outputDir}");
    }
}
