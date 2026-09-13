using System;
using System.Collections.Generic;
using System.IO;
using SC2.MapEditor.Document;
using SC2.MapEditor.Textures;
using UnityEngine;
using UnityEditor;

public static class SC2TextureBrowser
{
    private const float HeaderHeight = 42f;
    private const float FooterHeight = 42f;
    private const float SidebarWidth = 156f;
    private const float DetailsWidth = 230f;
    private const float CellWidth = 118f;
    private const float CellHeight = 142f;
    private static readonly SC2TextureThumbnailCache Cache = new(96);
    /// <summary>DrawPreview's Texture2D branch (material-slot picker boxes,
    /// e.g. "left panel" Diffuse1/RNM Mask 0-2) has no SC2TextureCatalogEntry
    /// to key Cache off — it draws mat.GetTexture(...) directly. Same
    /// aliasing risk as the catalog-entry thumbnail cache (see
    /// SC2TextureThumbnailCache.FilterAndDestroySource's doc comment), fixed
    /// the same way: cache a small GPU-filtered preview per live texture
    /// instance instead of drawing the full-resolution texture into a small
    /// rect every repaint. Capped and evicted the same simple way — this
    /// only ever holds as many entries as there are picker boxes visible in
    /// one session, never hundreds.</summary>
    private static readonly Dictionary<Texture2D, Texture2D> _livePreviewCache = new();
    private const int LivePreviewCacheCapacity = 64;
    // 2026-08-26, reported directly with a screenshot: info.bdf's Cubemap/
    // Texture3D fields (EnvironmentMap/SkyMap/ColorCorrectionCube/
    // FogOfWarCorrectionCube) resolved correctly once
    // SC2TerrainMaterialFactory.TryLoadCubeOrVolumeFromGameArchive was added,
    // but the picker's own preview box stayed blank — the texture browser's
    // GRID could show them fine (it draws catalog-entry thumbnails), but
    // DrawPreview below only knows how to find a thumbnail via a
    // SC2TextureCatalogEntry, and a texture resolved this new way has none —
    // that's exactly the case the archive bypass exists for (the catalog
    // scan never covered it). Keyed by the live Cubemap/Texture3D instance
    // itself.
    //
    // BUILT LAZILY (GetOrBuildArchivePreview below), NOT eagerly at resolve
    // time — a real, confirmed regression from an earlier eager-registration
    // version, diagnosed via Player.log: SC2TerrainMaterialFactory.Create
    // also resolves EnvironmentMap once per terrain material via this exact
    // same function (to bind the shader's environment-mapping property),
    // completely independent of any picker UI, creating a fresh short-lived
    // Cubemap instance every single rebuild — thousands per session. Eagerly
    // caching a preview for every one of those thrashed this cache's small
    // capacity constantly, evicting the ONE instance a picker box actually
    // needed before it was ever drawn. Building on demand here means only
    // instances actually passed to DrawPreview (i.e. really shown in a
    // picker box) ever occupy a cache slot — back to the same small, stable
    // set _livePreviewCache above already safely assumes.
    private static readonly Dictionary<Texture, Texture2D> _archiveCubeVolumePreviewCache = new();
    private const int ArchiveCubeVolumePreviewCacheCapacity = 64;

    private static Texture2D GetOrBuildArchivePreview(Texture texture)
    {
        if (_archiveCubeVolumePreviewCache.TryGetValue(texture, out var cached) && cached != null) return cached;
        var path = SC2TerrainMaterialFactory.TryGetResourcePath(texture);
        if (string.IsNullOrEmpty(path) || !path.StartsWith("archive:", StringComparison.Ordinal)) return null;
        if (!SC2GameArchiveRegistry.TryGetReader(out var reader)) return null;
        var entryPath = path.Substring("archive:".Length);
        if (!SC2CustomTextureImporter.TryLoadPreviewFromArchive(reader, entryPath, out var preview, out _) || preview == null)
            return null;
        if (_archiveCubeVolumePreviewCache.Count >= ArchiveCubeVolumePreviewCacheCapacity)
        {
            foreach (var tex in _archiveCubeVolumePreviewCache.Values)
                if (tex != null) UnityEngine.Object.Destroy(tex);
            _archiveCubeVolumePreviewCache.Clear();
        }
        _archiveCubeVolumePreviewCache[texture] = preview;
        return preview;
    }
    private static readonly SC2TextureQuery Query = new();
    private static readonly HashSet<string> Favorites = new(StringComparer.Ordinal);
    private static readonly List<string> Recent = new();
    private static IReadOnlyList<int> _results = Array.Empty<int>();
    private static string _queryKey = "";
    private static int _catalogVersion = -1;
    private static bool _open;
    private static string _pickerKey = "";
    private static string _selectedId = "";
    private static string _currentId = "";
    // Set only when Open() found a sibling-based suggestion (see its own
    // doc comment) — RefreshResults reorders the query results so this one
    // entry sorts first, WITHOUT narrowing the search itself, so the rest
    // of the (correctly Usage-filtered) set stays fully browsable below it
    // rather than being hidden by a name-search filter.
    private static string _suggestedId = "";
    private static Vector2 _gridScroll;
    // Widened from Texture2D: a cubemap/volume catalog entry's real picked
    // asset is a Cubemap/Texture3D (see Accept()) — all IS-A Texture.
    private static Texture _pendingTexture;
    private static SC2TextureCatalogEntry _pendingEntry;
    private static string _pendingKey;
    private static bool _pending;
    private static string _message = "";
    private static double _lastClick;
    private static int _channel;
    private static Material _channelMaterial;
    private static bool _fullPreviewRequested;
    private static double _queryReadyAt;
    private static GUISkin _styleSkin;
    private static GUIStyle _boldStyle;
    private static GUIStyle _miniBoldStyle;
    private static GUIStyle _centerMiniStyle;
    private static GUIStyle _centerDimStyle;
    private static GUIStyle _wrapBoldStyle;
    private static GUIStyle _wrapDimStyle;
    private static GUIStyle _flatButtonStyle;
    private static GUIStyle _activeButtonStyle;

