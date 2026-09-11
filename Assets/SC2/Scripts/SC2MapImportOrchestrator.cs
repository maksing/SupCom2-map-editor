using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;
using BdfIO.Core;
using BdfIO.Formats.Info;
using SC2.MapEditor.Document;

/// <summary>
/// Unified "Import Map" pipeline — the single orchestrator that replaces the
/// legacy "Import .scd" scene-builder, the raw-only "Import .scd (Document)"
/// panel, and the standalone Diagnostics batch tool with one real flow: open
/// a .scd (file or folder) plus any scanned sibling archives as one composite
/// view (SC2MultiSourceContainerReader), find every real map inside it
/// (SC2ScdEntryPointResolver.FindAllScenarioCandidates — bytecode-aware,
/// multi-candidate), then import the selected one by calling each already-
/// correct single-domain importer in turn: SC2TerrainBdfImporter (mesh),
/// SC2ObjectsImport (props/markers/armies/units/chains/areas), info.bdf
/// (Environment), SC2CostsBdfImporter (real Costs navmesh — the one domain
/// with no prior import path at all), SC2DecalsBdfImporter (decals mesh),
/// Collision2 (parsed for preview/reference only — real export always
/// regenerates it from geometry, never from an imported file), and
/// SC2ScenarioLegacyBridge (ScenarioInfo's own fields + lossless _save.lua/
/// _script.lua passthrough blobs).
///
/// No UnityEditor dependency (matches SC2TerrainBdfImporter/SC2ObjectsImport's
/// existing build-compatible pattern) — the same code runs in the Editor and
/// in a standalone Build &amp; Run player.
///
/// Most of the per-domain importers this reuses are path-based (they read a
/// real file off disk), not stream-based, so ImportSelected extracts just the
/// selected map's own small files (BDFs + lua, never textures) to a temp
/// directory and cleans it up afterward — mirroring exactly what the legacy
/// ScdImportWindow/SC2VanillaImportDiagnostics/SC2RuntimeScdImporter already
/// did for the same reason. Textures are the one thing deliberately NOT
/// extracted eagerly: SC2TerrainBdfImporter's own archive-aware fallback
/// (Scripts/SC2TerrainBdfImporter.cs) pulls only the specific texture files a
/// material actually references, straight from the still-open composite
/// reader, so a multi-gigabyte textures.scd never needs full extraction.
/// </summary>
public static class SC2MapImportOrchestrator
{
    public sealed class Stage
    {
        public string Name;
        public bool Ok;
        public string Detail;
    }

    public sealed class Result
    {
        public bool Success;
        public string Summary = "";
        public List<Stage> Stages = new();

        public SC2TerrainBdfImporter.ImportResult TerrainImport;
        public SC2DecalsBdfImporter.ImportResult DecalsImport;
        public BdfIO.Formats.Collision2.Collision2Model Collision2Preview;
        public SC2ScenarioDocument ScenarioDocument;
    }

    /// <summary>Opens primaryPath plus every path in siblingPaths as one
    /// composite view — a single ISC2SourceContainerReader if only the
    /// primary path was given, or a SC2MultiSourceContainerReader spanning
    /// all of them. Caller owns disposal.</summary>
    public static ISC2SourceContainerReader OpenComposite(string primaryPath, IEnumerable<string> siblingPaths)
    {
        var factory = new SC2SourceContainerReaderFactory();
        var readers = new List<ISC2SourceContainerReader> { factory.Open(primaryPath) };
        if (siblingPaths != null)
        {
            foreach (var path in siblingPaths)
            {
                try { readers.Add(factory.Open(path)); }
                catch (Exception ex) { Debug.LogWarning($"[SC2MapImportOrchestrator] Failed to open sibling archive '{path}': {ex.Message}"); }
            }
        }
        return readers.Count == 1 ? readers[0] : new SC2MultiSourceContainerReader(readers);
    }

