using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace SC2.MapEditor.Textures
{
    /// <summary>Pure, UnityEngine-independent core of the terrain-material
    /// "sibling" suggestion feature (SC2TextureCatalog.FindAllSiblingMatches/
    /// TryFindSiblingMatch, in Scripts/TextureBrowser, are thin wrappers
    /// delegating here with the live catalog's own _byId). Kept separate and
    /// dependency-free specifically so it can be covered by real, fast,
    /// no-Unity-process-required tests (see
    /// Tests~/StandaloneCharacterization/TextureSiblingMatchTests.cs, run via
    /// the standalone dotnet-test project — this file lives under
    /// Scripts/MapDocument, already compiled into that project by its
    /// existing "Scripts\MapDocument\**\*.cs" glob, no project-file change
    /// needed) rather than only exercisable through a live Unity Editor
    /// process with a real game install configured.</summary>
    public static class SC2TextureSiblingMatcher
    {
        // Real vanilla terrain-pair materials are exported as a
        // same-basename, different-suffix set. Audited a real game
        // install's entire textures/Terrain tree (1360 files) to settle
        // this pattern: the original single-letter-only version
        // ("sandstone_001a_d.win.dds"/"_n.win.dds"/"_s.win.dds") matched
        // 1166/1360 files; this version matches 1276/1360 by also
        // accepting the whole-word convention
        // ("C03_CommArray01_Diffuse.win.dds", and real decal atlases -
        // "<map>.decal_atlas_diffuse.win.dds" - which use
        // "_diffuse"/"_normal" instead of "_d"/"_n") and an optional
        // trailing numbered-variant suffix
        // ("Rock_wall_brown_d_001.win.dds"/"_n_001"/"_s_001", and this
        // editor's own generated "decals_diffuse.dds"/"decals_normal.dds").
        // Of the 1276 matches, 1167 belong to a real multi-file family (a
        // sibling genuinely exists on disk to find) - the remaining 109
        // matched-but-solo entries and 84 total non-matches are, on manual
        // inspection, either genuinely standalone single textures
        // (skyboxes, water animation frames, ramp gradient LUTs, one-off
        // decorative props) or belong to the UNIT/PROP material system's
        // own AO/Specular/Reflectivity/SpecMask convention, a different
        // domain from terrain-pair Diffuse/Normal/Mask that this
        // deliberately doesn't reach into. Deliberately excludes
        // ao/specular/reflectivity/specmask for that reason, even though
        // they appear in the same folders.
        // Captures the shared prefix, the matched suffix token, any
        // trailing numbered-variant suffix, the optional ".win" marker,
        // and the real extension, so a candidate for any OTHER usage can
        // be reconstructed by swapping only the token and keeping
        // everything else identical.
        // BuildStableId lowercases the whole path before comparing, so a
        // reconstructed candidate matches its real counterpart regardless
        // of case (vanilla capitalizes this suffix inconsistently -
        // "_Diffuse", "_diffuse", "_Normal" all occur) without needing
        // multiple case variants tried here.
        internal static readonly Regex SiblingSuffixPattern =
            new(@"^(.*)_(d|n|s|m|diffuse|normal|mask)(_?\d*)(\.win)?\.(dds|tga|png)$", RegexOptions.IgnoreCase);

        internal static readonly Dictionary<SC2TextureUsage, string[]> CandidateSuffixes = new()
        {
            [SC2TextureUsage.Diffuse] = new[] { "d", "diffuse" },
            [SC2TextureUsage.Normal] = new[] { "n", "normal" },
            // "_s" and "_m" both occur in real vanilla data - different
            // art folders use different ones for the same packed
            // mask/specular channel.
            [SC2TextureUsage.Mask] = new[] { "s", "m", "mask" },
        };

        /// <summary>Every real catalog match for `targetUsage` built by
        /// swapping just the suffix token on `sibling`'s own path — e.g.
        /// Mask can genuinely have both a "_s" AND a separate "_m" file in
        /// the same real set, so this returns both rather than stopping at
        /// the first. `byId` is the catalog's own id→entry index (already
        /// lowercase-keyed via SC2TextureCatalogEntry.BuildStableId); a
        /// candidate not present there simply isn't in the result. Returns
        /// an empty list (never null) when `sibling` is null, its path
        /// can't be determined, or its path doesn't end in a recognized
        /// suffix — a safe no-op, since callers only ever use this to
        /// pre-select or suggest, never to assert a sibling must exist.</summary>
        public static List<SC2TextureCatalogEntry> FindAll(
            SC2TextureCatalogEntry sibling,
            SC2TextureUsage targetUsage,
            IReadOnlyDictionary<string, SC2TextureCatalogEntry> byId)
        {
            var results = new List<SC2TextureCatalogEntry>();
            if (sibling == null || byId == null) return results;

            // A Custom-sourced entry (e.g. a user-imported decal atlas,
            // unlike a real vanilla one) has empty ArchiveEntryPath AND
            // ResourcePath, only GamePath (its absolute file path).
            var siblingPath = !string.IsNullOrEmpty(sibling.ArchiveEntryPath) ? sibling.ArchiveEntryPath
                : !string.IsNullOrEmpty(sibling.ResourcePath) ? sibling.ResourcePath
                : sibling.GamePath;
            if (string.IsNullOrEmpty(siblingPath)) return results;

            var match = SiblingSuffixPattern.Match(siblingPath);
            if (!match.Success) return results;
            if (!CandidateSuffixes.TryGetValue(targetUsage, out var candidateSuffixes)) return results;

            var prefix = match.Groups[1].Value;
            var trailing = match.Groups[3].Value;
            var winSuffix = match.Groups[4].Value;
            var ext = match.Groups[5].Value;
            foreach (var suffix in candidateSuffixes)
            {
                var candidatePath = prefix + "_" + suffix + trailing + winSuffix + "." + ext;
                var id = SC2TextureCatalogEntry.BuildStableId(sibling.Source, candidatePath);
                if (byId.TryGetValue(id, out var found) && found.Id != sibling.Id && !results.Contains(found))
                    results.Add(found);
            }
            return results;
        }

        public static SC2TextureCatalogEntry FindFirst(
            SC2TextureCatalogEntry sibling,
            SC2TextureUsage targetUsage,
            IReadOnlyDictionary<string, SC2TextureCatalogEntry> byId)
        {
            var all = FindAll(sibling, targetUsage, byId);
            return all.Count > 0 ? all[0] : null;
        }
    }
}