    public static bool IsOpen => _open;

    /// <summary>`siblingHint`: another texture slot on the SAME material
    /// (e.g. pass the Diffuse texture when opening the Normal or Mask
    /// picker) — when this slot has nothing picked yet, its counterpart is
    /// suggested by real vanilla suffix convention (SC2TextureCatalog.
    /// TryFindSiblingMatch) instead of leaving the user to search 1000+
    /// textures for the matching set by hand. A silent no-op whenever no
    /// sibling is passed, the sibling itself isn't a cataloged texture, or
    /// no matching counterpart exists.</summary>
    public static void Open(string pickerKey, Texture current, SC2TextureUsage expectedUsage, Texture siblingHint = null)
    {
        LoadPreferences();
        _open = true;
        _pickerKey = pickerKey ?? "Texture";
        Query.Usage = expectedUsage == SC2TextureUsage.Unknown ? null : expectedUsage;
        Query.Search = "";
        Query.Source = null;
        Query.Category = "";
        Query.FavoritesOnly = false;
        _gridScroll = Vector2.zero;
        _message = "";
        _suggestedId = "";
        _currentId = ResolveCurrentId(current);
        Cache.PinFull(_currentId);
        _selectedId = _currentId;

        if (string.IsNullOrEmpty(_currentId) && siblingHint != null && expectedUsage != SC2TextureUsage.Unknown)
        {
            var siblingEntry = ResolveEntry(siblingHint);
            var suggested = siblingEntry != null ? SC2TextureCatalog.Instance.TryFindSiblingMatch(siblingEntry, expectedUsage) : null;
            if (suggested != null)
            {
                _selectedId = suggested.Id;
                _suggestedId = suggested.Id;
                Cache.PinFull(_selectedId);
                _message = "Suggested match for " + siblingEntry.Name + ": " + suggested.Name;
            }
        }
        _fullPreviewRequested = false;
        InvalidateQuery();
    }

    public static Texture Consume(Texture current, string pickerKey, out SC2TextureCatalogEntry entry)
    {
        entry = null;
        if (!_pending || !string.Equals(_pendingKey, pickerKey, StringComparison.Ordinal)) return current;
        _pending = false;
        _pendingKey = null;
        entry = _pendingEntry;
        _pendingEntry = null;
        var result = _pendingTexture;
        _pendingTexture = null;
        GUI.changed = true;
        return result;
    }

    public static void DrawOverlay()
    {
        if (!_open) return;
        RefreshResults();
        var evt = Event.current;
        var screen = new Rect(0, 0, Screen.width, Screen.height);
        DrawRect(screen, new Color(0f, 0f, 0f, 0.72f));

        var width = Mathf.Clamp(Screen.width - 48f, 720f, 1180f);
        var height = Mathf.Clamp(Screen.height - 48f, 520f, 820f);
        var window = new Rect((Screen.width - width) * 0.5f, (Screen.height - height) * 0.5f, width, height);
        DrawRect(window, new Color(0.075f, 0.082f, 0.098f, 1f));
        DrawBorder(window, new Color(0.25f, 0.28f, 0.34f, 1f));

        DrawHeader(new Rect(window.x, window.y, window.width, HeaderHeight));
        var body = new Rect(window.x, window.y + HeaderHeight, window.width, window.height - HeaderHeight - FooterHeight);
        var sidebar = new Rect(body.x, body.y, SidebarWidth, body.height);
        var details = new Rect(body.xMax - DetailsWidth, body.y, DetailsWidth, body.height);
        var grid = new Rect(sidebar.xMax, body.y, body.width - SidebarWidth - DetailsWidth, body.height);
        DrawSidebar(sidebar);
        DrawGrid(grid);
        DrawDetails(details);
        DrawFooter(new Rect(window.x, window.yMax - FooterHeight, window.width, FooterHeight));

        HandleKeyboard(evt);
        if (evt != null && evt.type != EventType.Repaint && evt.type != EventType.Layout) evt.Use();
    }

