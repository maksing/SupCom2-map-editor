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

    private static Texture2D GetOrBuildArchivePreview(Texture texture, bool linear)
    {
        if (_archiveCubeVolumePreviewCache.TryGetValue(texture, out var cached) && cached != null) return cached;
        var path = SC2TerrainMaterialFactory.TryGetResourcePath(texture);
        if (string.IsNullOrEmpty(path) || !path.StartsWith("archive:", StringComparison.Ordinal)) return null;
        if (!SC2GameArchiveRegistry.TryGetReader(out var reader)) return null;
        var entryPath = path.Substring("archive:".Length);
        if (!SC2CustomTextureImporter.TryLoadPreviewFromArchive(reader, entryPath, out var preview, out _, linear) || preview == null)
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
    // Every real sibling match found for siblingHints, all sharing THIS
    // slot's own expectedUsage (see Open's own doc comment — a Normal
    // picker only ever suggests other Normal textures, never Diffuse/Mask)
    // computed unconditionally whenever a siblingHint resolves. More than
    // one entry means either more than one hint resolved a candidate (e.g.
    // Diffuse and Mask came from two different real texture families) or a
    // single family genuinely has more than one file for this usage (e.g.
    // both a "_s" and a "_m" file). DrawCard reorders these to the front of
    // the grid and badges them, rather than a separate suggestions panel,
    // so the user sees the actual thumbnail instead of just a name.
    private static readonly List<(SC2TextureUsage Usage, SC2TextureCatalogEntry Entry)> _siblingSuggestions = new();
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
    // The card's small round favorite button. FlatButton is left-aligned with
    // the skin's own padding (right for the sidebar's text buttons, wrong for
    // a single glyph in an 18px circle, which sat off-center).
    private static GUIStyle _favoriteOffStyle;
    private static GUIStyle _favoriteOnStyle;

    public static bool IsOpen => _open;

    /// <summary>`siblingHints`: the OTHER texture slots on the SAME
    /// material (e.g. pass the already-picked Diffuse and Mask textures
    /// when opening the Normal picker) — every real THIS-SLOT'S-OWN-USAGE
    /// counterpart found for ANY of them (see
    /// SC2TextureCatalog.FindAllSiblingMatches) is collected into
    /// _siblingSuggestions unconditionally, for the browser to show as
    /// highlighted thumbnail cards pulled to the front of the grid, instead
    /// of leaving the user to search 1000+ textures for the matching one by
    /// hand. Deliberately only ever suggests THIS slot's own expectedUsage —
    /// opening the Normal picker never suggests a Diffuse or Mask texture,
    /// since those wouldn't even be valid picks here. Passing more than one
    /// hint matters when they come from DIFFERENT real texture families
    /// (e.g. Diffuse and Normal were picked from two different sets) —
    /// opening Mask then offers BOTH families' own Mask candidate as
    /// separate cards, rather than only ever guessing from whichever single
    /// slot happened to be preferred. Separately, when THIS slot has
    /// nothing picked yet, the first candidate found is also pre-selected
    /// (the original, single-suggestion nudge this used to be the only
    /// mechanism for). A silent no-op for any hint that's null, isn't a
    /// cataloged texture, has no matching counterpart, or when
    /// expectedUsage itself is Unknown (nothing to match against).</summary>
    public static void Open(string pickerKey, Texture current, SC2TextureUsage expectedUsage, params Texture[] siblingHints)
    {
        LoadPreferences();
        _open = true;
        _pickerKey = pickerKey ?? "Texture";
        Query.Usage = expectedUsage == SC2TextureUsage.Unknown ? null : expectedUsage;
        // The search text is kept across reopening the browser too (in
        // memory, and restored once from PlayerPrefs after an app restart)
        // instead of being cleared every time — see SetSearch.
        EnsureSearchLoaded();
        // Source/Community deliberately NOT reset here — the last chosen
        // SOURCE tab is kept across reopening the browser (see SetSourceTab).
        EnsureSourceTabLoaded();
        // Same "derive a UI default from the picker's own key" convention
        // SC2TerrainShell.TextureUsageForPicker already uses for Usage —
        // opening the browser from a decal atlas slot (pickerKey containing
        // "decal") now starts pre-filtered to the Decals category instead
        // of the full, unfiltered catalog. Source/Usage alone can't do this
        // (a decal atlas is Vanilla/Custom-sourced and Diffuse/Normal-usaged
        // just like an ordinary terrain material, indistinguishable by
        // either filter) — Category is the one dimension that actually
        // finds decals specifically, per SC2TextureCatalogEntry.Normalize's
        // own decal-detection rule.
        Query.Category = (pickerKey ?? "").IndexOf("decal", StringComparison.OrdinalIgnoreCase) >= 0 ? "Decals"
            // Same for a Ramp slot (a pair's Ramp texture): start in the Ramp category.
            : (pickerKey ?? "").IndexOf(":ramp", StringComparison.OrdinalIgnoreCase) >= 0 ? SC2TextureCatalogEntry.RampCategory
            : "";
        Query.FavoritesOnly = false;
        _gridScroll = Vector2.zero;
        _message = "";
        _siblingSuggestions.Clear();
        _currentId = ResolveCurrentId(current);
        Cache.PinFull(_currentId);
        _selectedId = _currentId;

        if (siblingHints != null && siblingHints.Length > 0 && expectedUsage != SC2TextureUsage.Unknown)
        {
            // Resolve every hint's catalog entry up front (in the caller's
            // own order — e.g. Diffuse before Mask when opening Normal)
            // and skip anything null/uncataloged, so a family match on one
            // hint doesn't depend on iteration order finding it before a
            // dud entry short-circuits the loop.
            var siblingEntries = new List<SC2TextureCatalogEntry>();
            foreach (var hint in siblingHints)
            {
                if (hint == null) continue;
                var resolved = ResolveEntry(hint);
                if (resolved != null) siblingEntries.Add(resolved);
            }

            foreach (var siblingEntry in siblingEntries)
            {
                foreach (var candidate in SC2TextureCatalog.Instance.FindAllSiblingMatches(siblingEntry, expectedUsage))
                {
                    if (candidate.Id == siblingEntry.Id) continue;
                    var alreadyListed = false;
                    foreach (var existing in _siblingSuggestions)
                        if (existing.Entry.Id == candidate.Id) { alreadyListed = true; break; }
                    if (alreadyListed) continue;
                    _siblingSuggestions.Add((expectedUsage, candidate));
                    Cache.PinFull(candidate.Id);
                }
            }

            if (_siblingSuggestions.Count > 0 && string.IsNullOrEmpty(_currentId))
            {
                var suggested = _siblingSuggestions[0].Entry;
                _selectedId = suggested.Id;
                Cache.PinFull(_selectedId);
                _message = "Suggested match for " + siblingEntries[0].Name + ": " + suggested.Name;
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

        // While the Build's own confirm dialog (Delete imported texture) is
        // open, this browser is still drawn behind it but must not react:
        // disabled controls, no keyboard shortcuts, and — crucially — it must
        // not swallow the mouse events the dialog, drawn later in the same
        // OnGUI, needs. (The Editor's dialog is a blocking OS modal.)
        var previousEnabled = GUI.enabled;
        var confirmOpen = false;
#if !UNITY_EDITOR
        confirmOpen = SC2RuntimeConfirm.IsOpen;
#endif
        if (confirmOpen) GUI.enabled = false;

        DrawHeader(new Rect(window.x, window.y, window.width, HeaderHeight));
        var body = new Rect(window.x, window.y + HeaderHeight, window.width, window.height - HeaderHeight - FooterHeight);
        var sidebar = new Rect(body.x, body.y, SidebarWidth, body.height);
        var details = new Rect(body.xMax - DetailsWidth, body.y, DetailsWidth, body.height);
        var grid = new Rect(sidebar.xMax, body.y, body.width - SidebarWidth - DetailsWidth, body.height);
        DrawSidebar(sidebar);
        DrawGrid(grid);
        DrawDetails(details);
        DrawFooter(new Rect(window.x, window.yMax - FooterHeight, window.width, FooterHeight));

        GUI.enabled = previousEnabled;
        if (confirmOpen) return;
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
        if (next != Query.Search) { SetSearch(next); InvalidateQuery(true); }
        if (GUI.Button(new Rect(search.xMax + 6f, search.y, 56f, 26f), "Clear")) { SetSearch(""); InvalidateQuery(); }
        if (GUI.Button(new Rect(rect.xMax - 82f, search.y, 68f, 26f), "Close")) { _open = false; PlayerPrefs.Save(); }
    }

    private const string SearchPreferenceKey = "SC2.TextureBrowser.Search";
    private static bool _searchLoaded;

    // PlayerPrefs.SetString on every keystroke is cheap (in-memory); the disk
    // write happens on Close / app quit rather than per character.
    private static void SetSearch(string text)
    {
        Query.Search = text ?? "";
        PlayerPrefs.SetString(SearchPreferenceKey, Query.Search);
    }

    private static void EnsureSearchLoaded()
    {
        if (_searchLoaded) return;
        _searchLoaded = true;
        Query.Search = PlayerPrefs.GetString(SearchPreferenceKey, "");
    }

    private static void DrawSidebar(Rect rect)
    {
        DrawRect(rect, new Color(0.085f, 0.092f, 0.108f, 1f));
        DrawBorder(rect, new Color(0.16f, 0.18f, 0.22f, 1f));
        var y = rect.y + 10f;
        GUI.Label(new Rect(rect.x + 10f, y, rect.width - 20f, 20f), "SOURCE", MiniBold()); y += 24f;
        // Four groups, not the raw SC2TextureSource enum (Map/Generated never
        // had a real use here, and Vanilla mixed the base game's own
        // archives with community map packs):
        //   Vanilla   - textures.scd / z_dlc1.scd / z_dlc1_map_shared.scd
        //   Community - any OTHER archive in the game folder
        //   Imported  - textures imported into this editor's TextureLibrary
        //               (Source == Custom)
        DrawFilterButton(ref y, rect, "All", !Query.Source.HasValue, () => SetSourceTab(SourceTabAll));
        DrawFilterButton(ref y, rect, "Vanilla", Query.Source == SC2TextureSource.Vanilla && Query.Community == false,
            () => SetSourceTab(SourceTabVanilla));
        DrawFilterButton(ref y, rect, "Community", Query.Source == SC2TextureSource.Vanilla && Query.Community == true,
            () => SetSourceTab(SourceTabCommunity));
        DrawFilterButton(ref y, rect, "Imported", Query.Source == SC2TextureSource.Custom,
            () => SetSourceTab(SourceTabImported));
        y += 8f;
        GUI.Label(new Rect(rect.x + 10f, y, rect.width - 20f, 20f), "USAGE", MiniBold()); y += 24f;
        DrawFilterButton(ref y, rect, "Any", !Query.Usage.HasValue, () => Query.Usage = null);
        DrawUsageButton(ref y, rect, "Diffuse", SC2TextureUsage.Diffuse);
        DrawUsageButton(ref y, rect, "Normal", SC2TextureUsage.Normal);
        DrawUsageButton(ref y, rect, "Mask", SC2TextureUsage.Mask);
        // The whitelisted textures/Engine/ fills (see
        // SC2TextureCatalogEntry.IsEngineTexturePath) — a Category toggle
        // like Decals, placed under Mask since they are all mask-usage.
        DrawFilterButton(ref y, rect, "Engine", string.Equals(Query.Category, SC2TextureCatalogEntry.EngineCategory, StringComparison.OrdinalIgnoreCase),
            () => Query.Category = string.Equals(Query.Category, SC2TextureCatalogEntry.EngineCategory, StringComparison.OrdinalIgnoreCase) ? "" : SC2TextureCatalogEntry.EngineCategory);
        // The ramp gradient textures (terrain, water, particle) — a Category toggle like Engine, right under it.
        DrawFilterButton(ref y, rect, "Ramp", string.Equals(Query.Category, SC2TextureCatalogEntry.RampCategory, StringComparison.OrdinalIgnoreCase),
            () => Query.Category = string.Equals(Query.Category, SC2TextureCatalogEntry.RampCategory, StringComparison.OrdinalIgnoreCase) ? "" : SC2TextureCatalogEntry.RampCategory);
        // Everything with no button of its own (Rnm, Heightmap, and textures
        // whose usage couldn't be inferred) — see SC2TextureUsageFilter.
        DrawUsageButton(ref y, rect, "Other", SC2TextureUsage.Unknown);
        // Environment (sky included) and ColorCorrectionCube are a different
        // kind of texture than the material Diffuse/Normal/Mask above, so
        // they sit in their own visually separated block, like Decals below.
        y += 8f;
        DrawUsageButton(ref y, rect, "Environment", SC2TextureUsage.Environment);
        DrawUsageButton(ref y, rect, "ColorCorrectionCube", SC2TextureUsage.ColorCorrectionCube);
        y += 8f;
        // A dedicated toggle rather than a full generic per-Category
        // section (there are a dozen+ real distinct categories - Terrain,
        // Splats, Tarmacs, environment, ui, ... - which would overflow this
        // fixed-height sidebar without adding scrolling). Decals earns its
        // own button specifically because SC2TextureCatalogEntry.Normalize
        // now tags it from BOTH a folder match and a filename match (real
        // decal textures turned out to be scattered across dozens of
        // per-map folders, not confined to one), so "Category == Decals"
        // is the only way to actually find them all — Source/Usage alone
        // can't (a decal is Vanilla-sourced and Diffuse/Normal-usaged just
        // like ordinary terrain materials, indistinguishable by either
        // filter). Query.Category itself already existed and is already
        // consulted by SC2TextureCatalogQueryEngine — this was the missing
        // UI to actually set it.
        DrawFilterButton(ref y, rect, "Decals", string.Equals(Query.Category, "Decals", StringComparison.OrdinalIgnoreCase),
            () => Query.Category = string.Equals(Query.Category, "Decals", StringComparison.OrdinalIgnoreCase) ? "" : "Decals");
        y += 8f;
        DrawFilterButton(ref y, rect, "Favorites", Query.FavoritesOnly, () => Query.FavoritesOnly = !Query.FavoritesOnly);
    }

    // Filter buttons compare through SC2TextureUsageFilter.Normalize so a
    // picker that opened the browser asking for Sky/Rnm/Heightmap still lights
    // up the button that actually covers it (Environment / Other).
    private static void DrawUsageButton(ref float y, Rect parent, string label, SC2TextureUsage usage) =>
        DrawFilterButton(ref y, parent, label,
            Query.Usage.HasValue && SC2TextureUsageFilter.Normalize(Query.Usage.Value) == usage,
            () => Query.Usage = usage);

    private const int SourceTabAll = 0, SourceTabVanilla = 1, SourceTabCommunity = 2, SourceTabImported = 3;
    private const string SourceTabPreferenceKey = "SC2.TextureBrowser.SourceTab";
    private static bool _sourceTabLoaded;

    private static void SetSourceTab(int tab)
    {
        switch (tab)
        {
            case SourceTabVanilla: Query.Source = SC2TextureSource.Vanilla; Query.Community = false; break;
            case SourceTabCommunity: Query.Source = SC2TextureSource.Vanilla; Query.Community = true; break;
            case SourceTabImported: Query.Source = SC2TextureSource.Custom; Query.Community = null; break;
            default: Query.Source = null; Query.Community = null; tab = SourceTabAll; break;
        }
        PlayerPrefs.SetInt(SourceTabPreferenceKey, tab);
        PlayerPrefs.Save();
    }

    /// <summary>The SOURCE tab is remembered, not reset to All every time the
    /// browser opens: the in-memory Query already survives reopening within a
    /// session; this restores it once from PlayerPrefs after an app restart.</summary>
    private static void EnsureSourceTabLoaded()
    {
        if (_sourceTabLoaded) return;
        _sourceTabLoaded = true;
        SetSourceTab(PlayerPrefs.GetInt(SourceTabPreferenceKey, SourceTabAll));
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
        // Fit as many CellWidth-wide columns as the view holds, then stretch
        // the cells to share the whole width — rather than leaving whatever
        // is left over after the last column as a wide empty strip on the
        // right. A card's height grows with its width (its thumbnail is
        // square) by keeping the fixed label area below it (CellHeight -
        // CellWidth) constant.
        var layoutWidth = rect.width - 16f; // minus the vertical scrollbar
        var columns = Mathf.Max(1, Mathf.FloorToInt(layoutWidth / CellWidth));
        var cellWidth = layoutWidth / columns;
        var cellHeight = cellWidth + (CellHeight - CellWidth);
        // +0.01f: Calculate re-derives the column count by dividing, and
        // layoutWidth / (layoutWidth / columns) can land a hair under
        // `columns` in floating point.
        var range = SC2TextureVirtualGrid.Calculate(_results.Count, columns * cellWidth + 0.01f, _gridScroll.y, rect.height, cellWidth, cellHeight, 1);
        var content = new Rect(0f, 0f, layoutWidth, Mathf.Max(rect.height, range.TotalHeight));
        _gridScroll = GUI.BeginScrollView(rect, _gridScroll, content, false, true);
        var entries = SC2TextureCatalog.Instance.Entries;
        for (var visible = range.FirstIndex; visible < range.LastIndexExclusive; visible++)
        {
            var catalogIndex = _results[visible];
            if (catalogIndex < 0 || catalogIndex >= entries.Count) continue;
            var entry = entries[catalogIndex];
            var column = visible % range.Columns;
            var row = visible / range.Columns;
            DrawCard(new Rect(column * cellWidth + 6f, row * cellHeight + 6f, cellWidth - 10f, cellHeight - 10f), entry);
        }
        GUI.EndScrollView();
    }

    private static bool TryGetSuggestionUsage(string entryId, out SC2TextureUsage usage)
    {
        foreach (var suggestion in _siblingSuggestions)
            if (suggestion.Entry.Id == entryId) { usage = suggestion.Usage; return true; }
        usage = SC2TextureUsage.Unknown;
        return false;
    }

    private static void DrawCard(Rect rect, SC2TextureCatalogEntry entry)
    {
        var selected = entry.Id == _selectedId;
        var isSuggested = TryGetSuggestionUsage(entry.Id, out var suggestionUsage);
        DrawRect(rect, selected ? new Color(0.18f, 0.31f, 0.52f, 1f) : new Color(0.11f, 0.12f, 0.145f, 1f));
        DrawBorder(rect, selected ? new Color(0.36f, 0.65f, 1f, 1f) : isSuggested ? new Color(0.95f, 0.65f, 0.15f, 1f) : new Color(0.20f, 0.22f, 0.27f, 1f));
        var preview = new Rect(rect.x + 7f, rect.y + 7f, rect.width - 14f, rect.width - 14f);
        DrawChecker(preview);
        var texture = Cache.Get(entry);
        if (texture != null) DrawEntryTexture(preview, entry, texture);
        else GUI.Label(preview, "not loaded", CenterMini());
        GUI.Label(new Rect(rect.x + 5f, preview.yMax + 4f, rect.width - 10f, 18f), entry.Name, CenterMini());
        GUI.Label(new Rect(rect.x + 5f, preview.yMax + 20f, rect.width - 10f, 16f), entry.Category, CenterDim());
        // Inset into the thumbnail's own top-right corner by the same margin
        // on both sides, mirroring the "Suggested" badge at its top-left.
        // It used to hang off the card's edge (xMax-22..xMax-4 against a
        // thumbnail ending at xMax-7), so it poked past the thumbnail and
        // sat unevenly against the card border.
        var favoriteRect = new Rect(preview.xMax - 21f, preview.y + 3f, 18f, 18f);
        var isFavorite = Favorites.Contains(entry.Id);
        if (GUI.Button(favoriteRect, isFavorite ? "*" : "+", FavoriteButton(isFavorite))) ToggleFavorite(entry.Id);
        if (isSuggested)
        {
            // A small tag over the thumbnail's own top-left corner rather
            // than a separate row — the point of moving suggestions into
            // the grid itself (replacing an earlier top text-button-row
            // design) was showing the actual thumbnail image, so the badge
            // has to sit ON the card, not push it down or replace it. Fixed
            // "Suggested" text, not suggestionUsage itself — every card
            // shown here already shares this slot's own expectedUsage (see
            // Open's own doc comment), so printing the usage would just be
            // redundant with the picker's own Usage filter.
            const string badgeText = "Suggested";
            var badgeWidth = Mathf.Clamp(MiniBold().CalcSize(new GUIContent(badgeText)).x + 10f, 26f, rect.width - 14f);
            var badgeRect = new Rect(rect.x + 7f, rect.y + 7f, badgeWidth, 16f);
            DrawRect(badgeRect, new Color(0.95f, 0.65f, 0.15f, 0.92f));
            var badgeStyle = MiniBold();
            var previousColor = badgeStyle.normal.textColor;
            badgeStyle.normal.textColor = Color.black;
            GUI.Label(badgeRect, badgeText, badgeStyle);
            badgeStyle.normal.textColor = previousColor;
        }
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
        var previousEnabled = GUI.enabled;
        if (GUI.Button(new Rect(rect.x + 180f, rect.y + 7f, 112f, 28f), "Import...")) ImportCustom();
        var selected = SC2TextureCatalog.Instance.Find(_selectedId);
        GUI.enabled = previousEnabled && selected != null;
        if (GUI.Button(new Rect(rect.x + 298f, rect.y + 7f, 104f, 28f), "Download...")) DownloadSelected(selected);
        GUI.enabled = previousEnabled;
        var messageX = rect.x + 408f;
        // Only an IMPORTED texture (the editor's own TextureLibrary) can be
        // deleted — vanilla/community archive entries are read-only.
        if (selected != null && selected.Source == SC2TextureSource.Custom)
        {
            if (GUI.Button(new Rect(rect.x + 408f, rect.y + 7f, 84f, 28f), "Delete")) RequestDeleteImported(selected);
            messageX = rect.x + 500f;
        }
        if (!string.IsNullOrEmpty(_message)) GUI.Label(new Rect(messageX, rect.y + 10f, rect.xMax - 260f - messageX, 22f), _message, CenterDim());
        if (GUI.Button(new Rect(rect.xMax - 238f, rect.y + 7f, 72f, 28f), "None")) Accept(null);
        GUI.enabled = previousEnabled && selected != null;
        if (GUI.Button(new Rect(rect.xMax - 158f, rect.y + 7f, 144f, 28f), "Use Selected")) Accept(SC2TextureCatalog.Instance.Find(_selectedId));
        GUI.enabled = previousEnabled;
    }

    private static void RequestDeleteImported(SC2TextureCatalogEntry entry)
    {
        if (entry == null || entry.Source != SC2TextureSource.Custom) return;
        const string title = "Delete Imported Texture";
        var message = $"Delete '{entry.Name}' from the texture library? Maps that use it will lose this texture. This cannot be undone.";
#if UNITY_EDITOR
        if (EditorUtility.DisplayDialog(title, message, "Delete", "Cancel")) ApplyDeleteImported(entry);
#else
        SC2RuntimeConfirm.Open(title, message, "Delete", () => ApplyDeleteImported(entry));
#endif
    }

    private static void ApplyDeleteImported(SC2TextureCatalogEntry entry)
    {
        if (!SC2CustomTextureImporter.TryDelete(entry, out var error))
        {
            _message = "Could not delete: " + error;
            return;
        }
        SC2TextureCatalog.Instance.Remove(entry.Id);
        Cache.Evict(entry);
        var name = entry.Name;
        if (Favorites.Remove(entry.Id) | Recent.Remove(entry.Id)) SavePreferences();
        if (_selectedId == entry.Id) _selectedId = "";
        // A deleted texture must not linger as a sibling suggestion card.
        _siblingSuggestions.RemoveAll(s => SC2TextureCatalog.Instance.Find(s.Entry.Id) == null);
        _message = "Deleted " + name;
        InvalidateQuery();
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

    /// <summary>Saves a copy of the selected texture's real file to disk — the
    /// original bytes whenever there's a real one to copy (GamePath, a loose
    /// file; ArchiveEntryPath, inside a .scd), so a vanilla/community texture
    /// downloads byte-for-byte identical to what ships in the game, not a
    /// re-encoded/re-compressed copy. Only entries with neither (a catalog
    /// baked straight into a Resources asset, with no independently
    /// resolvable original left — see SC2TextureCatalogEntry.ResourcePath's
    /// own doc comment) fall back to re-encoding the resolved texture as
    /// PNG, which also covers a Cubemap/Texture3D entry (IsCubemap/IsVolume)
    /// no differently, since raw-byte copying never touches the decoded
    /// Unity object at all — the PNG fallback simply can't help one whose
    /// only source is baked-only, since EncodeToPNG needs a flat, readable
    /// Texture2D.</summary>
    private static void DownloadSelected(SC2TextureCatalogEntry entry)
    {
        if (entry == null) return;
        byte[] bytes; string extension;
        if (TryGetRawBytes(entry, out bytes, out extension))
        {
            SaveDownload(entry, bytes, extension);
            return;
        }
        var texture = ResolveRealAsset(entry) as Texture2D;
        if (texture == null) { _message = "This texture has no downloadable file."; return; }
        Texture2D readable = null;
        try
        {
            // Full resolution (not the default 160px thumbnail size), and via a GPU blit+readback
            // rather than the source texture's own bytes — sidesteps both the "Read/Write Enabled"
            // import restriction EncodeToPNG would otherwise need and this fallback's own square-
            // size limitation only matters for a non-square source, which none of this game's real
            // terrain textures are.
            var size = Mathf.Max(1, Mathf.Max(texture.width, texture.height));
            readable = SC2CustomTextureImporter.BuildFilteredPreview(texture, size, linear: false);
            bytes = ImageConversion.EncodeToPNG(readable != null ? readable : texture);
        }
        catch (Exception ex)
        {
            _message = "Download failed: " + ex.Message;
            return;
        }
        finally
        {
            // A fresh instance BuildFilteredPreview built just for this download — never the real
            // resolved `texture` itself, which other live code may still be holding a reference to.
            if (readable != null) UnityEngine.Object.Destroy(readable);
        }
        if (bytes == null || bytes.Length == 0) { _message = "This texture has no downloadable file."; return; }
        SaveDownload(entry, bytes, "png");
    }

    /// <summary>The real, original file bytes behind `entry` — a byte-exact copy of whatever ships
    /// on disk or inside a .scd, never a re-encode. False when neither a loose file nor an archive
    /// entry exists for it (see DownloadSelected's own doc comment on the PNG fallback for that
    /// case).</summary>
    private static bool TryGetRawBytes(SC2TextureCatalogEntry entry, out byte[] bytes, out string extension)
    {
        bytes = null;
        extension = "dds";
        if (!string.IsNullOrEmpty(entry.GamePath) && File.Exists(entry.GamePath))
        {
            try
            {
                bytes = File.ReadAllBytes(entry.GamePath);
                var fromPath = Path.GetExtension(entry.GamePath).TrimStart('.');
                if (!string.IsNullOrEmpty(fromPath)) extension = fromPath.ToLowerInvariant();
                return true;
            }
            catch { /* fall through to the archive path below */ }
        }
        if (!string.IsNullOrEmpty(entry.ArchiveEntryPath) && SC2GameArchiveRegistry.TryGetReader(out var reader))
        {
            try
            {
                using var stream = reader.OpenEntry(entry.ArchiveEntryPath);
                if (stream == null) return false;
                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                bytes = memory.ToArray();
                var fromPath = Path.GetExtension(entry.ArchiveEntryPath).TrimStart('.');
                if (!string.IsNullOrEmpty(fromPath)) extension = fromPath.ToLowerInvariant();
                return true;
            }
            catch { return false; }
        }
        return false;
    }

    private static void SaveDownload(SC2TextureCatalogEntry entry, byte[] bytes, string extension)
    {
        var defaultName = SanitizeFileName(entry.Name);
#if UNITY_EDITOR
        var path = EditorUtility.SaveFilePanel("Download Texture", "", defaultName, extension);
#else
        var filterLabel = extension.ToUpperInvariant() + " (*." + extension + ")";
        var path = SC2RuntimeFileDialog.SaveFile("Download Texture", filterLabel, extension);
#endif
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            File.WriteAllBytes(path, bytes);
            _message = "Downloaded " + Path.GetFileName(path);
        }
        catch (Exception ex)
        {
            _message = "Download failed: " + ex.Message;
        }
    }

    private static string SanitizeFileName(string name)
    {
        if (string.IsNullOrEmpty(name)) return "texture";
        var sanitized = name;
        foreach (var invalid in Path.GetInvalidFileNameChars())
            sanitized = sanitized.Replace(invalid, '_');
        return sanitized;
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
        // Normal/RNM textures are raw linear vector/blend data, not sRGB
        // color — the SAME condition SC2TextureThumbnailCache.Load already
        // uses for the browser's own preview thumbnails. Both runtime
        // decode calls below used to omit this entirely (TryLoadAny had no
        // way to even accept it), so the REAL Texture2D handed to a
        // material's Normal/Mask field got its GPU-level sRGB tag baked
        // wrong at the moment it was picked — no later preview-side fix
        // (e.g. the left panel's own filtered-preview blit) can correct a
        // source texture that's already mistagged. Confirmed real: a picked
        // vanilla normal map rendered visibly different (flatter, more
        // saturated blue) than that same file's own — correctly linear —
        // browser thumbnail.
        var linear = entry.Usage == SC2TextureUsage.Normal || entry.Usage == SC2TextureUsage.Rnm;
        if (!string.IsNullOrEmpty(entry.ResourcePath))
        {
            var loaded = Resources.Load<Texture>(entry.ResourcePath);
            if (loaded != null) return loaded;
        }
        if (!string.IsNullOrEmpty(entry.GamePath) && SC2CustomTextureImporter.TryLoadAny(entry.GamePath, out var any, out _, linear))
            return any;
        if (!string.IsNullOrEmpty(entry.ArchiveEntryPath) && SC2GameArchiveRegistry.TryGetReader(out var reader)
            && SC2CustomTextureImporter.TryLoadFromArchive(reader, entry.ArchiveEntryPath, out var archiveTex, out _, linear))
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
    public static void DrawPreview(Rect rect, Texture texture, SC2TextureUsage usage = SC2TextureUsage.Unknown)
    {
        // Normal/RNM textures are raw vector/blend data, not color —
        // decoding and previewing them as sRGB (the correct interpretation
        // for every other usage) gamma-distorts every value. Same condition
        // SC2TextureThumbnailCache.Load already uses for the texture
        // BROWSER's own grid/details thumbnails; this "left panel" preview
        // built its filtered copy unconditionally as sRGB and never applied
        // it, so a Normal/Mask slot's inline preview visibly disagreed with
        // that same texture's browser thumbnail (different color/contrast).
        var linear = usage == SC2TextureUsage.Normal || usage == SC2TextureUsage.Rnm;
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
            GUI.DrawTextureWithTexCoords(rect, GetOrBuildLivePreview(flat, linear), FlippedSourceRect, true);
            return;
        }
        var archivePreview = GetOrBuildArchivePreview(texture, linear);
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

    private static Texture2D GetOrBuildLivePreview(Texture2D source, bool linear)
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
        var built = SC2CustomTextureImporter.BuildFilteredPreview(source, linear: linear);
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

    /// <summary>Moves every sibling suggestion (see Open's own siblingHints
    /// doc comment) to the front of the result set, in the same
    /// Diffuse→Normal→Mask priority order they were collected in, so they
    /// show up as real thumbnail cards right at the start of the grid
    /// instead of a separate text-only panel — the whole point of the
    /// feature is a one-glance visual match, not a name to read. Pulled in
    /// even when the active Usage/Category/Source filter would otherwise
    /// have excluded one (e.g. a Diffuse suggestion while the grid itself
    /// is filtered to Normal, since expectedUsage narrows Query.Usage) —
    /// these are an explicit recommendation, not a normal search result,
    /// so the filter doesn't get to hide them. `raw` is the query engine's
    /// own cached array; never mutated in place since the same array
    /// instance is shared across every call with the same cache key.
    /// DrawCard's own suggestion badge is what tells these apart from an
    /// ordinary result once they're mixed into the same grid.</summary>
    private static IReadOnlyList<int> ApplySuggestedOrdering(IReadOnlyList<int> raw, SC2TextureCatalog catalog)
    {
        if (_siblingSuggestions.Count == 0) return raw;
        var entries = catalog.Entries;

        var suggestedIndices = new List<int>(_siblingSuggestions.Count);
        var seen = new HashSet<int>();
        foreach (var (_, suggestion) in _siblingSuggestions)
        {
            var index = -1;
            for (var i = 0; i < entries.Count; i++)
                if (entries[i].Id == suggestion.Id) { index = i; break; }
            if (index >= 0 && seen.Add(index)) suggestedIndices.Add(index);
        }
        if (suggestedIndices.Count == 0) return raw;

        var reordered = new List<int>(raw.Count + suggestedIndices.Count);
        reordered.AddRange(suggestedIndices);
        foreach (var index in raw)
            if (!seen.Contains(index)) reordered.Add(index);
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

    public static SC2TextureCatalogEntry ResolveEntry(Texture texture)
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
        // "+" is vertically centered by its font metrics already; "*" is a
        // superscript-style glyph that sits high in the em box, so it gets
        // extra top padding to land at the circle's center too.
        _favoriteOffStyle = new GUIStyle(GUI.skin.button)
            { alignment = TextAnchor.MiddleCenter, padding = new RectOffset(0, 0, 0, 1), fontSize = 12 };
        _favoriteOnStyle = new GUIStyle(_favoriteOffStyle) { padding = new RectOffset(0, 0, 5, 0) };
    }
    private static GUIStyle FavoriteButton(bool isFavorite) { EnsureStyles(); return isFavorite ? _favoriteOnStyle : _favoriteOffStyle; }
    private static GUIStyle Bold() { EnsureStyles(); return _boldStyle; }
    private static GUIStyle MiniBold() { EnsureStyles(); return _miniBoldStyle; }
    private static GUIStyle CenterMini() { EnsureStyles(); return _centerMiniStyle; }
    private static GUIStyle CenterDim() { EnsureStyles(); return _centerDimStyle; }
    private static GUIStyle WrapBold() { EnsureStyles(); return _wrapBoldStyle; }
    private static GUIStyle WrapDim() { EnsureStyles(); return _wrapDimStyle; }
    private static GUIStyle FlatButton() { EnsureStyles(); return _flatButtonStyle; }
    private static GUIStyle ActiveButton() { EnsureStyles(); return _activeButtonStyle; }
}
