#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using BdfIO.Core;
using BdfIO.Formats.Terrain;
using BdfIO.Formats.Hfield;
using BdfIO.Formats.Costs;
using BdfIO.Formats.Collision2;
using BdfIO.Formats.MapObjs;
using BdfIO.Formats.Info;
using BdfIO.Formats.Decals;

/// <summary>
/// Batch parse-level diagnostics for vanilla .scd maps.
///
/// For every .scd found under a chosen folder: unzip, resolve the map entry
/// point via SC2ScdEntryPointResolver (scenario.lua-driven, falling back to
/// heuristics), parse every BDF container, and parse _save.lua markers —
/// each stage wrapped separately so one broken BDF doesn't hide results for
/// the others, so it's clear which specific BDF or Lua block fails to import
/// and at which stage.
///
/// Deliberately parse-only: it does not create scene GameObjects, materials,
/// or write any assets into the project, so it is safe to run repeatedly
/// over an entire vanilla map folder without polluting Assets/ with dozens
/// of imported map hierarchies. Full scene-build validation (materials,
/// GameObjects, textures) is exercised by the interactive single-map import
/// — ScdImportWindow / the unified shell's Import tab — which uses the same
/// entry-point resolver.
/// </summary>
public static class SC2VanillaImportDiagnostics
{
    private sealed class StageResult
    {
        public string Stage;
        public bool Ok;
        public string Detail;
    }

    private sealed class MapReport
    {
        public string ScdPath;
        public string MapStem;
        public string ResolutionMethod;
        public List<string> Warnings = new List<string>();
        public List<StageResult> Stages = new List<StageResult>();
        public bool HardFailure;
        public string HardFailureMessage;
    }

    [MenuItem("SC2/Diagnostics/Batch Import Vanilla Maps")]
    public static void RunFromMenu()
    {
        var folder = EditorUtility.OpenFolderPanel("Select folder with vanilla .scd maps", "", "");
        if (string.IsNullOrEmpty(folder))
            return;

        var defaultName = "Vanilla_Map_Import_Bugfix_List";
        var reportPath = EditorUtility.SaveFilePanel("Save import diagnostics report", folder, defaultName, "md");
        if (string.IsNullOrEmpty(reportPath))
            return;

        var finalPath = Run(folder, reportPath);
        EditorUtility.DisplayDialog("Batch Import Diagnostics", $"Report written to:\n{finalPath}", "OK");
    }

