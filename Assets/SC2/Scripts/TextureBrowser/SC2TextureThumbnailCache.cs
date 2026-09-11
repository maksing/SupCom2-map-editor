using System;
using System.Collections.Generic;
using System.IO;
using SC2.MapEditor.Document;
using SC2.MapEditor.Textures;
using UnityEngine;

public sealed class SC2TextureThumbnailCache
{
    private sealed class Item { public Texture2D Texture; public int LastUse; public bool Owned; }
    private readonly Dictionary<string, Item> _items = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pinned = new(StringComparer.Ordinal);
    private readonly int _capacity;
    private int _clock;

    public SC2TextureThumbnailCache(int capacity = 96) => _capacity = Mathf.Max(16, capacity);
    public int Count => _items.Count;

    public void Pin(string id)
    {
        if (!string.IsNullOrEmpty(id)) _pinned.Add(id);
    }

    public void PinFull(string id)
    {
        if (!string.IsNullOrEmpty(id)) _pinned.Add("full:" + id);
    }

    public Texture2D Get(SC2TextureCatalogEntry entry)
    {
        if (entry == null) return null;
        _clock++;
        var cacheKey = ThumbnailCacheKey(entry);
        if (_items.TryGetValue(cacheKey, out var cached))
        {
            cached.LastUse = _clock;
            return cached.Texture;
        }

        var texture = Load(entry, out var owned);
        if (texture == null) return null;
        _items[cacheKey] = new Item { Texture = texture, LastUse = _clock, Owned = owned };
        Trim();
        return texture;
    }

    public Texture2D GetFull(SC2TextureCatalogEntry entry)
    {
        if (entry == null) return null;
        if (string.IsNullOrEmpty(entry.ThumbnailResourcePath)) return Get(entry);
        var fullEntry = new SC2TextureCatalogEntry
        {
            Id = "full:" + entry.Id,
            ResourcePath = entry.ResourcePath,
            GamePath = entry.GamePath,
            ArchiveEntryPath = entry.ArchiveEntryPath,
            Source = entry.Source,
            // Load() below picks linear-vs-sRGB decoding off Usage — drop
            // this and a bypassed-thumbnail "full preview" would always
            // decode as sRGB regardless of the real entry's usage.
            Usage = entry.Usage,
        };
        return Get(fullEntry);
    }

    public void Clear()
    {
        foreach (var item in _items.Values)
            if (item.Owned && item.Texture != null) UnityEngine.Object.Destroy(item.Texture);
        _items.Clear();
    }

    private static Texture2D Load(SC2TextureCatalogEntry entry, out bool owned)
    {
        owned = false;
        if (!string.IsNullOrEmpty(entry.ThumbnailResourcePath))
        {
            var thumbnail = Resources.Load<Texture2D>(entry.ThumbnailResourcePath);
            if (thumbnail != null) return thumbnail;
        }
        if (!string.IsNullOrEmpty(entry.ThumbnailFilePath) && File.Exists(entry.ThumbnailFilePath))
        {
            var thumbnail = new Texture2D(2, 2, TextureFormat.RGBA32, false) { hideFlags = HideFlags.DontSave };
            if (ImageConversion.LoadImage(thumbnail, File.ReadAllBytes(entry.ThumbnailFilePath), true))
            {
                owned = true;
                return thumbnail;
            }
            UnityEngine.Object.Destroy(thumbnail);
        }
        if (!string.IsNullOrEmpty(entry.ResourcePath))
            return Resources.Load<Texture2D>(entry.ResourcePath);
        // Normal/RNM textures are raw vector/blend data, not color — decoding
        // and previewing them as sRGB (the correct interpretation for every
        // other usage) gamma-distorts every value, confirmed real from a
        // normal-map thumbnail showing visibly different colors/contrast
        // than the same file opened in an external image editor.
        var linear = entry.Usage == SC2TextureUsage.Normal || entry.Usage == SC2TextureUsage.Rnm;
        if (!string.IsNullOrEmpty(entry.GamePath) && File.Exists(entry.GamePath))
        {
            if (SC2CustomTextureImporter.TryLoad(entry.GamePath, out var custom, out _, linear))
                return FilterAndDestroySource(entry, custom, linear, out owned);
            return null;
        }
        if (!string.IsNullOrEmpty(entry.ArchiveEntryPath) && SC2GameArchiveRegistry.TryGetReader(out var reader))
        {
            if (SC2CustomTextureImporter.TryLoadPreviewFromArchive(reader, entry.ArchiveEntryPath, out var archiveTex, out _, linear))
                return FilterAndDestroySource(entry, archiveTex, linear, out owned);
            return null;
        }
        return null;
    }

    /// <summary>These two branches decode straight from a live .dds file/
    /// archive entry with no baked-thumbnail step (unlike the
    /// ThumbnailResourcePath/ThumbnailFilePath branches above, which are
    /// already pre-filtered at bake time) — the raw, full-resolution
    /// decoded texture was drawn directly into a small grid-card rect,
    /// which for real vanilla RNM masks' fine, high-frequency radial detail
    /// aliases into a regular striped/banded pattern (confirmed by the user
    /// after the decoder/orientation fix — mesh rendering, sampled through
    /// the GPU's normal mip pipeline, was already correct). Route through
    /// SC2CustomTextureImporter.BuildFilteredPreview instead of returning
    /// the raw decode, and destroy the temporary full-res source (it's not
    /// cached — only the small filtered result is).
    ///
    /// Real Width/Height/MipCount/Encoding are captured from `source` onto
    /// `entry` before it's destroyed — LoadArchiveLibrary never populates
    /// these (deliberately cheap catalog-load, no per-entry header read),
    /// so without this the Details panel's "Size/Format/Mips" fields (which
    /// fall back to reading them off the drawn texture whenever there's no
    /// baked atlas thumbnail) would report the small filtered preview's own
    /// 160x160/RGBA32/1-mip shape instead of the real file's — confirmed by
    /// the user as a real, if secondary, regression from the filtering fix.</summary>
    private static Texture2D FilterAndDestroySource(SC2TextureCatalogEntry entry, Texture2D source, bool linear, out bool owned)
    {
        if (entry != null && source != null)
        {
            entry.Width = source.width;
            entry.Height = source.height;
            entry.MipCount = source.mipmapCount;
            entry.Encoding = source.format.ToString();
        }
        var filtered = SC2CustomTextureImporter.BuildFilteredPreview(source, linear: linear);
        UnityEngine.Object.Destroy(source);
        owned = filtered != null;
        return filtered;
    }

    private static string ThumbnailCacheKey(SC2TextureCatalogEntry entry) =>
        !string.IsNullOrEmpty(entry.ThumbnailResourcePath)
            ? "thumbnail:" + entry.ThumbnailResourcePath
            : !string.IsNullOrEmpty(entry.ThumbnailFilePath)
                ? "thumbnail-file:" + entry.ThumbnailFilePath
            : entry.Id;

    private void Trim()
    {
        while (_items.Count > _capacity)
        {
            string oldestKey = null;
            var oldestUse = int.MaxValue;
            foreach (var pair in _items)
                if (!_pinned.Contains(pair.Key) && pair.Value.LastUse < oldestUse) { oldestUse = pair.Value.LastUse; oldestKey = pair.Key; }
            if (oldestKey == null) return;
            var item = _items[oldestKey];
            if (item.Owned && item.Texture != null) UnityEngine.Object.Destroy(item.Texture);
            else if (item.Texture != null) Resources.UnloadAsset(item.Texture);
            _items.Remove(oldestKey);
        }
    }
}