    private static void DrawHeader(Rect rect)
    {
        DrawRect(rect, new Color(0.105f, 0.115f, 0.135f, 1f));
        GUI.Label(new Rect(rect.x + 14f, rect.y + 10f, 132f, 22f), "Texture Browser", Bold());
        var search = new Rect(rect.x + 150f, rect.y + 8f, rect.width - 310f, 26f);
        GUI.SetNextControlName("SC2TextureBrowserSearch");
        var next = GUI.TextField(search, Query.Search ?? "");
        if (next != Query.Search) { Query.Search = next; InvalidateQuery(true); }
        if (GUI.Button(new Rect(search.xMax + 6f, search.y, 56f, 26f), "Clear")) { Query.Search = ""; InvalidateQuery(); }
        if (GUI.Button(new Rect(rect.xMax - 82f, search.y, 68f, 26f), "Close")) _open = false;
    }

    private static void DrawSidebar(Rect rect)
    {
        DrawRect(rect, new Color(0.085f, 0.092f, 0.108f, 1f));
        DrawBorder(rect, new Color(0.16f, 0.18f, 0.22f, 1f));
        var y = rect.y + 10f;
        GUI.Label(new Rect(rect.x + 10f, y, rect.width - 20f, 20f), "SOURCE", MiniBold()); y += 24f;
        DrawFilterButton(ref y, rect, "All", !Query.Source.HasValue, () => Query.Source = null);
        foreach (SC2TextureSource source in Enum.GetValues(typeof(SC2TextureSource)))
            DrawFilterButton(ref y, rect, source.ToString(), Query.Source == source, () => Query.Source = source);
        y += 8f;
        GUI.Label(new Rect(rect.x + 10f, y, rect.width - 20f, 20f), "USAGE", MiniBold()); y += 24f;
        DrawFilterButton(ref y, rect, "Any", !Query.Usage.HasValue, () => Query.Usage = null);
        foreach (SC2TextureUsage usage in Enum.GetValues(typeof(SC2TextureUsage)))
            if (usage != SC2TextureUsage.Unknown)
                DrawFilterButton(ref y, rect, usage.ToString(), Query.Usage == usage, () => Query.Usage = usage);
        y += 8f;
        DrawFilterButton(ref y, rect, "Favorites", Query.FavoritesOnly, () => Query.FavoritesOnly = !Query.FavoritesOnly);
    }

    private static void DrawFilterButton(ref float y, Rect parent, string label, bool active, Action action)
    {
        var rect = new Rect(parent.x + 8f, y, parent.width - 16f, 24f);
        if (active) DrawRect(rect, new Color(0.22f, 0.40f, 0.68f, 1f));
        if (GUI.Button(rect, label, active ? ActiveButton() : FlatButton())) { action(); InvalidateQuery(); }
        y += 26f;
    }

    private static void DrawGrid(Rect rect)
    {
        DrawRect(rect, new Color(0.065f, 0.07f, 0.083f, 1f));
        var range = SC2TextureVirtualGrid.Calculate(_results.Count, rect.width - 14f, _gridScroll.y, rect.height, CellWidth, CellHeight, 1);
        var content = new Rect(0f, 0f, Mathf.Max(rect.width - 16f, range.Columns * CellWidth), Mathf.Max(rect.height, range.TotalHeight));
        _gridScroll = GUI.BeginScrollView(rect, _gridScroll, content, false, true);
        var entries = SC2TextureCatalog.Instance.Entries;
        for (var visible = range.FirstIndex; visible < range.LastIndexExclusive; visible++)
        {
            var catalogIndex = _results[visible];
            if (catalogIndex < 0 || catalogIndex >= entries.Count) continue;
            var entry = entries[catalogIndex];
            var column = visible % range.Columns;
            var row = visible / range.Columns;
            DrawCard(new Rect(column * CellWidth + 6f, row * CellHeight + 6f, CellWidth - 10f, CellHeight - 10f), entry);
        }
        GUI.EndScrollView();
    }

    private static void DrawCard(Rect rect, SC2TextureCatalogEntry entry)
    {
        var selected = entry.Id == _selectedId;
        DrawRect(rect, selected ? new Color(0.18f, 0.31f, 0.52f, 1f) : new Color(0.11f, 0.12f, 0.145f, 1f));
        DrawBorder(rect, selected ? new Color(0.36f, 0.65f, 1f, 1f) : new Color(0.20f, 0.22f, 0.27f, 1f));
        var preview = new Rect(rect.x + 7f, rect.y + 7f, rect.width - 14f, rect.width - 14f);
        DrawChecker(preview);
        var texture = Cache.Get(entry);
        if (texture != null) DrawEntryTexture(preview, entry, texture);
        else GUI.Label(preview, "not loaded", CenterMini());
        GUI.Label(new Rect(rect.x + 5f, preview.yMax + 4f, rect.width - 10f, 18f), entry.Name, CenterMini());
        GUI.Label(new Rect(rect.x + 5f, preview.yMax + 20f, rect.width - 10f, 16f), entry.Category, CenterDim());
        var favoriteRect = new Rect(rect.xMax - 22f, rect.y + 5f, 18f, 18f);
        if (GUI.Button(favoriteRect, Favorites.Contains(entry.Id) ? "*" : "+", FlatButton())) ToggleFavorite(entry.Id);
        var evt = Event.current;
        if (evt != null && evt.type == EventType.MouseDown && evt.button == 0 && rect.Contains(evt.mousePosition))
        {
            var now = Time.realtimeSinceStartupAsDouble;
            var doubleClick = _selectedId == entry.Id && now - _lastClick < 0.35;
            if (_selectedId != entry.Id) _fullPreviewRequested = false;
            _selectedId = entry.Id;
            _lastClick = now;
            if (doubleClick) Accept(entry);
            evt.Use();
        }
    }

