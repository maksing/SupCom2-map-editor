using System;
using System.Collections.Generic;
using System.IO;
using SC2.MapEditor.Document;
using SC2.MapEditor.Textures;
using UnityEngine;

public sealed class SC2TextureCatalog
{
    private const string CatalogResource = "SC2TextureCatalog";
    private static SC2TextureCatalog _instance;
    public static SC2TextureCatalog Instance => _instance ??= new SC2TextureCatalog();

    private readonly List<SC2TextureCatalogEntry> _entries = new();
    private readonly Dictionary<string, SC2TextureCatalogEntry> _byId = new(StringComparer.Ordinal);
    private readonly SC2TextureCatalogQueryEngine _query = new();
    private bool _loaded;
    private int _version;

    public IReadOnlyList<SC2TextureCatalogEntry> Entries { get { EnsureLoaded(); return _entries; } }
    public int Version { get { EnsureLoaded(); return _version; } }

    public IReadOnlyList<int> Search(SC2TextureQuery query, ISet<string> favorites = null)
    {
        EnsureLoaded();
        _query.SetEntries(_entries, _version);
        return _query.Execute(query, favorites);
    }

    public SC2TextureCatalogEntry Find(string id)
    {
        EnsureLoaded();
        return !string.IsNullOrEmpty(id) && _byId.TryGetValue(id, out var entry) ? entry : null;
    }

    public SC2TextureCatalogEntry FindByResourcePath(string resourcePath)
    {
        EnsureLoaded();
        var id = SC2TextureCatalogEntry.BuildStableId(SC2TextureSource.Vanilla, resourcePath);
        if (_byId.TryGetValue(id, out var entry)) return entry;
        id = SC2TextureCatalogEntry.BuildStableId(SC2TextureSource.Map, resourcePath);
        return _byId.TryGetValue(id, out entry) ? entry : null;
    }

