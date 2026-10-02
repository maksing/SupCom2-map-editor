using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using SC2.MapEditor.Document;
using SC2.MapEditor.Textures;
using UnityEngine;

public sealed class SC2TextureThumbnailCache
{
    private sealed class Item { public Texture2D Texture; public int LastUse; public bool Owned; }
    private readonly Dictionary<string, Item> _items = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pinned = new(StringComparer.Ordinal);
    // Keys whose load already failed: without this a texture that cannot be decoded was
    // re-attempted (archive read + decode) on every repaint for as long as its card was visible.
    private readonly HashSet<string> _failed = new(StringComparer.Ordinal);
    private readonly int _capacity;
    private int _clock;

    public SC2TextureThumbnailCache(int capacity = 96) => _capacity = Mathf.Max(16, capacity);
    public int Count => _items.Count;

    /// <summary>Counts thumbnails decoded from a file or archive (as opposed to the cheap baked
    /// ones) — the grid reads it to spread those loads across frames.</summary>
    public int HeavyLoadCount { get; private set; }

    /// <summary>Counts thumbnails restored from the on-disk cache (a small PNG read — cheap, so the
    /// grid does not hold these back the way it holds back a full decode).</summary>
    public int DiskLoadCount { get; private set; }

    public bool HasFailed(SC2TextureCatalogEntry entry) => entry != null && _failed.Contains(ThumbnailCacheKey(entry));

    public void Pin(string id)
    {
        if (!string.IsNullOrEmpty(id)) _pinned.Add(id);
    }

    public void PinFull(string id)
    {
        if (!string.IsNullOrEmpty(id)) _pinned.Add("full:" + id);
    }

    public Texture2D Get(SC2TextureCatalogEntry entry) => Get(entry, true, true);

    /// <param name="allowHeavyLoad">False returns null for a thumbnail that is not cached yet and
    /// would have to be decoded from a file or archive, without recording a failure — the caller
    /// asks again later. Baked thumbnails (cheap resource loads) are never held back.</param>
    /// <param name="allowDiskLoad">False likewise defers a thumbnail that is waiting in the on-disk
    /// cache, so the caller can cap how many of those small PNG reads happen in one frame.</param>
    public Texture2D Get(SC2TextureCatalogEntry entry, bool allowHeavyLoad, bool allowDiskLoad)
    {
        if (entry == null) return null;
        _clock++;
        var cacheKey = ThumbnailCacheKey(entry);
        if (_items.TryGetValue(cacheKey, out var cached))
        {
            cached.LastUse = _clock;
            return cached.Texture;
        }

        if (_failed.Contains(cacheKey)) return null;

        var texture = Load(entry, allowHeavyLoad, allowDiskLoad, out var owned, out var deferred);
        if (deferred) return null;
        if (texture == null) { _failed.Add(cacheKey); return null; }
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

    /// <summary>Drops (and destroys, if owned) the cached thumbnail for an
    /// entry that no longer exists.</summary>
    public void Evict(SC2TextureCatalogEntry entry)
    {
        if (entry == null) return;
        var key = ThumbnailCacheKey(entry);
        if (_items.TryGetValue(key, out var item))
        {
            if (item.Owned && item.Texture != null) UnityEngine.Object.Destroy(item.Texture);
            _items.Remove(key);
        }
        _failed.Remove(key);
        _pinned.Remove(key);
        _pinned.Remove(entry.Id);
        _pinned.Remove("full:" + entry.Id);
    }

    public void Clear()
    {
        foreach (var item in _items.Values)
            if (item.Owned && item.Texture != null) UnityEngine.Object.Destroy(item.Texture);
        _items.Clear();
        _failed.Clear();
    }

    private Texture2D Load(SC2TextureCatalogEntry entry, bool allowHeavyLoad, bool allowDiskLoad, out bool owned, out bool deferred)
    {
        owned = false;
        deferred = false;
        if (!string.IsNullOrEmpty(entry.ThumbnailResourcePath))
        {
            var thumbnail = Resources.Load<Texture2D>(entry.ThumbnailResourcePath);
            if (thumbnail != null) return thumbnail;
        }
        var diskStem = DiskCacheStem(entry);
        if (diskStem != null && DiskIndex.Contains(diskStem))
        {
            if (!allowDiskLoad) { deferred = true; return null; }
            if (TryLoadFromDisk(entry, diskStem, out var fromDisk))
            {
                owned = true;
                return fromDisk;
            }
        }
        // Everything below reads and decodes a file or archive entry on the calling thread.
        if (!allowHeavyLoad) { deferred = true; return null; }
        HeavyLoadCount++;
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
            {
                var filtered = FilterAndDestroySource(entry, custom, linear, out owned);
                StoreOnDisk(entry, diskStem, filtered);
                return filtered;
            }
            return null;
        }
        if (!string.IsNullOrEmpty(entry.ArchiveEntryPath) && !SC2GameArchiveRegistry.TryGetReader(out _))
        {
            // The archives are not available yet — try again later instead of recording a failure.
            deferred = true;
            return null;
        }
        if (!string.IsNullOrEmpty(entry.ArchiveEntryPath) && SC2GameArchiveRegistry.TryGetReader(out var reader))
        {
            if (SC2CustomTextureImporter.TryLoadPreviewFromArchive(reader, entry.ArchiveEntryPath, out var archiveTex, out _, linear))
            {
                var filtered = FilterAndDestroySource(entry, archiveTex, linear, out owned);
                StoreOnDisk(entry, diskStem, filtered);
                return filtered;
            }
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

    // ---- On-disk thumbnail cache ----
    // Decoding a texture from an archive or file (inflate, upload, GPU filter, readback) is by far
    // the slowest part of filling the grid, and the result is the same every session. The filtered
    // 160px preview is kept as a PNG next to a small text sidecar holding the source's real
    // size/mips/format (the Details panel reports those, and the PNG no longer has them). A cached
    // file is keyed by the source path, whether it is decoded as linear data, and the size and
    // modification time of the file or archives it came from, so a changed install gets fresh
    // thumbnails instead of stale ones.
    private readonly Dictionary<string, string> _diskStems = new(StringComparer.Ordinal);
    private HashSet<string> _diskIndex;
    private ISC2SourceContainerReader _archiveStampReader;
    private string _archiveStamp;

    private static string DiskCacheDirectory => Path.Combine(Application.persistentDataPath, "TextureThumbCache");

    private HashSet<string> DiskIndex
    {
        get
        {
            if (_diskIndex != null) return _diskIndex;
            _diskIndex = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                if (Directory.Exists(DiskCacheDirectory))
                    foreach (var file in Directory.EnumerateFiles(DiskCacheDirectory, "*.png"))
                        _diskIndex.Add(Path.GetFileNameWithoutExtension(file));
            }
            catch (Exception) { /* an unreadable cache folder just means no cache */ }
            return _diskIndex;
        }
    }

    /// <summary>The cache file name (without extension) for an entry that is decoded from a file or
    /// archive, or null when this entry is not cacheable (baked thumbnail, resource texture, or the
    /// archives are not available yet).</summary>
    private string DiskCacheStem(SC2TextureCatalogEntry entry)
    {
        var key = ThumbnailCacheKey(entry);
        if (_diskStems.TryGetValue(key, out var known)) return known;
        if (!string.IsNullOrEmpty(entry.ThumbnailFilePath) || !string.IsNullOrEmpty(entry.ResourcePath)) return _diskStems[key] = null;

        string source;
        string stamp;
        if (!string.IsNullOrEmpty(entry.GamePath) && File.Exists(entry.GamePath))
        {
            source = "file:" + entry.GamePath;
            try
            {
                var info = new FileInfo(entry.GamePath);
                stamp = info.Length + ":" + info.LastWriteTimeUtc.Ticks;
            }
            catch (Exception) { return _diskStems[key] = null; }
        }
        else if (!string.IsNullOrEmpty(entry.ArchiveEntryPath))
        {
            if (!SC2GameArchiveRegistry.TryGetReader(out var reader)) return null; // not decided yet, ask again later
            source = "archive:" + entry.ArchiveEntryPath;
            stamp = ArchiveStamp(reader);
        }
        else return _diskStems[key] = null;

        var linear = entry.Usage == SC2TextureUsage.Normal || entry.Usage == SC2TextureUsage.Rnm;
        using var sha = SHA1.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(source + "|" + linear + "|" + stamp));
        var stem = new StringBuilder(hash.Length * 2);
        foreach (var b in hash) stem.Append(b.ToString("x2"));
        return _diskStems[key] = stem.ToString();
    }