    private static void DrawDetails(Rect rect)
    {
        DrawRect(rect, new Color(0.085f, 0.092f, 0.108f, 1f));
        DrawBorder(rect, new Color(0.16f, 0.18f, 0.22f, 1f));
        var entry = SC2TextureCatalog.Instance.Find(_selectedId);
        var y = rect.y + 12f;
        GUI.Label(new Rect(rect.x + 12f, y, rect.width - 24f, 22f), "DETAILS", MiniBold()); y += 28f;
        var preview = new Rect(rect.x + 14f, y, rect.width - 28f, rect.width - 28f);
        DrawChecker(preview);
        var texture = entry != null ? (_fullPreviewRequested ? Cache.GetFull(entry) : Cache.Get(entry)) : null;
        if (texture != null)
        {
            if (_fullPreviewRequested) DrawChannelTexture(preview, texture);
            else DrawEntryTexture(preview, entry, texture);
        }
        y = preview.yMax + 10f;
        var channelLabels = new[] { "RGBA", "RGB", "R", "G", "B", "A" };
        var channelWidth = (rect.width - 28f) / channelLabels.Length;
        for (var i = 0; i < channelLabels.Length; i++)
        {
            var channelRect = new Rect(rect.x + 14f + i * channelWidth, y, channelWidth - 2f, 22f);
            if (_channel == i) DrawRect(channelRect, new Color(0.22f, 0.40f, 0.68f, 1f));
            if (GUI.Button(channelRect, channelLabels[i], _channel == i ? ActiveButton() : FlatButton()))
            {
                _channel = i;
                _fullPreviewRequested = true;
            }
        }
        y += 30f;
        if (entry != null)
        {
            if (!_fullPreviewRequested && GUI.Button(new Rect(rect.x + 14f, y, rect.width - 28f, 24f), "Load full preview", FlatButton()))
                _fullPreviewRequested = true;
            y += 30f;
            GUI.Label(new Rect(rect.x + 12f, y, rect.width - 24f, 38f), entry.Name, WrapBold()); y += 42f;
            DetailLine(rect, ref y, "Usage", entry.Usage.ToString());
            DetailLine(rect, ref y, "Source", entry.Source.ToString());
            // Prefer entry's own recorded metadata over the DRAWN texture's
            // properties whenever it's available (atlas-baked entries always
            // have it; archive/custom entries get it populated the first
            // time SC2TextureThumbnailCache actually decodes them, see
            // FilterAndDestroySource) — `texture` here is frequently now a
            // small GPU-filtered preview substitute (BuildFilteredPreview),
            // not the real file, so reading Size/Format/Mips off it directly
            // would report the preview's own shape (e.g. "160x160 RGBA32")
            // instead of the real DDS file's.
            var hasRealMetadata = !string.IsNullOrEmpty(entry.ThumbnailResourcePath) || entry.Width > 0;
            DetailLine(rect, ref y, "Size", hasRealMetadata || texture == null ? entry.Width + " x " + entry.Height : texture.width + " x " + texture.height);
            DetailLine(rect, ref y, "Format", hasRealMetadata || texture == null ? entry.Encoding : texture.format.ToString());
            DetailLine(rect, ref y, "Mips", hasRealMetadata || texture == null ? entry.MipCount.ToString() : texture.mipmapCount.ToString());
            GUI.Label(new Rect(rect.x + 12f, y, rect.width - 24f, 74f), string.IsNullOrEmpty(entry.ResourcePath) ? entry.GamePath : entry.ResourcePath, WrapDim());
        }
    }

    private static void DrawFooter(Rect rect)
    {
        DrawRect(rect, new Color(0.105f, 0.115f, 0.135f, 1f));
        GUI.Label(new Rect(rect.x + 12f, rect.y + 12f, 170f, 20f), _results.Count + " textures", CenterDim());
        if (GUI.Button(new Rect(rect.x + 180f, rect.y + 7f, 112f, 28f), "Import...")) ImportCustom();
        if (!string.IsNullOrEmpty(_message)) GUI.Label(new Rect(rect.x + 300f, rect.y + 10f, rect.width - 560f, 22f), _message, CenterDim());
        if (GUI.Button(new Rect(rect.xMax - 238f, rect.y + 7f, 72f, 28f), "None")) Accept(null);
        GUI.enabled = SC2TextureCatalog.Instance.Find(_selectedId) != null;
        if (GUI.Button(new Rect(rect.xMax - 158f, rect.y + 7f, 144f, 28f), "Use Selected")) Accept(SC2TextureCatalog.Instance.Find(_selectedId));
        GUI.enabled = true;
    }

