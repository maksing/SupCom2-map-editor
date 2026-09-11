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
            Name = string.IsNullOrWhiteSpace(Name) ? Leaf(!string.IsNullOrEmpty(ResourcePath) ? ResourcePath : ArchiveEntryPath) : Name.Trim();
            Category = string.IsNullOrWhiteSpace(Category) ? CategoryFromPath(!string.IsNullOrEmpty(ResourcePath) ? ResourcePath : ArchiveEntryPath) : Category.Trim();
            SearchKey = BuildSearchKey(Name, ResourcePath, GamePath, Category, Source, Usage);
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
