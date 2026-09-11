using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using SC2.MapEditor.Document;

/// <summary>
/// Resolves a .scd extraction's map "entry point": finds scenario.lua, reads
/// its map/save/script fields, and derives the BDF filename stem from the
/// .scmap path (e.g. '/maps/SC2_D3_crab/SC2_D3_crab.scmap' -&gt; 'SC2_D3_crab').
///
/// This exists because the importer previously guessed BDF file names by
/// globbing for the first "*.terrain.win.bdf" found anywhere in the archive
/// — which breaks for archives with multiple such files, or where the
/// archive's own file name doesn't match the actual map stem. scenario.lua
/// is the real source of truth for which files belong to the map.
///
/// Deliberately has no UnityEngine dependency — it's pure file/string logic,
/// so it can be exercised by a plain C# test harness outside Unity.
/// </summary>
public static class SC2ScdEntryPointResolver
{
    public struct ScenarioPaths
    {
        public string Map;
        public string Save;
        public string Script;
    }

    public sealed class EntryPointResult
    {
        public string ExtractRoot;
        public string ScenarioLuaPath;
        public string ScenarioLuaText;
        public ScenarioPaths Scenario;
        public string MapStem;
        public string BdfDirectory;
        public string BdfStem;
        public string ResolutionMethod;
        public List<string> Warnings = new List<string>();
    }

    /// <summary>Finds the shallowest scenario.lua under extractRoot, or null
    /// if none exists. Every real vanilla map folder names
    /// this file "&lt;mapname&gt;_scenario.lua" (e.g. SC2_CA_C01_scenario.lua),
    /// never bare "scenario.lua" — confirmed on disk. The old exact-filename
    /// search pattern meant every real map silently fell through to the
    /// heuristic fallback below instead of ever using its real
    /// scenario.lua. Widened to a "*scenario.lua" wildcard, which matches
    /// both conventions.</summary>
    public static string FindScenarioLuaPath(string extractRoot)
    {
        if (!Directory.Exists(extractRoot))
            return null;

        return Directory.EnumerateFiles(extractRoot, "*scenario.lua", SearchOption.AllDirectories)
            .OrderBy(p => p.Count(c => c == '/' || c == '\\'))
            .FirstOrDefault();
    }

    public static ScenarioPaths ParseScenarioPaths(string scenarioLuaText)
    {
        return new ScenarioPaths
        {
            Map = ExtractField(scenarioLuaText, "map"),
            Save = ExtractField(scenarioLuaText, "save"),
            Script = ExtractField(scenarioLuaText, "script")
        };
    }

    /// <summary>Public so other readers of plain-text (uncompiled) scenario.lua
    /// content — e.g. SC2MapImportService's Probe stage, which reads
    /// candidate scenario entries directly from a container reader instead of
    /// an extracted directory — can reuse this instead of re-implementing the
    /// same bare `field = 'value'` extraction.</summary>
    public static string ExtractField(string lua, string field)
    {
        if (string.IsNullOrEmpty(lua))
            return "";

        // (?<![A-Za-z0-9_]) - without this, extracting "name" also matches
        // inside "devname = '...'" (a real, distinct field every vanilla
        // scenario.lua also has), since plain "name\s*=..." has no anchor
        // against being preceded by other identifier characters and Regex.
        // Match just returns the first match in the file, which is
        // "devname" whenever it's declared before "name" - confirmed by a
        // real user report, not a hypothetical: this caused the map picker
        // to show a raw dev-only name instead of the real one for a lot of
        // maps.
        var match = Regex.Match(lua, @"(?<![A-Za-z0-9_])" + Regex.Escape(field) + @"\s*=\s*'([^']*)'");
        return match.Success ? match.Groups[1].Value : "";
    }

    /// <summary>
    /// '/maps/SC2_D3_crab/SC2_D3_crab.scmap' -&gt; 'SC2_D3_crab'. Returns null
    /// if mapField is empty or has no recognizable file name.
    /// </summary>
    public static string ResolveMapStem(string mapField)
    {
        if (string.IsNullOrWhiteSpace(mapField))
            return null;

        var normalized = mapField.Replace('\\', '/').TrimEnd('/');
        var lastSlash = normalized.LastIndexOf('/');
        var fileName = lastSlash >= 0 ? normalized.Substring(lastSlash + 1) : normalized;
        if (fileName.Length == 0)
            return null;

        var dot = fileName.LastIndexOf('.');
        return dot > 0 ? fileName.Substring(0, dot) : fileName;
    }