    private static void ImportCustom()
    {
#if UNITY_EDITOR
        var path = EditorUtility.OpenFilePanel("Import custom texture", "", "png,jpg,jpeg,dds");
#else
        var path = SC2RuntimeFileDialog.OpenFile("Import custom texture", "Texture (*.png;*.jpg;*.jpeg;*.dds)", "png;*.jpg;*.jpeg;*.dds");
#endif
        if (string.IsNullOrEmpty(path)) return;
        // overwrite: true — a user deliberately clicking "Import..." and
        // re-picking a file they already imported before means "(re)import
        // this," not "silently hand me back whatever's cached." Without
        // this, re-importing the exact same source (same content hash, same
        // cache path) always hit the "already cached" shortcut in Import
        // below and reloaded the untouched old file — the only way this
        // project can regenerate a texture's cached .dds is right here, so
        // a fix to the encoder (e.g. SC2CustomTextureImporter.
        // EncodeMaterialDds's row-flip fix) could never actually reach an
        // already-imported texture no matter how many times the user
        // re-imported it.
        var result = SC2CustomTextureImporter.Import(path, Query.Usage ?? SC2TextureUsage.Diffuse, true);
        if (!result.Success) { _message = result.Error; return; }
        _selectedId = result.Entry.Id;
        _message = "Imported " + result.Entry.Name + " as " + result.Entry.Encoding;
        InvalidateQuery();
    }

    private static void Accept(SC2TextureCatalogEntry entry)
    {
        Texture texture = entry != null ? ResolveRealAsset(entry) : null;
        if (entry != null && texture == null) { _message = "Texture could not be loaded."; return; }
        if (entry != null)
        {
            Cache.PinFull(entry.Id);
            var resolvedLabel = !string.IsNullOrEmpty(entry.ResourcePath) ? entry.ResourcePath
                : !string.IsNullOrEmpty(entry.GamePath) ? "file:" + entry.GamePath
                : "archive:" + entry.ArchiveEntryPath;
            SC2TerrainMaterialFactory.RegisterResourcePath(texture, resolvedLabel);
            AddRecent(entry.Id);
        }
        _pendingTexture = texture;
        _pendingEntry = entry;
        _pendingKey = _pickerKey;
        _pending = true;
        _open = false;
    }

    /// <summary>The real, independently-owned asset for a picked entry —
    /// deliberately NEVER routes through Cache.Get/GetFull for the actual
    /// pick (only the grid/details panel's own live PREVIEW drawing may use
    /// those). See this method's own 2026-08-29 fix note below for why.
    ///
    /// Resolves the same way for every entry kind (flat 2D, cubemap, volume)
    /// — Resources.Load for vanilla/map-catalogued entries (SC2TextureCatalog
    /// Builder catalogues cubemap/volume the same way as flat textures), the
    /// on-disk file for Custom entries (SC2CustomTextureImporter.TryLoadAny),
    /// the game archive for archive-sourced entries, and — only as a last
    /// resort, for an entry with no independently-resolvable real source at
    /// all (an atlas-baked-thumbnail-only entry) — a direct, uncached
    /// Resources.Load of its thumbnail asset (still safe to hold: this is a
    /// fresh call to Unity's own Resources system, never registered in
    /// Cache's eviction dictionary, unlike Cache.Get/GetFull's return value).
    ///
    /// 2026-08-29 fix, reported directly ("assigning one material's Diffuse
    /// resets a DIFFERENT material's Diffuse — only when picking with Usage
    /// filter 'Any', not a specific usage"): the previous version shortcut
    /// flat (non-cubemap/non-volume) entries straight to Cache.GetFull(entry)
    /// — the SAME Texture2D instance SC2TextureThumbnailCache manages
    /// internally for grid/details PREVIEW drawing, capped at 96 entries
    /// (SC2TextureThumbnailCache's own capacity) and evicted+Destroy()'d by
    /// Trim() whenever that cap is exceeded. A material's Diffuse/Normal/
    /// Mask field got that SAME cache-owned object assigned directly — a
    /// completely different lifetime contract than the cache's own "cheap to
    /// reload, safe to evict" preview usage. Browsing with Usage='Any'
    /// touches a much larger, more varied set of entries per session (every
    /// usage category at once) than a single filtered usage does, making it
    /// far more likely to actually exceed the 96-entry cap within one
    /// session and trigger a real eviction — at which point Trim() destroyed
    /// the exact Texture2D object a PREVIOUSLY-assigned, unrelated
    /// material's Diffuse field was still holding a live reference to,
    /// instantly turning it into a Unity "destroyed object" (reads as null
    /// from then on) — read by the user as "assigning material B's texture
    /// reset material A's". Resolving independently here (never touching
    /// Cache for the actual pick) means the object handed to a material is
    /// never subject to the preview cache's own eviction lifecycle at
    /// all.</summary>
    private static Texture ResolveRealAsset(SC2TextureCatalogEntry entry)
    {
        if (!string.IsNullOrEmpty(entry.ResourcePath))
        {
            var loaded = Resources.Load<Texture>(entry.ResourcePath);
            if (loaded != null) return loaded;
        }
        if (!string.IsNullOrEmpty(entry.GamePath) && SC2CustomTextureImporter.TryLoadAny(entry.GamePath, out var any, out _))
            return any;
        if (!string.IsNullOrEmpty(entry.ArchiveEntryPath) && SC2GameArchiveRegistry.TryGetReader(out var reader)
            && SC2CustomTextureImporter.TryLoadFromArchive(reader, entry.ArchiveEntryPath, out var archiveTex, out _))
            return archiveTex;
        if (!string.IsNullOrEmpty(entry.ThumbnailResourcePath))
        {
            var thumb = Resources.Load<Texture>(entry.ThumbnailResourcePath);
            if (thumb != null) return thumb;
        }
        return null;
    }