    // Real vanilla terrain materials are exported as a same-basename,
    // different-suffix set ("sandstone_001a_d.win.dds"/"_n.win.dds"/
    // "_s.win.dds" — see InferUsage's own doc comment for the full suffix
    // scan this mirrors). Captures the shared prefix + optional ".win" +
    // real extension so a candidate for any OTHER usage can be
    // reconstructed by swapping just the single suffix letter.
    private static readonly System.Text.RegularExpressions.Regex SiblingSuffixPattern =
        new(@"^(.*)_[dnsm](\.win)?\.(dds|tga|png)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    // Decal atlas files use a whole-WORD "_diffuse"/"_normal" suffix instead
    // of the single-letter terrain-material convention above — matches both
    // real vanilla decal atlases ("<map>.decal_atlas_diffuse.win.dds"/
    // "..._normal.win.dds", confirmed against a real game install) and this
    // editor's own generated atlas names ("decals_diffuse.dds"/
    // "decals_normal.dds", see SC2DecalsShell.ApplyAtlasPick).
    private static readonly System.Text.RegularExpressions.Regex DecalAtlasSuffixPattern =
        new(@"^(.*)_(diffuse|normal)(\.win)?\.(dds|tga|png)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Guesses a sibling texture's counterpart for a different
    /// usage on the same material — e.g. given the Diffuse texture already
    /// picked, suggest that same material's Normal or Mask by swapping the
    /// real vanilla suffix convention. Tries both real Mask suffix
    /// conventions ("_s" and "_m", see InferUsage's own doc comment for why
    /// both exist) since different art folders use different ones. Also
    /// tries the separate whole-word decal-atlas suffix convention
    /// (DecalAtlasSuffixPattern) for Diffuse/Normal, so picking a decal
    /// atlas's Diffuse suggests that same map's Normal atlas and vice versa,
    /// the same way an ordinary terrain material's Diffuse/Normal/Mask
    /// already suggest each other. Returns null when `sibling`'s own path
    /// doesn't end in a recognized suffix of EITHER convention, or no
    /// catalog entry matches any constructed candidate — a safe, silent
    /// no-op rather than a wrong guess, since this only ever pre-selects a
    /// suggestion the user still has to accept.</summary>
    public SC2TextureCatalogEntry TryFindSiblingMatch(SC2TextureCatalogEntry sibling, SC2TextureUsage targetUsage)
    {
        if (sibling == null) return null;
        // A Custom-sourced entry (SC2TextureCatalog.LoadCustomLibrary — the
        // common case for a user-imported decal atlas, unlike a real
        // vanilla one) has empty ArchiveEntryPath AND ResourcePath, only
        // GamePath (its absolute file path) — falling through to it here
        // was missing entirely, so picking a custom-imported atlas's
        // Diffuse never suggested anything for Normal at all.
        var siblingPath = !string.IsNullOrEmpty(sibling.ArchiveEntryPath) ? sibling.ArchiveEntryPath
            : !string.IsNullOrEmpty(sibling.ResourcePath) ? sibling.ResourcePath
            : sibling.GamePath;
        if (string.IsNullOrEmpty(siblingPath)) return null;

        EnsureLoaded();

        var match = SiblingSuffixPattern.Match(siblingPath);
        if (match.Success)
        {
            var prefix = match.Groups[1].Value;
            var winSuffix = match.Groups[2].Value;
            var ext = match.Groups[3].Value;
            var candidateSuffixes = targetUsage switch
            {
                SC2TextureUsage.Diffuse => new[] { "_d" },
                SC2TextureUsage.Normal => new[] { "_n" },
                SC2TextureUsage.Mask => new[] { "_s", "_m" },
                _ => Array.Empty<string>(),
            };
            foreach (var suffix in candidateSuffixes)
            {
                var candidatePath = prefix + suffix + winSuffix + "." + ext;
                var id = SC2TextureCatalogEntry.BuildStableId(sibling.Source, candidatePath);
                if (_byId.TryGetValue(id, out var found)) return found;
            }
        }

        if (targetUsage == SC2TextureUsage.Diffuse || targetUsage == SC2TextureUsage.Normal)
        {
            var decalMatch = DecalAtlasSuffixPattern.Match(siblingPath);
            if (decalMatch.Success)
            {
                var prefix = decalMatch.Groups[1].Value;
                var winSuffix = decalMatch.Groups[3].Value;
                var ext = decalMatch.Groups[4].Value;
                var suffix = targetUsage == SC2TextureUsage.Diffuse ? "_diffuse" : "_normal";
                var candidatePath = prefix + suffix + winSuffix + "." + ext;
                var id = SC2TextureCatalogEntry.BuildStableId(sibling.Source, candidatePath);
                if (_byId.TryGetValue(id, out var found)) return found;
            }
        }

        return null;
    }

    public void Reload()
    {
        _loaded = false;
        EnsureLoaded();
    }

    public void AddOrReplace(SC2TextureCatalogEntry entry)
    {
        EnsureLoaded();
        AddEntry(entry, true);
        _version++;
        _query.SetEntries(_entries, _version);
    }

    private void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        _entries.Clear();
        _byId.Clear();

        var catalogAsset = Resources.Load<TextAsset>(CatalogResource);
        if (catalogAsset != null)
        {
            try
            {
                var data = JsonUtility.FromJson<SC2TextureCatalogData>(catalogAsset.text);
                if (data?.Entries != null)
                    foreach (var entry in data.Entries) AddEntry(entry, false);
                _version = Math.Max(2, data?.Version ?? 2);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[SC2TextureCatalog] Catalog JSON is invalid: " + ex.Message);
            }
        }

        if (_entries.Count == 0)
            LoadLegacyManifest();
        LoadCustomLibrary();
        LoadArchiveLibrary();
        _entries.Sort(CompareEntries);
        RebuildIndex();
        _version++;
        _query.SetEntries(_entries, _version);
    }

    private void LoadLegacyManifest()
    {
        var manifest = Resources.Load<TextAsset>("SC2TextureManifest");
        if (manifest == null) return;
        foreach (var raw in manifest.text.Split('\n'))
        {
            var path = SC2TextureCatalogEntry.NormalizePath(raw);
            if (path.Length == 0) continue;
            AddEntry(CreateEntry(path, SC2TextureSource.Vanilla), false);
        }
    }

    private void LoadCustomLibrary()
    {
        var directory = SC2CustomTextureImporter.LibraryDirectory;
        if (!Directory.Exists(directory)) return;
        foreach (var path in Directory.GetFiles(directory, "*.dds", SearchOption.TopDirectoryOnly))
        {
            var entry = CreateEntry(path, SC2TextureSource.Custom);
            entry.ResourcePath = "";
            entry.GamePath = path;
            entry.Id = SC2TextureCatalogEntry.BuildStableId(SC2TextureSource.Custom, path);
            entry.Encoding = SC2CustomTextureImporter.ReadEncoding(path);
            var thumbnailPath = path + ".thumb.png";
            if (File.Exists(thumbnailPath)) entry.ThumbnailFilePath = thumbnailPath;
            if (SC2CustomTextureImporter.TryReadMetadata(path, out var metadata))
            {
                entry.SourceHash = metadata.SourceHash;
                entry.ContentHash = metadata.ContentHash;
                entry.Usage = metadata.Usage;
                entry.Width = metadata.Width;
                entry.Height = metadata.Height;
                entry.MipCount = metadata.MipCount;
                entry.HasAlpha = metadata.HasAlpha;
                entry.Encoding = metadata.Encoding;
            }
            AddEntry(entry, false);
        }
    }

    /// <summary>Dynamically lists every texture-shaped entry found across
    /// the configured game install's archives (SC2GameArchiveRegistry) —
    /// same "scan live every session, never bake into Assets/Resources"
    /// shape as LoadCustomLibrary above, deliberately NOT routed through
    /// the Editor-only SC2TextureCatalogBuilder bake step: that step writes
    /// its output (JSON + thumbnail atlas PNGs) into Assets/SC2/Resources,
    /// which is exactly the "copyrighted game bytes land inside the
    /// project" outcome the whole archive-first pipeline exists to avoid.
    /// Deliberately cheap — no pixel decode here, just path/name/usage
    /// metadata (a real install's textures.scd alone can hold thousands of
    /// entries) — the real texture only gets decoded lazily, on demand, by
    /// SC2TextureThumbnailCache/SC2CustomTextureImporter.TryLoadFromArchive
    /// when an entry is actually viewed or picked, same as every other
    /// catalog source here. AddEntry(..., replace:false) means a path
    /// already covered by the baked Assets-based vanilla catalog (if any)
    /// keeps that richer, already-decoded entry rather than being
    /// shadowed by this one.</summary>
    // Real terrain Diffuse/Normal/Mask/RNM textures live under textures/
    // Terrain, EnvironmentMap/SkyMap under textures/environment, and
    // ColorCorrectionCube/FogOfWarCorrectionCube under textures/
    // ColorCorrection — the 3 folders this catalog's own consumers
    // (SC2TerrainShell's Diffuse/Normal/Mask pickers AND
    // SC2InfoModelFieldDrawer.DrawTexturesTab's Environment/Sky/
    // ColorCorrection pickers) actually need. reader.Entries otherwise
    // walks the WHOLE combined archive filesystem (maps.scd, units.scd,
    // effects.scd, etc.), so without this filter the picker showed
    // thousands of unrelated unit/effect/UI textures a mapmaker would
    // never want here. Case-insensitive since real vanilla paths mix
    // casing ("textures/Terrain/...", "textures/ColorCorrection/...").
    private static readonly string[] AllowedTextureFolderPrefixes =
    {
        "textures/terrain/",
        "textures/environment/",
        "textures/colorcorrection/",
    };

    private void LoadArchiveLibrary()
    {
        if (!SC2GameArchiveRegistry.TryGetReader(out var reader)) return;
        foreach (var entryInfo in reader.Entries)
        {
            var path = entryInfo.NormalizedPath;
            var inScope = false;
            foreach (var prefix in AllowedTextureFolderPrefixes)
                if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { inScope = true; break; }
            if (!inScope) continue;
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext != ".dds" && ext != ".png" && ext != ".tga") continue;

            var entry = new SC2TextureCatalogEntry
            {
                Source = SC2TextureSource.Vanilla,
                ArchiveEntryPath = path,
                Usage = InferUsage(path),
                Encoding = ext.TrimStart('.').ToUpperInvariant(),
                Id = SC2TextureCatalogEntry.BuildStableId(SC2TextureSource.Vanilla, path),
            };
            AddEntry(entry, false);
        }
    }