    private string ArchiveStamp(ISC2SourceContainerReader reader)
    {
        if (ReferenceEquals(reader, _archiveStampReader) && _archiveStamp != null) return _archiveStamp;
        var paths = reader is SC2MultiSourceContainerReader multi ? (IReadOnlyList<string>)multi.SourcePaths : new[] { reader.SourcePath };
        var builder = new StringBuilder();
        foreach (var path in paths)
        {
            try
            {
                if (File.Exists(path))
                {
                    var info = new FileInfo(path);
                    builder.Append(path).Append(':').Append(info.Length).Append(':').Append(info.LastWriteTimeUtc.Ticks).Append(';');
                }
                else builder.Append(path).Append(':').Append(Directory.GetLastWriteTimeUtc(path).Ticks).Append(';');
            }
            catch (Exception) { builder.Append(path).Append(";"); }
        }
        _archiveStampReader = reader;
        _archiveStamp = builder.ToString();
        return _archiveStamp;
    }

    private bool TryLoadFromDisk(SC2TextureCatalogEntry entry, string stem, out Texture2D texture)
    {
        texture = null;
        try
        {
            var meta = File.ReadAllText(Path.Combine(DiskCacheDirectory, stem + ".txt")).Split('|');
            if (meta.Length < 4 || !int.TryParse(meta[0], out var width) || !int.TryParse(meta[1], out var height) || !int.TryParse(meta[2], out var mips))
                return false;
            var loaded = new Texture2D(2, 2, TextureFormat.RGBA32, false) { hideFlags = HideFlags.DontSave };
            if (!ImageConversion.LoadImage(loaded, File.ReadAllBytes(Path.Combine(DiskCacheDirectory, stem + ".png")), true))
            {
                UnityEngine.Object.Destroy(loaded);
                return false;
            }
            entry.Width = width;
            entry.Height = height;
            entry.MipCount = mips;
            entry.Encoding = meta[3];
            texture = loaded;
            DiskLoadCount++;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void StoreOnDisk(SC2TextureCatalogEntry entry, string stem, Texture2D filtered)
    {
        if (stem == null || filtered == null) return;
        try
        {
            Directory.CreateDirectory(DiskCacheDirectory);
            File.WriteAllText(Path.Combine(DiskCacheDirectory, stem + ".txt"), entry.Width + "|" + entry.Height + "|" + entry.MipCount + "|" + entry.Encoding);
            File.WriteAllBytes(Path.Combine(DiskCacheDirectory, stem + ".png"), ImageConversion.EncodeToPNG(filtered));
            DiskIndex.Add(stem);
        }
        catch (Exception) { /* the cache is an optimisation only */ }
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