    /// <summary>Draws a texture's preview into `rect`, resolving a cubemap/
    /// volume value's catalog entry (via reverse resource-path lookup) and
    /// drawing its cached thumbnail correctly instead of trying to draw the
    /// real non-2D object directly. Routes through DrawEntryTexture — the
    /// exact same helper the grid/details panel already use — so a cached
    /// thumbnail that happens to be a shared atlas page (the common case:
    /// most vanilla entries pack into one big page texture, see
    /// SC2TextureCatalogBuilder's atlas logic) gets UV-cropped to this
    /// entry's own cell instead of showing the whole page. A prior version
    /// returned the raw Texture2D for the caller to draw directly, which
    /// silently skipped this cropping and showed the entire atlas page —
    /// confirmed via a real screenshot (a cubemap's big picker preview
    /// showing dozens of unrelated thumbnails tiled together). Used by
    /// SC2TerrainShell.DrawTexturePicker's preview box.</summary>
    public static void DrawPreview(Rect rect, Texture texture)
    {
        if (texture is Texture2D flat)
        {
            // See FlippedSourceRect's doc comment (DrawEntryTexture below) -
            // this is the "left panel" material texture slot preview
            // (SC2TerrainShell.DrawTexturePicker -> here), which for a map
            // opened via the archive-first pipeline draws the SAME
            // decoder-produced Texture2D actually sampled by the terrain
            // shader - correctly oriented for 3D, needs a display-only flip
            // to read right-side-up as a flat 2D preview. Also routed
            // through a small filtered-preview cache (_livePreviewCache) -
            // see its own doc comment - to avoid the same high-frequency-
            // content aliasing SC2TextureThumbnailCache.FilterAndDestroySource
            // fixes for catalog-entry thumbnails.
            GUI.DrawTextureWithTexCoords(rect, GetOrBuildLivePreview(flat), FlippedSourceRect, true);
            return;
        }
        var archivePreview = GetOrBuildArchivePreview(texture);
        if (archivePreview != null)
        {
            GUI.DrawTextureWithTexCoords(rect, archivePreview, FlippedSourceRect, true);
            return;
        }
        var path = SC2TerrainMaterialFactory.TryGetResourcePath(texture);
        if (string.IsNullOrEmpty(path)) return;
        var entry = path.StartsWith("file:", StringComparison.Ordinal)
            ? SC2TextureCatalog.Instance.Find(SC2TextureCatalogEntry.BuildStableId(SC2TextureSource.Custom, path.Substring(5)))
            : SC2TextureCatalog.Instance.FindByResourcePath(path);
        if (entry == null) return;
        var preview = Cache.Get(entry);
        if (preview != null) DrawEntryTexture(rect, entry, preview);
    }

    private static Texture2D GetOrBuildLivePreview(Texture2D source)
    {
        if (source == null) return null;
        if (_livePreviewCache.TryGetValue(source, out var cached) && cached != null) return cached;
        // Simple, rare-to-hit cap: this cache only ever holds as many
        // entries as picker boxes visible in one session (a handful), so a
        // full clear on overflow (rather than real LRU bookkeeping) is fine.
        if (_livePreviewCache.Count >= LivePreviewCacheCapacity)
        {
            foreach (var tex in _livePreviewCache.Values)
                if (tex != null) UnityEngine.Object.Destroy(tex);
            _livePreviewCache.Clear();
        }
        var built = SC2CustomTextureImporter.BuildFilteredPreview(source);
        if (built != null) _livePreviewCache[source] = built;
        return built ?? source;
    }

    private static void RefreshResults()
    {
        var catalog = SC2TextureCatalog.Instance;
        var key = Query.CacheKey;
        if (_catalogVersion == catalog.Version && key == _queryKey) return;
        if (_catalogVersion == catalog.Version && Time.realtimeSinceStartupAsDouble < _queryReadyAt) return;
        _catalogVersion = catalog.Version;
        _queryKey = key;
        _results = ApplySuggestedOrdering(catalog.Search(Query, Favorites), catalog);
        if (!string.IsNullOrEmpty(_selectedId) && catalog.Find(_selectedId) == null) _selectedId = "";
    }

    /// <summary>Moves the suggested entry (see Open's own siblingHint doc
    /// comment) to the front of an already-Usage-filtered result set,
    /// WITHOUT narrowing the query itself — the rest of the set stays
    /// fully browsable below it, unlike an earlier version of this feature
    /// that filtered the search box down to just the one guessed name and
    /// hid everything else. `raw` is the query engine's own cached array;
    /// never mutated in place since the same array instance is shared
    /// across every call with the same cache key.</summary>
    private static IReadOnlyList<int> ApplySuggestedOrdering(IReadOnlyList<int> raw, SC2TextureCatalog catalog)
    {
        if (string.IsNullOrEmpty(_suggestedId) || raw.Count == 0) return raw;
        var entries = catalog.Entries;
        var suggestedIndex = -1;
        for (var i = 0; i < entries.Count; i++)
            if (entries[i].Id == _suggestedId) { suggestedIndex = i; break; }
        if (suggestedIndex < 0) return raw;

        var pos = -1;
        for (var i = 0; i < raw.Count; i++)
            if (raw[i] == suggestedIndex) { pos = i; break; }
        if (pos <= 0) return raw; // already first, or filtered out entirely (e.g. a Source filter excludes it)

        var reordered = new int[raw.Count];
        reordered[0] = suggestedIndex;
        var w = 1;
        for (var i = 0; i < raw.Count; i++)
            if (i != pos) reordered[w++] = raw[i];
        return reordered;
    }