    /// <summary>
    /// Finds the directory containing "{stem}.terrain.win.bdf" under
    /// extractRoot. Case-insensitive: vanilla archives are inconsistent about case.
    /// </summary>
    public static string FindBdfDirectoryForStem(string extractRoot, string stem)
    {
        if (string.IsNullOrEmpty(stem) || !Directory.Exists(extractRoot))
            return null;

        var target = stem + ".terrain.win.bdf";
        var match = Directory.EnumerateFiles(extractRoot, "*.terrain.win.bdf", SearchOption.AllDirectories)
            .FirstOrDefault(f => string.Equals(Path.GetFileName(f), target, StringComparison.OrdinalIgnoreCase));

        return match != null ? Path.GetDirectoryName(match) : null;
    }

    /// <summary>
    /// Full resolution: try scenario.lua-driven stem lookup first (the
    /// correct source of truth); fall back to "first *.terrain.win.bdf found
    /// anywhere" for archives without a locatable/parsable scenario.lua, so
    /// oddly-packaged archives still resolve instead of hard-failing.
    /// </summary>
    public static EntryPointResult Resolve(string extractRoot)
    {
        var result = new EntryPointResult { ExtractRoot = extractRoot };

        var scenarioPath = FindScenarioLuaPath(extractRoot);
        if (scenarioPath != null)
        {
            result.ScenarioLuaPath = scenarioPath;
            // Real vanilla scenario.lua files are compiled
            // Lua 5.1 bytecode (confirmed: \x1bLuaQ signature), not the plain
            // text this used to assume via File.ReadAllText — every real map
            // silently produced empty/garbled Scenario fields until now.
            // ScenarioLuaText stays populated for the text case (existing
            // callers like ScdImportWindow's own ParseScenarioPaths re-use
            // still expect it); the bytecode case parses via KopiLua instead
            // and skips straight to filling result.Scenario.
            var scenarioBytes = File.ReadAllBytes(scenarioPath);
            if (SC2SaveLuaBytecodeReader.IsBytecode(scenarioBytes))
            {
                result.Scenario = TryReadScenarioPathsFromBytecode(scenarioBytes, result.Warnings);
            }
            else
            {
                result.ScenarioLuaText = File.ReadAllText(scenarioPath);
                result.Scenario = ParseScenarioPaths(result.ScenarioLuaText);
            }
            result.MapStem = ResolveMapStem(result.Scenario.Map);

            if (result.MapStem != null)
            {
                var dir = FindBdfDirectoryForStem(extractRoot, result.MapStem);
                if (dir != null)
                {
                    result.BdfDirectory = dir;
                    result.BdfStem = result.MapStem;
                    result.ResolutionMethod = "scenario.lua";
                    return result;
                }

                result.Warnings.Add(
                    $"scenario.lua's map field resolved stem '{result.MapStem}' but no matching " +
                    $"{result.MapStem}.terrain.win.bdf was found; falling back to heuristic search.");
            }
            else
            {
                result.Warnings.Add("scenario.lua found but its 'map' field is empty or unparsable; falling back to heuristic search.");
            }
        }
        else
        {
            result.Warnings.Add("No scenario.lua found under the extracted archive; falling back to heuristic search.");
        }

        // Fallback: legacy behavior — first *.terrain.win.bdf found anywhere.
        var anyTerrainBdf = Directory.Exists(extractRoot)
            ? Directory.EnumerateFiles(extractRoot, "*.terrain.win.bdf", SearchOption.AllDirectories).FirstOrDefault()
            : null;
        if (anyTerrainBdf == null)
            return result; // BdfDirectory/BdfStem stay null -> caller reports a hard "not found" error

        var name = Path.GetFileName(anyTerrainBdf);
        result.BdfStem = name.Substring(0, name.IndexOf(".terrain.win.bdf", StringComparison.OrdinalIgnoreCase));
        result.BdfDirectory = Path.GetDirectoryName(anyTerrainBdf);
        result.ResolutionMethod = "heuristic (first *.terrain.win.bdf found)";
        return result;
    }