    /// <summary>Every real map found across the composite reader, bytecode-aware,
    /// not just the first — see SC2ScdEntryPointResolver.FindAllScenarioCandidates'
    /// own doc comment for why this matters (a real archive commonly holds
    /// dozens of maps at once).</summary>
    public static List<SC2ScenarioCandidate> FindCandidates(ISC2SourceContainerReader reader) =>
        SC2ScdEntryPointResolver.FindAllScenarioCandidates(reader);

    /// <summary>Imports the map identified by `candidate` (one entry from
    /// FindCandidates' result) from `reader` into the live `data`/`session`.
    /// `reader` is also handed to SC2TerrainBdfImporter as the texture
    /// fallback source, so it must still be open (not yet disposed) when this
    /// is called. Every stage is wrapped independently — a broken costs.bdf
    /// doesn't block getting the terrain/objects/environment imported, same
    /// pattern SC2VanillaImportDiagnostics already established.</summary>
    public static Result ImportSelected(SC2TerrainData data, SC2MapSession session, ISC2SourceContainerReader reader, SC2ScenarioCandidate candidate)
    {
        var result = new Result();
        if (data == null) { result.Summary = "No active terrain to import into."; return result; }
        if (candidate == null || string.IsNullOrEmpty(candidate.BdfStem)) { result.Summary = "No map selected."; return result; }

        // BdfDirectory (not re-derived from MapPath here) is whatever
        // directory the resolver actually found "{stem}.terrain.win.bdf" in
        // — may differ from MapPath's own directory prefix when the reader
        // is rooted at one map's own folder rather than a full archive/
        // game-install root; see SC2ScenarioCandidate.BdfDirectory's own
        // doc comment for why re-deriving it here would be wrong.
        var bdfDirectory = candidate.BdfDirectory ?? GetDirectory(candidate.MapPath);
        var stem = candidate.BdfStem;
        var tempDir = Path.Combine(Path.GetTempPath(), "sc2_import_" + stem + "_" + Guid.NewGuid().ToString("N").Substring(0, 8));

        try
        {
            Directory.CreateDirectory(tempDir);

            var terrainTempPath = ExtractIfPresent(reader, Combine(bdfDirectory, $"{stem}.terrain.win.bdf"), tempDir, $"{stem}.terrain.win.bdf");
            if (terrainTempPath == null)
            {
                result.Stages.Add(new Stage { Name = "terrain.bdf", Ok = false, Detail = "Not found in any scanned archive." });
                result.Summary = "Import aborted: terrain.bdf not found.";
                return result;
            }

            RunStage(result, "terrain.bdf", () =>
            {
                var terrainImport = SC2TerrainBdfImporter.Import(terrainTempPath, reader);
                result.TerrainImport = terrainImport;
                return $"{terrainImport.Model.Materials.Count} material(s), {terrainImport.Model.Vertices.Count} vert(s)";
            });

            var mapObjsTempPath = ExtractIfPresent(reader, Combine(bdfDirectory, $"{stem}.mapobjs.win.bdf"), tempDir, $"{stem}.mapobjs.win.bdf");
            var saveLuaTempPath = ExtractIfPresent(reader, NormalizeOrNull(candidate.SavePath), tempDir, $"{stem}_save.lua");
            var scenarioLuaTempPath = ExtractIfPresent(reader, candidate.ScenarioEntryPath, tempDir, $"{stem}_scenario.lua");
            var scriptLuaTempPath = ExtractIfPresent(reader, NormalizeOrNull(candidate.ScriptPath), tempDir, $"{stem}_script.lua");

            if (mapObjsTempPath != null || saveLuaTempPath != null || scenarioLuaTempPath != null)
            {
                RunStage(result, "Objects (mapobjs/_save.lua/_scenario.lua)", () =>
                {
                    var objResult = SC2ObjectsImport.ImportFromTerrainBdfPath(data, terrainTempPath);
                    if (!objResult.Success) throw new InvalidOperationException(objResult.Summary);
                    // Real original-bytes source for Smart
                    // Export's Objects passthrough — same reasoning as
                    // Costs.OriginalBdf. mapObjsTempPath can be null (a real
                    // map found via candidate.SavePath/ScenarioEntryPath but
                    // with no mapobjs.bdf of its own), in which case there's
                    // nothing to store — Objects simply keeps no original.
                    if (mapObjsTempPath != null && session?.BlobStore != null)
                    {
                        using var stream = new MemoryStream(File.ReadAllBytes(mapObjsTempPath));
                        session.Document.Objects.OriginalBdf = session.BlobStore.Put(stream);
                    }
                    return objResult.Summary + (objResult.Warnings.Count > 0 ? " (" + string.Join("; ", objResult.Warnings) + ")" : "");
                });
            }
            else
            {
                result.Stages.Add(new Stage { Name = "Objects (mapobjs/_save.lua/_scenario.lua)", Ok = false, Detail = "No mapobjs.bdf/_save.lua/_scenario.lua found — Props/Markers/Armies/Units/Chains/Areas left unchanged." });
            }

            var infoTempPath = ExtractIfPresent(reader, Combine(bdfDirectory, $"{stem}.info.win.bdf"), tempDir, $"{stem}.info.win.bdf");
            if (infoTempPath != null)
            {
                RunStage(result, "info.bdf (Environment)", () =>
                {
                    var infoModel = InfoBdf.Parse(BdfContainer.Read(File.ReadAllBytes(infoTempPath), InfoBdf.Padding));
                    data.EnvironmentHeader = (byte[])infoModel.Header.Clone();
                    data.EnvironmentMap = infoModel.EnvironmentMap;
                    data.SkyMap = infoModel.SkyMap;
                    data.TexturePath3 = infoModel.ReservedTexturePath;
                    data.ColorCorrectionCube = infoModel.ColorCorrectionCube;
                    data.FogOfWarCorrectionCube = infoModel.FogOfWarCorrectionCube;
                    // Also re-sync the document's WorkingHeader, not just the
                    // legacy field — GetWaterElevation/IsWaterEnabled (used to
                    // gate prop placement over water, and Auto Path Node's
                    // land/water resolution) both PREFER
                    // Session.Document.Environment.WorkingHeader over
                    // data.EnvironmentHeader when a session exists. Without
                    // this, WorkingHeader stays stuck at whatever it was
                    // synced to when the terrain was first created/loaded
                    // (typically an all-zero header from SC2TerrainShell.
                    // SetData, called BEFORE Import Map ever runs) — so on
                    // every imported map, water-gated checks silently kept
                    // reading WaterEnabled=0 from the stale document header
                    // even though the real imported map has water, letting
                    // props be placed on/under water. Session can be null
                    // when this orchestrator is invoked without a live
                    // document (e.g. Diagnostics' report-only mode) —
                    // data.EnvironmentHeader alone still gets used by those
                    // checks in that case.
                    if (session?.Document?.Environment != null)
                        System.Array.Copy(data.EnvironmentHeader, session.Document.Environment.WorkingHeader,
                            SC2EnvironmentDocument.HeaderSize);
                    // States the real parsed values directly, so it's visible
                    // whether THIS stage genuinely found water data or not,
                    // independent of whatever SC2ObjectsShell's own [SC2
                    // Water Check] log reports at placement time.
                    Debug.Log($"[SC2 Import] info.bdf parsed: WaterEnabled={infoModel.WaterEnabled}, WaterElevation={infoModel.WaterElevation:0.##}, sessionAvailable={session?.Document?.Environment != null}.");
                    return "Environment header applied.";
                });
            }
            else
            {
                result.Stages.Add(new Stage { Name = "info.bdf (Environment)", Ok = false, Detail = "Not found — Environment left unchanged." });
            }

            // hfield.bdf carries TWO independent things, not
            // one — a heightmap (Heightmap, genuinely re-derivable from the
            // imported terrain mesh via SC2HfieldProjectionService/
            // SC2LiveHeightGrid, correctly still never imported here) AND a
            // separate, real AUTHORED surface-type grid (TerrainTypes —
            // Runtime/BdfIO/Formats/Hfield/HfieldBdf.cs:21, one ushort per
            // cell, e.g. Default vs Chasm_Bottom) that is NOT derivable from
            // geometry at all, exactly like Costs' navmesh. The original
            // "skip hfield.bdf entirely" decision only accounted for the
            // heightmap half and silently dropped real painted TerrainTypes
            // data on import.
            var hfieldTempPath = ExtractIfPresent(reader, Combine(bdfDirectory, $"{stem}.hfield.win.bdf"), tempDir, $"{stem}.hfield.win.bdf");
            if (hfieldTempPath != null)
            {
                RunStage(result, "hfield.bdf (TerrainTypes only — heightmap re-derived from geometry)", () =>
                {
                    var hfieldBytes = File.ReadAllBytes(hfieldTempPath);
                    var hfieldModel = BdfIO.Formats.Hfield.HfieldBdf.Parse(BdfContainer.Read(hfieldBytes, BdfIO.Formats.Hfield.HfieldBdf.Padding));
                    var gridSize = (int)hfieldModel.MapX - 1;
                    if (gridSize <= 0 || hfieldModel.MapX != hfieldModel.MapY)
                        throw new InvalidOperationException($"hfield.bdf has a non-square grid ({hfieldModel.MapX}x{hfieldModel.MapY}) — TerrainTypes import skipped.");
                    var expectedCells = gridSize * gridSize;
                    if (hfieldModel.TerrainTypes.Length != expectedCells)
                        throw new InvalidOperationException($"hfield.bdf TerrainTypes length ({hfieldModel.TerrainTypes.Length}) doesn't match its own grid ({gridSize}x{gridSize}={expectedCells}).");

                    // Costs' Navmesh grid and TerrainTypes share the exact
                    // same CostsGridSize-square registration (see
                    // SC2TerrainData.EnsureCostsAndTerrainTypes, which sizes
                    // both off the same `cells` value) — this stage runs
                    // before the costs.bdf stage below, which sets its own
                    // (should always agree for a real, self-consistent map).
                    data.CostsGridSize = gridSize;
                    data.TerrainTypes = (ushort[])hfieldModel.TerrainTypes.Clone();
                    return $"Imported TerrainTypes, grid {gridSize}x{gridSize} ({expectedCells} cells).";
                });
            }
            else
            {
                result.Stages.Add(new Stage { Name = "hfield.bdf (TerrainTypes only)", Ok = false, Detail = "Not found — TerrainTypes left unchanged." });
            }

            var costsTempPath = ExtractIfPresent(reader, Combine(bdfDirectory, $"{stem}.costs.win.bdf"), tempDir, $"{stem}.costs.win.bdf");
            if (costsTempPath != null)
            {
                RunStage(result, "costs.bdf (real Costs navmesh)", () =>
                {
                    var costsResult = SC2CostsBdfImporter.Import(data, costsTempPath);
                    if (!costsResult.Success) throw new InvalidOperationException(costsResult.Summary);
                    // Real original-bytes source for Smart
                    // Export's Costs passthrough — Costs is real, hand-
                    // painted navmesh data (unlike Hfield's heightmap half
                    // or Collision2's mesh, both always regenerated from
                    // geometry by design), so a genuine "this is exactly
                    // what was imported, nothing has touched it since"
                    // passthrough is meaningful here. Whole-file blob (not
                    // per-layer) so re-export matches the real original
                    // byte-for-byte, sidestepping any drift from fields this
                    // importer doesn't round-trip (Islandmap/LongInts).
                    if (session?.BlobStore != null)
                    {
                        using var stream = new MemoryStream(File.ReadAllBytes(costsTempPath));
                        session.Document.Costs.OriginalBdf = session.BlobStore.Put(stream);
                    }
                    return costsResult.Summary;
                });
            }
            else
            {
                result.Stages.Add(new Stage { Name = "costs.bdf (real Costs navmesh)", Ok = false, Detail = "Not found — Costs left unchanged (auto-generate or paint by hand)." });
            }

            var collision2TempPath = ExtractIfPresent(reader, Combine(bdfDirectory, $"{stem}.collision2.win.bdf"), tempDir, $"{stem}.collision2.win.bdf");
            if (collision2TempPath != null)
            {
                RunStage(result, "collision2.bdf (preview/reference only)", () =>
                {
                    var bytes = File.ReadAllBytes(collision2TempPath);
                    var container = BdfContainer.Read(bytes, BdfIO.Formats.Collision2.Collision2Bdf.Padding);
                    var model = BdfIO.Formats.Collision2.Collision2Bdf.Parse(container);
                    result.Collision2Preview = model;
                    return $"{model.Vertices.Count} vert(s), {model.Triangles.Count} tri(s) — real export always regenerates Collision2 from geometry, this is reference-only.";
                });
            }
            else
            {
                result.Stages.Add(new Stage { Name = "collision2.bdf (preview/reference only)", Ok = false, Detail = "Not present — not required (regenerated from geometry on export)." });
            }

            var decalsTempPath = ExtractIfPresent(reader, Combine(bdfDirectory, $"{stem}.decals.win.bdf"), tempDir, $"{stem}.decals.win.bdf");
            if (decalsTempPath != null)
            {
                RunStage(result, "decals.bdf", () =>
                {
                    var decalsImport = SC2DecalsBdfImporter.Import(decalsTempPath, reader);
                    result.DecalsImport = decalsImport;
                    return decalsImport.Model.HasDecals ? "Decal mesh imported." : "No decals in this map.";
                });
            }
            else
            {
                result.Stages.Add(new Stage { Name = "decals.bdf", Ok = false, Detail = "Not present." });
            }

            if (session != null && session.BlobStore != null && (saveLuaTempPath != null || scenarioLuaTempPath != null || scriptLuaTempPath != null))
            {
                RunStage(result, "Scenario metadata + lossless _save.lua/_script.lua passthrough", () =>
                {
                    var scenarioBytes = scenarioLuaTempPath != null ? File.ReadAllBytes(scenarioLuaTempPath) : Array.Empty<byte>();
                    var saveBytes = saveLuaTempPath != null ? File.ReadAllBytes(saveLuaTempPath) : Array.Empty<byte>();
                    var scriptBytes = scriptLuaTempPath != null ? File.ReadAllBytes(scriptLuaTempPath) : Array.Empty<byte>();
                    var scenarioDoc = SC2ScenarioLegacyBridge.BuildFromLegacy(scenarioBytes, saveBytes, scriptBytes, session.BlobStore);
                    session.Document.Scenario = scenarioDoc;
                    result.ScenarioDocument = scenarioDoc;

                    // _script.lua is never fully parsed (real mission-logic
                    // Lua needing live engine globals — see
                    // SC2ScenarioLegacyBridge's own doc comment), but its one
                    // real per-map setting, SetPlayableArea('AREA_01'), is a
                    // plain string literal that survives Lua->bytecode
                    // compilation unchanged (constant pools keep string
                    // literals as raw bytes) — a byte-level regex finds it
                    // just as reliably as scanning real source text, no Lua
                    // execution needed.
                    var playableArea = FindPlayableAreaName(scriptBytes);
                    if (!string.IsNullOrEmpty(playableArea))
                        data.PlayableAreaName = playableArea;

                    return (scenarioDoc.State == SC2DerivedState.Current
                        ? $"'{scenarioDoc.KnownFields.Name}' ({scenarioDoc.KnownFields.Type})"
                        : $"State={scenarioDoc.State}") +
                        (playableArea != null ? $", PlayableArea='{playableArea}'" : "");
                });
            }

            var ok = result.Stages.Count(s => s.Ok);
            var total = result.Stages.Count;
            result.Success = result.TerrainImport != null;
            result.Summary = $"Imported '{stem}': {ok}/{total} stage(s) succeeded.";
            return result;
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); }
            catch (Exception ex) { Debug.LogWarning($"[SC2MapImportOrchestrator] Failed to clean up '{tempDir}': {ex.Message}"); }
        }
    }

    private static readonly Regex PlayableAreaRe = new(@"SetPlayableArea\(\s*'([^']+)'", RegexOptions.Compiled);

    /// <summary>Finds ScenarioFramework.lua's SetPlayableArea('X') argument in
    /// raw script.lua bytes — works for both plain text AND compiled Lua 5.1
    /// bytecode, since the string literal 'X' is stored as a raw byte
    /// sequence in the bytecode's constant pool either way (confirmed
    /// against a real vanilla _script.lua). Decoded permissively as Latin1
    /// (1 byte = 1 char, never throws on non-UTF8 bytecode bytes) purely to
    /// run the regex — not a claim the whole file is meaningfully "text."</summary>
    private static string FindPlayableAreaName(byte[] scriptBytes)
    {
        if (scriptBytes == null || scriptBytes.Length == 0) return null;
        // ASCII, not UTF8/Latin1: this needs to tolerate arbitrary bytecode
        // bytes without throwing, and the target pattern is pure ASCII —
        // ASCII.GetString replaces any non-ASCII byte with '?' rather than
        // throwing, which is fine here since it can never accidentally
        // produce a false match inside the quoted area name.
        var text = System.Text.Encoding.ASCII.GetString(scriptBytes);
        var m = PlayableAreaRe.Match(text);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static void RunStage(Result result, string name, Func<string> action)
    {
        try
        {
            var detail = action();
            result.Stages.Add(new Stage { Name = name, Ok = true, Detail = detail });
        }
        catch (Exception ex)
        {
            result.Stages.Add(new Stage { Name = name, Ok = false, Detail = ex.Message });
        }
    }

    /// <summary>If entryPath (or, failing that, ANY entry literally named
    /// destFileName) exists in reader, extracts it to tempDir and returns the
    /// resulting real filesystem path; otherwise null. entryPath may itself
    /// be null (an unresolved candidate field), handled the same way as "not
    /// found." The filename fallback matters because scenario.lua's own
    /// save/script fields (like its map field) encode paths relative to a
    /// full archive/game-install root (e.g. "maps/SC2_MP_001/..."), which
    /// don't resolve when the reader is instead rooted directly on one map's
    /// own already-extracted folder — same reasoning as
    /// SC2ScdEntryPointResolver.FindAllScenarioCandidates' own fallback for
    /// terrain.bdf.</summary>
    private static string ExtractIfPresent(ISC2SourceContainerReader reader, string entryPath, string tempDir, string destFileName)
    {
        var entry = string.IsNullOrEmpty(entryPath)
            ? null
            : reader.Entries.FirstOrDefault(e => string.Equals(e.NormalizedPath, entryPath, StringComparison.OrdinalIgnoreCase));

        if (entry == null)
        {
            entry = reader.Entries.FirstOrDefault(e =>
            {
                var slash = e.NormalizedPath.LastIndexOf('/');
                var fileName = slash >= 0 ? e.NormalizedPath.Substring(slash + 1) : e.NormalizedPath;
                return string.Equals(fileName, destFileName, StringComparison.OrdinalIgnoreCase);
            });
        }

        if (entry == null) return null;

        var destPath = Path.Combine(tempDir, destFileName);
        using (var src = reader.OpenEntry(entry.NormalizedPath))
        using (var dst = File.Create(destPath))
            src.CopyTo(dst);
        return destPath;
    }

    private static string NormalizeOrNull(string rawFieldValue)
    {
        if (string.IsNullOrEmpty(rawFieldValue)) return null;
        try { return SC2SafePath.NormalizeRelativeOrThrow(rawFieldValue); }
        catch (SC2UnsafePathException) { return null; }
    }

    private static string GetDirectory(string mapField)
    {
        var normalized = NormalizeOrNull(mapField) ?? "";
        var lastSlash = normalized.LastIndexOf('/');
        return lastSlash >= 0 ? normalized.Substring(0, lastSlash) : "";
    }

    private static string Combine(string directory, string fileName) =>
        string.IsNullOrEmpty(directory) ? fileName : directory + "/" + fileName;
}