    /// <summary>
    /// Runs the batch diagnostic pass and writes a Markdown report to
    /// reportPath. Returns reportPath. Exposed as a public static method (not
    /// just the menu item) so it can also be invoked from the unified shell's
    /// Diagnostics tab, or from a test harness.
    /// </summary>
    public static string Run(string scdFolder, string reportPath)
    {
        var scdFiles = Directory.Exists(scdFolder)
            ? Directory.GetFiles(scdFolder, "*.scd", SearchOption.AllDirectories)
            : Array.Empty<string>();
        var reports = new List<MapReport>();

        try
        {
            for (var i = 0; i < scdFiles.Length; i++)
            {
                var file = scdFiles[i];
                EditorUtility.DisplayProgressBar("Batch Import Diagnostics", Path.GetFileName(file), scdFiles.Length > 0 ? (float)i / scdFiles.Length : 1f);
                reports.Add(DiagnoseOne(file));
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }

        var markdown = BuildReport(scdFolder, reports);
        File.WriteAllText(reportPath, markdown, Encoding.UTF8);
        Debug.Log($"[SC2Diagnostics] Batch import report: {reports.Count} map(s) checked -> {reportPath}");
        return reportPath;
    }

    private static MapReport DiagnoseOne(string scdPath)
    {
        var report = new MapReport { ScdPath = scdPath };
        var tmpDir = Path.Combine(Path.GetTempPath(), "sc2_diag_" + Path.GetFileNameWithoutExtension(scdPath) + "_" + Guid.NewGuid().ToString("N").Substring(0, 8));

        try
        {
            Directory.CreateDirectory(tmpDir);

            RunStage(report, "Unzip", () =>
            {
                using (var archive = ZipFile.OpenRead(scdPath))
                {
                    foreach (var entry in archive.Entries)
                    {
                        if (string.IsNullOrEmpty(entry.Name)) continue; // directories
                        var destPath = Path.Combine(tmpDir, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                        Directory.CreateDirectory(Path.GetDirectoryName(destPath));
                        entry.ExtractToFile(destPath, overwrite: true);
                    }
                }
                return $"{Directory.GetFiles(tmpDir, "*", SearchOption.AllDirectories).Length} file(s) extracted";
            }, hardFailure: true);

            if (report.HardFailure)
                return report;

            SC2ScdEntryPointResolver.EntryPointResult entryPoint = null;
            RunStage(report, "Entry point resolution", () =>
            {
                entryPoint = SC2ScdEntryPointResolver.Resolve(tmpDir);
                report.Warnings.AddRange(entryPoint.Warnings);
                if (entryPoint.BdfDirectory == null || entryPoint.BdfStem == null)
                    throw new InvalidOperationException("No *.terrain.win.bdf found anywhere under the extracted archive.");
                report.MapStem = entryPoint.BdfStem;
                report.ResolutionMethod = entryPoint.ResolutionMethod;
                return $"stem='{entryPoint.BdfStem}' via {entryPoint.ResolutionMethod}";
            }, hardFailure: true);

            if (report.HardFailure)
                return report;

            var dir = entryPoint.BdfDirectory;
            var stem = entryPoint.BdfStem;

            RunStage(report, "terrain.bdf", () =>
            {
                var container = ReadContainer(dir, stem, "terrain", TerrainBdf.Padding);
                var model = TerrainBdf.Parse(container);
                return $"{model.Materials.Count} materials, {model.Vertices.Count} verts, {model.Triangles.Count} tris";
            }, hardFailure: true);

            RunStage(report, "hfield.bdf", () =>
            {
                var container = ReadContainer(dir, stem, "hfield", HfieldBdf.Padding);
                var model = HfieldBdf.Parse(container);
                return $"{model.MapX}x{model.MapY}, heightmap={model.Heightmap.Length}";
            }, hardFailure: true);

            RunStage(report, "costs.bdf", () =>
            {
                var container = ReadContainer(dir, stem, "costs", CostsBdf.Padding);
                var model = CostsBdf.Parse(container);
                return $"{model.Blocks.Count} block(s)";
            }, hardFailure: false);

            RunStage(report, "collision2.bdf", () =>
            {
                // Optional file — many maps ship without a manual collision2
                // (it's derived from hfield). Absence is expected, not a failure.
                if (!File.Exists(Path.Combine(dir, $"{stem}.collision2.win.bdf")))
                    return "not present (optional — can be auto-generated from hfield)";

                var container = ReadContainer(dir, stem, "collision2", Collision2Bdf.Padding);
                var model = Collision2Bdf.Parse(container);
                return $"{model.Vertices.Count} verts, {model.Triangles.Count} tris";
            }, hardFailure: false);

            RunStage(report, "mapobjs.bdf", () =>
            {
                var container = ReadContainer(dir, stem, "mapobjs", MapObjsBdf.Padding);
                var model = MapObjsBdf.Parse(container);
                return $"{model.Props.Count} props, {model.Markers.Count} markers, {model.Graphs.Count} graphs";
            }, hardFailure: false);

            RunStage(report, "info.bdf", () =>
            {
                var container = ReadContainer(dir, stem, "info", InfoBdf.Padding);
                InfoBdf.Parse(container);
                return "parsed";
            }, hardFailure: false);

            RunStage(report, "decals.bdf", () =>
            {
                var container = ReadContainer(dir, stem, "decals", DecalsBdf.Padding);
                DecalsBdf.Parse(container);
                return "parsed";
            }, hardFailure: false);

            RunStage(report, "_save.lua markers", () =>
            {
                var saveLuaPath = Path.Combine(dir, $"{stem}_save.lua");
                if (!File.Exists(saveLuaPath))
                    return "no _save.lua next to the BDFs (not necessarily an error)";
                var text = File.ReadAllText(saveLuaPath);
                var parsed = SC2SaveLuaParser.Parse(text);
                return $"{parsed.Markers.Count} lua marker(s)";
            }, hardFailure: false);
        }
        catch (Exception ex)
        {
            report.HardFailure = true;
            report.HardFailureMessage = ex.Message;
        }
        finally
        {
            try { if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true); }
            catch (Exception ex) { Debug.LogWarning($"[SC2Diagnostics] failed to clean up {tmpDir}: {ex.Message}"); }
        }

        return report;
    }

    private static void RunStage(MapReport report, string stageName, Func<string> action, bool hardFailure)
    {
        try
        {
            var detail = action();
            report.Stages.Add(new StageResult { Stage = stageName, Ok = true, Detail = detail });
        }
        catch (Exception ex)
        {
            report.Stages.Add(new StageResult { Stage = stageName, Ok = false, Detail = ex.Message });
            if (hardFailure)
            {
                report.HardFailure = true;
                report.HardFailureMessage = $"{stageName}: {ex.Message}";
            }
        }
    }

    private static BdfContainer ReadContainer(string dir, string stem, string kind, BdfPadding padding)
    {
        var path = Path.Combine(dir, $"{stem}.{kind}.win.bdf");
        var raw = File.ReadAllBytes(path);
        return BdfContainer.Read(raw, padding);
    }

    private static string BuildReport(string scdFolder, List<MapReport> reports)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Vanilla Map Import Bugfix List");
        sb.AppendLine();
        sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Folder scanned: `{scdFolder}`");
        sb.AppendLine($"Maps checked: {reports.Count}");
        var failing = reports.Count(r => r.HardFailure || r.Stages.Any(s => !s.Ok));
        sb.AppendLine($"Maps with at least one failure: {failing}");
        sb.AppendLine();

        if (reports.Count == 0)
        {
            sb.AppendLine("No .scd files found under the scanned folder.");
            return sb.ToString();
        }

        sb.AppendLine("## Summary");
        sb.AppendLine();
        sb.AppendLine("| Map | Status | Stem | Resolved via |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var r in reports.OrderBy(r => r.ScdPath, StringComparer.OrdinalIgnoreCase))
        {
            var status = r.HardFailure ? "HARD FAIL" : r.Stages.Any(s => !s.Ok) ? "PARTIAL" : "OK";
            sb.AppendLine($"| {Path.GetFileName(r.ScdPath)} | {status} | {r.MapStem ?? "-"} | {r.ResolutionMethod ?? "-"} |");
        }
        sb.AppendLine();

        sb.AppendLine("## Per-map detail");
        sb.AppendLine();
        foreach (var r in reports.OrderBy(r => r.ScdPath, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine($"### {Path.GetFileName(r.ScdPath)}");
            sb.AppendLine();
            sb.AppendLine($"- Path: `{r.ScdPath}`");
            if (!string.IsNullOrEmpty(r.MapStem))
                sb.AppendLine($"- Map stem: `{r.MapStem}` (resolved via {r.ResolutionMethod})");
            foreach (var w in r.Warnings)
                sb.AppendLine($"- ⚠️ {w}");
            if (r.HardFailure)
                sb.AppendLine($"- ❌ **HARD FAILURE**: {Escape(r.HardFailureMessage)}");
            sb.AppendLine();

            if (r.Stages.Count > 0)
            {
                sb.AppendLine("| Stage | Status | Detail |");
                sb.AppendLine("|---|---|---|");
                foreach (var s in r.Stages)
                    sb.AppendLine($"| {s.Stage} | {(s.Ok ? "OK" : "FAIL")} | {Escape(s.Detail)} |");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string Escape(string s) => (s ?? "").Replace("|", "\\|").Replace("\n", " ").Replace("\r", " ");
}
#endif