    /// <summary>Reads a compiled scenario.lua's ScenarioInfo.map/save/script
    /// fields via KopiLua (SC2SaveLuaBytecodeReader — the same technique
    /// SC2ObjectsImport/SC2ScenarioLegacyBridge already use for real bytecode
    /// _scenario.lua files). Never throws: a genuinely malformed file falls
    /// back to an empty ScenarioPaths (caller's existing "map field is empty"
    /// branch already handles that correctly), with the parse failure
    /// recorded as a warning instead of aborting the whole resolution.</summary>
    private static ScenarioPaths TryReadScenarioPathsFromBytecode(byte[] scenarioBytes, List<string> warnings)
    {
        try
        {
            var scenarioInfo = SC2SaveLuaBytecodeReader.ReadGlobalTable(scenarioBytes, "ScenarioInfo");
            if (scenarioInfo?.Kind != SC2LuaBytecodeValueKind.Table)
            {
                warnings.Add("scenario.lua bytecode decoded but no 'ScenarioInfo' table was found.");
                return default;
            }
            var fields = scenarioInfo.TableValue.Fields;
            return new ScenarioPaths
            {
                Map = SC2SaveLuaBytecodeReader.AsString(fields.GetValueOrDefault("map")) ?? "",
                Save = SC2SaveLuaBytecodeReader.AsString(fields.GetValueOrDefault("save")) ?? "",
                Script = SC2SaveLuaBytecodeReader.AsString(fields.GetValueOrDefault("script")) ?? "",
            };
        }
        catch (Exception ex)
        {
            warnings.Add("scenario.lua bytecode failed to decode: " + ex.Message);
            return default;
        }
    }

    /// <summary>Bytecode-aware, multi-container-aware sibling of Resolve()
    /// above: searches an ISC2SourceContainerReader (which may itself be a
    /// SC2MultiSourceContainerReader spanning several sibling .scd archives —
    /// e.g. a real game install's maps.scd+lua.scd+textures.scd) for EVERY
    /// "*scenario.lua" entry, not just the first — a real archive commonly
    /// holds dozens of maps at once (confirmed: 47 distinct maps' scenario.lua
    /// counted in one real lua.scd), so returning a single guess is wrong far
    /// more often than it's an edge case. Each candidate independently
    /// resolves its own terrain.bdf directory against the SAME reader (which
    /// may find it in a different underlying archive than the scenario.lua
    /// itself came from).</summary>
    public static List<SC2ScenarioCandidate> FindAllScenarioCandidates(ISC2SourceContainerReader reader)
    {
        var result = new List<SC2ScenarioCandidate>();
        if (reader == null) return result;

        foreach (var entry in reader.Entries)
        {
            var path = entry.NormalizedPath;
            var fileName = path.Substring(path.LastIndexOf('/') + 1);
            if (!fileName.EndsWith("scenario.lua", StringComparison.OrdinalIgnoreCase))
                continue;

            result.Add(ProbeScenarioEntry(reader, path));
        }
        return result;
    }