    private static string ResolveCurrentId(Texture current) => ResolveEntry(current)?.Id ?? "";

    // Archive-sourced picks (the live "no baked catalog" scenario — see
    // SC2TextureCatalog.LoadArchiveLibrary) register their resolved
    // texture under "archive:<ArchiveEntryPath>" (Accept's own
    // resolvedLabel), not a plain resource path — this was falling through
    // FindByResourcePath's Vanilla/Map-only Id lookup unmatched (a real,
    // if latent, bug: the fallback below never resolves either, since
    // TryLoadBytesAny's decoded Texture2D is never given a .name at all),
    // silently landing on "no entry resolved" for the single most common
    // real texture source in this project. Confirmed real: opening the
    // Normal/Mask picker right after picking Diffuse via the browser never
    // offered a suggestion at all.
    private const string ArchivePathPrefix = "archive:";

    private static SC2TextureCatalogEntry ResolveEntry(Texture texture)
    {
        if (texture == null) return null;
        var path = SC2TerrainMaterialFactory.TryGetResourcePath(texture);
        if (!string.IsNullOrEmpty(path))
        {
            if (path.StartsWith("file:", StringComparison.Ordinal))
                return SC2TextureCatalog.Instance.Find(SC2TextureCatalogEntry.BuildStableId(SC2TextureSource.Custom, path.Substring(5)));
            if (path.StartsWith(ArchivePathPrefix, StringComparison.Ordinal))
                return SC2TextureCatalog.Instance.Find(SC2TextureCatalogEntry.BuildStableId(SC2TextureSource.Vanilla, path.Substring(ArchivePathPrefix.Length)));
            var entry = SC2TextureCatalog.Instance.FindByResourcePath(path);
            if (entry != null) return entry;
        }
        foreach (var entry in SC2TextureCatalog.Instance.Entries)
            if (entry.Name == texture.name) return entry;
        return null;
    }

