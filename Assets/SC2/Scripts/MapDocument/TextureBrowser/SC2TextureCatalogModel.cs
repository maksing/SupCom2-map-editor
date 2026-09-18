using System;
using System.Collections.Generic;

namespace SC2.MapEditor.Textures
{
    public enum SC2TextureSource
    {
        Vanilla,
        Map,
        Custom,
        Generated
    }

    public enum SC2TextureUsage
    {
        Unknown,
        Diffuse,
        Normal,
        Mask,
        Rnm,
        Heightmap,
        Environment,
        Sky,
        ColorCorrectionCube
    }

    [Serializable]
    public sealed class SC2TextureCatalogEntry
    {
        public string Id = "";
        public string Name = "";
        public string ResourcePath = "";
        public string GamePath = "";
        /// <summary>Normalized entry path inside the configured game
        /// install's archive registry (SC2GameArchiveRegistry) - set when
        /// this entry's bytes come from a .scd archive rather than an
        /// AssetDatabase asset (ResourcePath) or a real loose file on disk
        /// (GamePath). Resolve via SC2GameArchiveRegistry.TryGetReader +
        /// reader.OpenEntry(ArchiveEntryPath) - the registry's reader is
        /// already a composite spanning every discovered archive, so this
        /// path alone (no separate "which .scd" field) is enough to find
        /// the entry regardless of which specific archive it lives in.</summary>
        public string ArchiveEntryPath = "";
        public string Category = "";
        public string SearchKey = "";
        public SC2TextureSource Source;
        public SC2TextureUsage Usage;
        public int Width;
        public int Height;
        public int MipCount;
        public bool HasAlpha;
        public string Encoding = "";
        public string ContentHash = "";
        public string SourceHash = "";
        public string ThumbnailResourcePath = "";
        public string ThumbnailFilePath = "";
        public int ThumbnailPage = -1;
        public float ThumbnailX;
        public float ThumbnailY;
        public float ThumbnailWidth;
        public float ThumbnailHeight;
        // Mutually exclusive; both false for an ordinary flat 2D texture.
        // The real resolved asset behind this entry is a Cubemap/Texture3D
        // respectively (see SC2CustomTextureImporter.TryLoadDdsCubemap/
        // TryLoadDdsVolume) — ThumbnailFilePath/ThumbnailResourcePath still
        // always point at a flat 2D preview image regardless, so browsing
        // (grid/details/picker) never needs to draw the non-flat asset
        // directly.
        public bool IsCubemap;
        public bool IsVolume;

        public void Normalize()
        {
            Id = NormalizePath(Id);
            ResourcePath = NormalizePath(ResourcePath);
            GamePath = NormalizePath(GamePath);
            ArchiveEntryPath = NormalizePath(ArchiveEntryPath);
            var pathForNaming = !string.IsNullOrEmpty(ResourcePath) ? ResourcePath : ArchiveEntryPath;
            Name = string.IsNullOrWhiteSpace(Name) ? Leaf(pathForNaming) : Name.Trim();
            Category = string.IsNullOrWhiteSpace(Category) ? CategoryFromPath(pathForNaming) : Category.Trim();
            // Real decal textures turned out to be scattered across dozens of
            // per-map Terrain subfolders, not confined to any single "Decals"
            // folder — audited against a real game install: only 44 of 77
            // actual decal files sit under a folder literally named Decals at
            // all, the rest live inside whichever map's own folder (e.g.
            // "MP_007", "CA_I03") and would otherwise surface under that
            // map's own code-name category instead of being findable as
            // decals at all. A "Decals"-named folder ANYWHERE in the path
            // (case-insensitive) OR the literal word "decal" anywhere in the
            // filename together catch every real decal file found in that
            // audit, including two files inside the real Decals folder whose
            // own names don't contain "decal" at all — except the sparse,
            // mostly-unrelated top-level textures/Decals/ folder (one debug
            // texture, unrelated to real per-map decal content), which is
            // deliberately excluded so it doesn't get lumped in here.
            if (IsDecalPath(pathForNaming))
                Category = "Decals";
            SearchKey = BuildSearchKey(Name, ResourcePath, GamePath, Category, Source, Usage);
        }

        private static bool IsDecalPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            var normalized = NormalizePath(path);
            if (Leaf(normalized).IndexOf("decal", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            var segments = normalized.Split('/');
            for (var i = 0; i < segments.Length - 1; i++) // exclude the leaf filename itself
            {
                if (!string.Equals(segments[i], "Decals", StringComparison.OrdinalIgnoreCase)) continue;
                // The sparse top-level textures/Decals/ folder specifically
                // (i==1, directly under "textures") is excluded — everything
                // else named Decals anywhere in the path counts.
                if (i == 1 && string.Equals(segments[0], "textures", StringComparison.OrdinalIgnoreCase)) continue;
                return true;
            }
            return false;
        }

        public static string BuildStableId(SC2TextureSource source, string path)
        {
            var normalized = NormalizePath(path).ToLowerInvariant();
            return source.ToString().ToLowerInvariant() + ":" + normalized;
        }

        public static string NormalizePath(string path) =>
            (path ?? "").Trim().Replace('\\', '/').TrimStart('/');

        public static string Leaf(string path)
        {
            var normalized = NormalizePath(path);
            var slash = normalized.LastIndexOf('/');
            return slash >= 0 ? normalized.Substring(slash + 1) : normalized;
        }

        public static string CategoryFromPath(string path)
        {
            var normalized = NormalizePath(path);
            var slash = normalized.LastIndexOf('/');
            if (slash <= 0) return "Uncategorized";
            var parent = normalized.Substring(0, slash);
            var parentSlash = parent.LastIndexOf('/');
            return parentSlash >= 0 ? parent.Substring(parentSlash + 1) : parent;
        }

        public static string BuildSearchKey(string name, string resourcePath, string gamePath, string category, SC2TextureSource source, SC2TextureUsage usage) =>
            string.Join(" ", name, resourcePath, gamePath, category, source.ToString(), usage.ToString()).ToLowerInvariant();
    }

    [Serializable]
    public sealed class SC2TextureCatalogData
    {
        public int Version = 2;
        public List<SC2TextureCatalogEntry> Entries = new();
    }

    public sealed class SC2TextureQuery
    {
        public string Search = "";
        public SC2TextureSource? Source;
        public SC2TextureUsage? Usage;
        public string Category = "";
        public bool FavoritesOnly;

        public string CacheKey => string.Join("|",
            (Search ?? "").Trim().ToLowerInvariant(),
            Source?.ToString() ?? "*",
            Usage?.ToString() ?? "*",
            (Category ?? "").Trim().ToLowerInvariant(),
            FavoritesOnly ? "1" : "0");
    }
}