    private static SC2ScenarioCandidate ProbeScenarioEntry(ISC2SourceContainerReader reader, string scenarioEntryPath)
    {
        var candidate = new SC2ScenarioCandidate
        {
            ScenarioEntryPath = scenarioEntryPath,
            SourceArchivePath = ResolveOwnerArchivePath(reader, scenarioEntryPath)
        };

        byte[] bytes;
        using (var stream = reader.OpenEntry(scenarioEntryPath))
        using (var ms = new MemoryStream())
        {
            stream.CopyTo(ms);
            bytes = ms.ToArray();
        }

        ScenarioPaths scenario;
        string displayName = null;
        candidate.IsCompiledBytecode = SC2SaveLuaBytecodeReader.IsBytecode(bytes);
        if (candidate.IsCompiledBytecode)
        {
            scenario = TryReadScenarioPathsFromBytecode(bytes, new List<string>());
            try
            {
                var scenarioInfo = SC2SaveLuaBytecodeReader.ReadGlobalTable(bytes, "ScenarioInfo");
                if (scenarioInfo?.Kind == SC2LuaBytecodeValueKind.Table)
                    displayName = SC2SaveLuaBytecodeReader.AsString(scenarioInfo.TableValue.Fields.GetValueOrDefault("name"));
            }
            catch { /* best-effort display name only — resolution below still works without it */ }
        }
        else
        {
            var text = Encoding.UTF8.GetString(bytes);
            scenario = ParseScenarioPaths(text);
            displayName = ExtractField(text, "name");
        }

        candidate.DisplayName = string.IsNullOrEmpty(displayName) ? null : displayName;
        candidate.MapPath = scenario.Map;
        candidate.SavePath = scenario.Save;
        candidate.ScriptPath = scenario.Script;

        var stem = ResolveMapStem(scenario.Map);
        if (string.IsNullOrEmpty(scenario.Map))
        {
            candidate.Messages.Add(new SC2ValidationMessage { Severity = SC2ValidationSeverity.Error, Code = "missing-map-field", Message = $"'{scenarioEntryPath}' has no 'map' field." });
            return candidate;
        }
        if (string.IsNullOrEmpty(stem))
        {
            candidate.Messages.Add(new SC2ValidationMessage { Severity = SC2ValidationSeverity.Error, Code = "unresolvable-bdf-stem", Message = $"Could not derive a BDF stem from '{scenarioEntryPath}''s map field ('{scenario.Map}')." });
            return candidate;
        }
        candidate.BdfStem = stem;

        string bdfDirectory;
        try
        {
            var normalizedMap = SC2SafePath.NormalizeRelativeOrThrow(scenario.Map);
            var lastSlash = normalizedMap.LastIndexOf('/');
            bdfDirectory = lastSlash >= 0 ? normalizedMap.Substring(0, lastSlash) : "";
        }
        catch (SC2UnsafePathException ex)
        {
            candidate.Messages.Add(new SC2ValidationMessage { Severity = SC2ValidationSeverity.Error, Code = "unsafe-map-path", Message = $"'{scenarioEntryPath}''s map field is unsafe: {ex.Message}" });
            candidate.CanImport = false;
            return candidate;
        }

        var terrainBdfPath = string.IsNullOrEmpty(bdfDirectory) ? $"{stem}.terrain.win.bdf" : $"{bdfDirectory}/{stem}.terrain.win.bdf";
        var terrainEntry = reader.Entries.FirstOrDefault(e => string.Equals(e.NormalizedPath, terrainBdfPath, StringComparison.OrdinalIgnoreCase));

        if (terrainEntry == null)
        {
            // Fallback, matching the older filesystem-based Resolve()'s own
            // two-tier behavior: the scenario.lua-derived path assumes the
            // reader is rooted at the same place the "map" field's path is
            // relative to (the full archive/game-install root, e.g.
            // "maps/SC2_MP_001/..."). A reader opened directly on one map's
            // own already-extracted folder instead has entries relative to
            // THAT folder (e.g. bare "SC2_MP_001.terrain.win.bdf", no
            // prefix) — search by filename alone before giving up.
            var targetFileName = $"{stem}.terrain.win.bdf";
            terrainEntry = reader.Entries.FirstOrDefault(e =>
                string.Equals(GetFileName(e.NormalizedPath), targetFileName, StringComparison.OrdinalIgnoreCase));
        }

        if (terrainEntry == null)
        {
            candidate.Messages.Add(new SC2ValidationMessage { Severity = SC2ValidationSeverity.Error, Code = "missing-terrain-bdf", Message = $"Required '{terrainBdfPath}' not found in any scanned archive." });
        }
        else
        {
            var lastSlash = terrainEntry.NormalizedPath.LastIndexOf('/');
            candidate.BdfDirectory = lastSlash >= 0 ? terrainEntry.NormalizedPath.Substring(0, lastSlash) : "";
        }

        candidate.CanImport = candidate.Messages.All(m => m.Severity != SC2ValidationSeverity.Error);
        return candidate;
    }

    private static string GetFileName(string normalizedPath)
    {
        var lastSlash = normalizedPath.LastIndexOf('/');
        return lastSlash >= 0 ? normalizedPath.Substring(lastSlash + 1) : normalizedPath;
    }

    /// <summary>Which real archive (.scd file/folder) an entry actually came
    /// from - reader.SourcePath directly for a single archive, or the real
    /// owning archive's SourcePath (via SC2MultiSourceContainerReader.
    /// FindOwnerSourcePath) when reader is a composite spanning several
    /// archives, which is the normal case for a real game install (a
    /// vanilla map's own maps.scd/lua.scd plus however many custom map-pack
    /// .scd files sit alongside them). Lets a caller trace/display which
    /// specific archive a given map candidate was actually found in.</summary>
    private static string ResolveOwnerArchivePath(ISC2SourceContainerReader reader, string entryPath) =>
        reader is SC2MultiSourceContainerReader multi ? multi.FindOwnerSourcePath(entryPath) : reader.SourcePath;
}