    public static SC2TextureCatalogEntry CreateEntry(string path, SC2TextureSource source)
    {
        var normalized = SC2TextureCatalogEntry.NormalizePath(path);
        var entry = new SC2TextureCatalogEntry
        {
            Source = source,
            ResourcePath = source == SC2TextureSource.Custom ? "" : normalized,
            GamePath = source == SC2TextureSource.Custom ? path : "",
            Name = Path.GetFileNameWithoutExtension(SC2TextureCatalogEntry.Leaf(normalized)),
            Usage = InferUsage(normalized),
            Encoding = Path.GetExtension(path).TrimStart('.').ToUpperInvariant()
        };
        if (source == SC2TextureSource.Vanilla)
        {
            var gamePath = normalized;
            const string resourceRoot = "SC2Textures/";
            if (gamePath.StartsWith(resourceRoot, StringComparison.OrdinalIgnoreCase))
                gamePath = gamePath.Substring(resourceRoot.Length);
            entry.GamePath = "/" + gamePath.TrimStart('/') + ".dds";
        }
        entry.Id = SC2TextureCatalogEntry.BuildStableId(source, source == SC2TextureSource.Custom ? path : normalized);
        entry.Normalize();
        return entry;
    }

    public static SC2TextureUsage InferUsage(string path)
    {
        var value = (path ?? "").ToLowerInvariant();
        var leaf = Path.GetFileNameWithoutExtension(value);
        // Real vanilla terrain textures commonly use a DOUBLE extension
        // ("_d.win.dds") — a single GetFileNameWithoutExtension only strips
        // ".dds", leaving ".win" glued onto the suffix and defeating every
        // _d/_n/_s/_m/_h check below for most real files. Confirmed against
        // a full scan of a real gamedata.scd/textures/Terrain: "_d"/"_n"/
        // "_s"/"_m" alone account for 1000+ real filenames, nearly all
        // using this convention (a plain single ".dds" with no ".win" also
        // exists and is unaffected either way).
        if (leaf.EndsWith(".win")) leaf = leaf.Substring(0, leaf.Length - 4);

        // Folder-based rules take priority over any filename substring
        // guess — confirmed real bug: a map literally named "Sky City"
        // lives at textures/Terrain/SC2_D3_SkySity/, and its plain
        // "1_d.win.dds"/"1_s.win.dds" terrain Diffuse/Mask textures were
        // misclassified as Sky purely because "sky" appears in the MAP
        // folder's own name. Real dedicated Sky/Environment/
        // ColorCorrection textures only ever live in their own top-level
        // textures/ folders (never under textures/Terrain/<mapname>/), so
        // a terrain-folder file should never even be a candidate for
        // those 3 usages.
        if (value.Contains("textures/colorcorrection/"))
            return SC2TextureUsage.ColorCorrectionCube;
        if (value.Contains("textures/environment/"))
            return leaf.Contains("sky") ? SC2TextureUsage.Sky : SC2TextureUsage.Environment;

        if (value.Contains("height") || leaf.EndsWith("_h")) return SC2TextureUsage.Heightmap;
        if (value.Contains("rnm") || value.Contains("lighting/mask")) return SC2TextureUsage.Rnm;
        if (value.Contains("normal") || leaf.EndsWith("_n") || leaf.EndsWith("_norm")) return SC2TextureUsage.Normal;
        // "_m" is just as common a real mask-suffix convention as "_s" —
        // confirmed real (143 vs. 189 occurrences across the same scan):
        // both name the same specular/mask slot, different art folders just
        // used different letters.
        if (value.Contains("mask") || value.Contains("spec") || leaf.EndsWith("_s") || leaf.EndsWith("_m")) return SC2TextureUsage.Mask;
        if (value.Contains("diff") || leaf.EndsWith("_d")) return SC2TextureUsage.Diffuse;

        // Fallback for anything outside the 3 dedicated folders above
        // (e.g. a user-imported custom texture with no folder context at
        // all) — same word-based guesses as before this fix, now that the
        // real dedicated folders are handled unambiguously above. "color"
        // stays last so a real ColorCorrection/Sky/Environment word match
        // above always wins over the generic Diffuse guess.
        if (value.Contains("cube") || value.Contains("colorcorrect") || value.Contains("fogcorrect")) return SC2TextureUsage.ColorCorrectionCube;
        if (value.Contains("sky")) return SC2TextureUsage.Sky;
        if (value.Contains("env")) return SC2TextureUsage.Environment;
        if (value.Contains("color")) return SC2TextureUsage.Diffuse;
        return SC2TextureUsage.Unknown;
    }

    private void AddEntry(SC2TextureCatalogEntry entry, bool replace)
    {
        if (entry == null) return;
        entry.Normalize();
        if (string.IsNullOrEmpty(entry.Id))
            entry.Id = SC2TextureCatalogEntry.BuildStableId(entry.Source, string.IsNullOrEmpty(entry.ResourcePath) ? entry.GamePath : entry.ResourcePath);
        if (_byId.TryGetValue(entry.Id, out var old))
        {
            if (!replace) return;
            var index = _entries.IndexOf(old);
            if (index >= 0) _entries[index] = entry;
            _byId[entry.Id] = entry;
            return;
        }
        _entries.Add(entry);
        _byId[entry.Id] = entry;
    }

    private void RebuildIndex()
    {
        _byId.Clear();
        foreach (var entry in _entries) _byId[entry.Id] = entry;
    }

    private static int CompareEntries(SC2TextureCatalogEntry a, SC2TextureCatalogEntry b)
    {
        var source = a.Source.CompareTo(b.Source);
        if (source != 0) return source;
        var category = string.Compare(a.Category, b.Category, StringComparison.OrdinalIgnoreCase);
        return category != 0 ? category : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
    }
}