    private static void HandleKeyboard(Event evt)
    {
        if (evt == null || evt.type != EventType.KeyDown) return;
        if (evt.keyCode == KeyCode.Escape) { _open = false; return; }
        if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter) Accept(SC2TextureCatalog.Instance.Find(_selectedId));
        if (SC2Hotkeys.Matches("TextureBrowser.FocusSearch", evt) || (evt.keyCode == KeyCode.F && evt.command)) GUI.FocusControl("SC2TextureBrowserSearch");
    }

    private static void ToggleFavorite(string id) { if (!Favorites.Add(id)) Favorites.Remove(id); SavePreferences(); InvalidateQuery(); }
    private static void AddRecent(string id) { Recent.Remove(id); Recent.Insert(0, id); if (Recent.Count > 20) Recent.RemoveRange(20, Recent.Count - 20); SavePreferences(); }
    private static void LoadPreferences() { Favorites.Clear(); foreach (var id in PlayerPrefs.GetString("SC2.TextureFavorites", "").Split(new[] {'\n'}, StringSplitOptions.RemoveEmptyEntries)) Favorites.Add(id); Recent.Clear(); Recent.AddRange(PlayerPrefs.GetString("SC2.TextureRecent", "").Split(new[] {'\n'}, StringSplitOptions.RemoveEmptyEntries)); }
    private static void SavePreferences() { PlayerPrefs.SetString("SC2.TextureFavorites", string.Join("\n", Favorites)); PlayerPrefs.SetString("SC2.TextureRecent", string.Join("\n", Recent)); PlayerPrefs.Save(); }
    private static void InvalidateQuery(bool debounce = false)
    {
        _queryKey = "";
        if (!debounce) _catalogVersion = -1;
        _queryReadyAt = debounce ? Time.realtimeSinceStartupAsDouble + 0.12 : 0.0;
    }

    private static void DetailLine(Rect parent, ref float y, string label, string value) { GUI.Label(new Rect(parent.x + 12f, y, 62f, 18f), label, CenterDim()); GUI.Label(new Rect(parent.x + 76f, y, parent.width - 88f, 18f), value ?? "", CenterMini()); y += 20f; }
    /// <summary>V-flipped source UV rect (V grows downward from 1 instead of
    /// up from 0) — used for every flat, non-atlas preview draw below. DDS
    /// files store row 0 at the top of the image; Unity's Texture2D/
    /// SetPixelData expects row 0 at the bottom (see
    /// SC2CustomTextureImporter.FlipCompressedBlocksVertically's own doc
    /// comment) so the DECODED texture is correctly oriented for 3D mesh
    /// sampling — but GUI.DrawTexture/Graphics.DrawTexture display a plain
    /// Texture2D with row 0 at the BOTTOM of the drawn rect (confirmed
    /// empirically: fixing the decoder's mesh-space orientation made these
    /// 2D preview panels display upside-down, where they previously
    /// happened to look right by coincidence of the old, now-fixed decoder
    /// bug). This flips only the DRAW, not the underlying texture data, so
    /// the same Texture2D still samples correctly on the actual 3D terrain.</summary>
    private static readonly Rect FlippedSourceRect = new Rect(0f, 1f, 1f, -1f);

    private static void DrawEntryTexture(Rect rect, SC2TextureCatalogEntry entry, Texture2D texture)
    {
        if (entry != null && !string.IsNullOrEmpty(entry.ThumbnailResourcePath) && entry.ThumbnailWidth > 0f && entry.ThumbnailHeight > 0f)
            GUI.DrawTextureWithTexCoords(rect, texture, new Rect(entry.ThumbnailX, entry.ThumbnailY, entry.ThumbnailWidth, entry.ThumbnailHeight), true);
        else
            GUI.DrawTextureWithTexCoords(rect, texture, FlippedSourceRect, true);
    }
    private static void DrawChannelTexture(Rect rect, Texture2D texture)
    {
        if (_channel == 0) { GUI.DrawTextureWithTexCoords(rect, texture, FlippedSourceRect, true); return; }
        if (_channelMaterial == null)
        {
            var shader = Resources.Load<Shader>("SC2TextureChannelPreview") ?? Shader.Find("Hidden/SC2/TextureChannelPreview");
            if (shader != null) _channelMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
        }
        if (_channelMaterial == null) { GUI.DrawTextureWithTexCoords(rect, texture, FlippedSourceRect, true); return; }
        _channelMaterial.SetFloat("_Channel", _channel);
        Graphics.DrawTexture(rect, texture, FlippedSourceRect, 0, 0, 0, 0, _channelMaterial);
    }
    private static void DrawChecker(Rect rect) { DrawRect(rect, new Color(0.12f, 0.13f, 0.15f, 1f)); const float s=12f; for(float y=rect.y;y<rect.yMax;y+=s) for(float x=rect.x;x<rect.xMax;x+=s) if((((int)((x-rect.x)/s)+(int)((y-rect.y)/s))&1)==0) DrawRect(new Rect(x,y,Mathf.Min(s,rect.xMax-x),Mathf.Min(s,rect.yMax-y)),new Color(0.17f,0.18f,0.21f,1f)); }
    private static void DrawBorder(Rect r, Color c) { DrawRect(new Rect(r.x,r.y,r.width,1),c); DrawRect(new Rect(r.x,r.yMax-1,r.width,1),c); DrawRect(new Rect(r.x,r.y,1,r.height),c); DrawRect(new Rect(r.xMax-1,r.y,1,r.height),c); }
    private static void DrawRect(Rect rect, Color color) { var old=GUI.color; GUI.color=color; GUI.DrawTexture(rect,Texture2D.whiteTexture); GUI.color=old; }
    private static void EnsureStyles()
    {
        if (_styleSkin == GUI.skin && _boldStyle != null) return;
        _styleSkin = GUI.skin;
        _boldStyle = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, fontSize = 14 };
        _boldStyle.normal.textColor = Color.white;
        _miniBoldStyle = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, fontSize = 10 };
        _miniBoldStyle.normal.textColor = new Color(.62f, .68f, .78f);
        _centerMiniStyle = new GUIStyle(GUI.skin.label) { fontSize = 10, alignment = TextAnchor.MiddleCenter, clipping = TextClipping.Clip };
        _centerMiniStyle.normal.textColor = new Color(.88f, .9f, .94f);
        _centerDimStyle = new GUIStyle(_centerMiniStyle);
        _centerDimStyle.normal.textColor = new Color(.55f, .6f, .68f);
        _wrapBoldStyle = new GUIStyle(_boldStyle) { fontSize = 12, wordWrap = true };
        _wrapDimStyle = new GUIStyle(_centerDimStyle) { alignment = TextAnchor.UpperLeft, wordWrap = true };
        _flatButtonStyle = new GUIStyle(GUI.skin.button) { alignment = TextAnchor.MiddleLeft };
        _activeButtonStyle = new GUIStyle(_flatButtonStyle);
        _activeButtonStyle.normal.textColor = Color.white;
        _activeButtonStyle.hover.textColor = Color.white;
    }
    private static GUIStyle Bold() { EnsureStyles(); return _boldStyle; }
    private static GUIStyle MiniBold() { EnsureStyles(); return _miniBoldStyle; }
    private static GUIStyle CenterMini() { EnsureStyles(); return _centerMiniStyle; }
    private static GUIStyle CenterDim() { EnsureStyles(); return _centerDimStyle; }
    private static GUIStyle WrapBold() { EnsureStyles(); return _wrapBoldStyle; }
    private static GUIStyle WrapDim() { EnsureStyles(); return _wrapDimStyle; }
    private static GUIStyle FlatButton() { EnsureStyles(); return _flatButtonStyle; }
    private static GUIStyle ActiveButton() { EnsureStyles(); return _activeButtonStyle; }
}
