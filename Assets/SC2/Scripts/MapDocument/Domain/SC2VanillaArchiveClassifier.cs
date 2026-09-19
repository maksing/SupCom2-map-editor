using System;
using System.IO;

namespace SC2.MapEditor.Document
{
    /// <summary>
    /// Decides whether a texture that came out of the configured game
    /// install's archives is "vanilla" — shipped by the base game itself —
    /// and so doesn't need its bytes duplicated into an exported map's own
    /// texture folder (the exported files only need its real in-game path).
    /// Keys off the LITERAL owning .scd file (SC2MultiSourceContainerReader.
    /// FindOwnerSourcePath), not SC2TextureCatalogEntry.Source==Vanilla: that
    /// flag is also set for map-local textures extracted from an imported
    /// map's own archive, which the base game does not have (see the long
    /// comment in SC2TerrainExporter.RegTex on why an earlier flag-based
    /// skip was removed).
    /// </summary>
    public static class SC2VanillaArchiveClassifier
    {
        // Filename stems (no extension), compared case-insensitively. Any
        // other archive's textures must always be copied into the map.
        private static readonly string[] VanillaStems = { "textures", "z_dlc1", "z_dlc1_map_shared" };

        public static bool IsVanillaArchiveStem(string stem)
        {
            if (string.IsNullOrEmpty(stem)) return false;
            return Array.Exists(VanillaStems, s => string.Equals(s, stem, StringComparison.OrdinalIgnoreCase));
        }

        public static bool IsVanillaArchivePath(string archivePath)
        {
            if (string.IsNullOrEmpty(archivePath)) return false;
            return IsVanillaArchiveStem(Path.GetFileNameWithoutExtension(archivePath.TrimEnd('\\', '/')));
        }

        /// <summary>`normalizedEntryPath`: forward-slash, no leading slash —
        /// the same convention as SC2SourceEntryInfo.NormalizedPath /
        /// SC2TextureCatalogEntry.ArchiveEntryPath. For a composite reader
        /// this asks which wrapped archive actually supplied the entry; for a
        /// single-archive reader it trusts the caller that the entry came
        /// from it (mirrors SC2ScdEntryPointResolver.ResolveOwnerArchivePath).</summary>
        public static bool IsVanillaOwnedEntry(ISC2SourceContainerReader reader, string normalizedEntryPath, out string ownerArchivePath)
        {
            ownerArchivePath = null;
            if (reader == null || string.IsNullOrEmpty(normalizedEntryPath)) return false;
            ownerArchivePath = reader is SC2MultiSourceContainerReader multi
                ? multi.FindOwnerSourcePath(normalizedEntryPath)
                : reader.SourcePath;
            return IsVanillaArchivePath(ownerArchivePath);
        }

        public static bool IsVanillaOwnedEntry(string normalizedEntryPath, out string ownerArchivePath)
        {
            ownerArchivePath = null;
            if (!SC2GameArchiveRegistry.TryGetReader(out var reader)) return false;
            return IsVanillaOwnedEntry(reader, normalizedEntryPath, out ownerArchivePath);
        }

        /// <summary>True when an exported map should reference the texture by
        /// its real game path instead of copying its bytes. `forceExport`
        /// (the per-texture checkbox) always wins.</summary>
        public static bool ShouldSkipCopy(ISC2SourceContainerReader reader, string normalizedEntryPath, bool forceExport) =>
            !forceExport && IsVanillaOwnedEntry(reader, normalizedEntryPath, out _);

        public static bool ShouldSkipCopy(string normalizedEntryPath, bool forceExport) =>
            !forceExport && IsVanillaOwnedEntry(normalizedEntryPath, out _);

        /// <summary>The real game-readable path for an archive entry: leading
        /// "/", and the platform ".win" infix stripped from the filename
        /// ("textures/Terrain/x_d.win.dds" -> "/textures/Terrain/x_d.dds") —
        /// no correctly-working path in a real terrain.bdf carries it (same
        /// stripping SC2TerrainExporter.ResolveRawResourcePath already
        /// applies to the names of the copies it writes).</summary>
        public static string ToGamePath(string normalizedEntryPath)
        {
            if (string.IsNullOrEmpty(normalizedEntryPath)) return "";
            var path = normalizedEntryPath.Replace('\\', '/').TrimStart('/');
            var slash = path.LastIndexOf('/');
            var dir = slash >= 0 ? path.Substring(0, slash + 1) : "";
            var file = slash >= 0 ? path.Substring(slash + 1) : path;
            var ext = Path.GetExtension(file);
            var stem = Path.GetFileNameWithoutExtension(file);
            if (stem.EndsWith(".win", StringComparison.OrdinalIgnoreCase)) stem = stem.Substring(0, stem.Length - 4);
            return "/" + dir + stem + ext;
        }

        /// <summary>Finds which archive entry a real game path ("/textures/x/y.dds",
        /// no ".win" infix, as written in an exported bdf) refers to — real
        /// archive entries carry the infix ("y.win.dds"), so both spellings are
        /// tried. False when no configured archive has it.</summary>
        public static bool TryResolveGamePathEntry(ISC2SourceContainerReader reader, string gamePath, out string entryPath, out string ownerArchivePath)
        {
            entryPath = null;
            ownerArchivePath = null;
            if (reader == null || string.IsNullOrEmpty(gamePath)) return false;
            var path = gamePath.Replace('\\', '/').TrimStart('/');
            var ext = Path.GetExtension(path);
            if (string.IsNullOrEmpty(ext)) return false;
            var baseName = path.Substring(0, path.Length - ext.Length);
            foreach (var candidate in new[] { baseName + ".win" + ext, baseName + ext })
            {
                string owner = null;
                if (reader is SC2MultiSourceContainerReader multi)
                    owner = multi.FindOwnerSourcePath(candidate);
                else
                {
                    foreach (var entry in reader.Entries)
                        if (string.Equals(entry.NormalizedPath, candidate, StringComparison.OrdinalIgnoreCase)) { owner = reader.SourcePath; break; }
                }
                if (owner == null) continue;
                entryPath = candidate;
                ownerArchivePath = owner;
                return true;
            }
            return false;
        }

        /// <summary>True when a real game path is served by one of the vanilla
        /// archives (see IsVanillaArchiveStem). False when it isn't found, is
        /// found in any other archive, or no game install is configured —
        /// every "not sure" answer means "copy it into the map," the safe
        /// direction.</summary>
        public static bool IsVanillaGamePath(ISC2SourceContainerReader reader, string gamePath) =>
            TryResolveGamePathEntry(reader, gamePath, out _, out var owner) && IsVanillaArchivePath(owner);

        public static bool IsVanillaGamePath(string gamePath) =>
            SC2GameArchiveRegistry.TryGetReader(out var reader) && IsVanillaGamePath(reader, gamePath);

        private static ISC2SourceContainerReader _slotCacheReader;
        private static readonly System.Collections.Generic.Dictionary<string, bool> SlotPathCache =
            new System.Collections.Generic.Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        /// <summary>UI-facing: is what a texture slot currently points at —
        /// an "archive:&lt;entry&gt;" tag or a real game path, whichever the slot
        /// holds — served by a vanilla archive? Cached per configured reader
        /// (an IMGUI panel asks this for every visible slot every frame; the
        /// cache is dropped whenever the registry hands back a different
        /// reader, i.e. the game install was changed/reopened). A "file:" tag
        /// or empty path is never vanilla.</summary>
        public static bool IsVanillaSlotPath(string slotPath)
        {
            if (string.IsNullOrEmpty(slotPath) || slotPath.StartsWith("file:", StringComparison.Ordinal)) return false;
            if (!SC2GameArchiveRegistry.TryGetReader(out var reader)) return false;
            if (!ReferenceEquals(reader, _slotCacheReader))
            {
                SlotPathCache.Clear();
                _slotCacheReader = reader;
            }
            if (SlotPathCache.TryGetValue(slotPath, out var cached)) return cached;
            var entry = EntryPathFromTag(slotPath);
            var result = entry != null
                ? IsVanillaOwnedEntry(reader, entry, out _)
                : IsVanillaGamePath(reader, slotPath);
            SlotPathCache[slotPath] = result;
            return result;
        }

        /// <summary>True when `resourceTag` ("archive:&lt;entry&gt;") describes the
        /// same texture as the real game path `gamePath` — i.e. the path was
        /// derived from that tag (ToGamePath) and hasn't been changed since.
        /// A slot that stores both a clean path and, separately, the tag it
        /// came from (Environment) uses this to ignore a stale tag after the
        /// path was edited/undone/reset by some other route.</summary>
        public static bool TagMatchesGamePath(string resourceTag, string gamePath)
        {
            var entry = EntryPathFromTag(resourceTag);
            if (entry == null || string.IsNullOrEmpty(gamePath)) return false;
            var normalized = "/" + gamePath.Replace('\\', '/').TrimStart('/');
            return string.Equals(ToGamePath(entry), normalized, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Extracts the archive entry path from a texture resource
        /// tag ("archive:&lt;entryPath&gt;", SC2TerrainMaterialFactory.
        /// TryGetResourcePath's convention), or null for any other tag.</summary>
        public static string EntryPathFromTag(string resourceTag)
        {
            const string prefix = "archive:";
            if (string.IsNullOrEmpty(resourceTag) || !resourceTag.StartsWith(prefix, StringComparison.Ordinal)) return null;
            return resourceTag.Substring(prefix.Length);
        }
    }
}
