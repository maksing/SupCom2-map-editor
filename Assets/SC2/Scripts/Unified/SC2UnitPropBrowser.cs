using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using SC2.MapEditor.Document;

/// <summary>
/// Runtime-compatible unit/prop picker. It deliberately indexes BP files off
/// the main thread and loads only visible DDS thumbnails on demand: opening a
/// browser must not instantiate hundreds of meshes or textures.
/// </summary>
public sealed class SC2UnitPropBrowser
{
    public enum AssetKind { Unit, Prop }

    private sealed class PropFolderOption
    {
        public readonly string Name;
        public readonly int Count;

        public PropFolderOption(string name, int count)
        {
            Name = name;
            Count = count;
        }
    }

    private const int WindowId = 184207;
    private const int BackdropWindowId = 184206;
    private const float WindowPadding = 12f;
    private const float HeaderHeight = 42f;
    private const float ScrollbarReserve = 18f;
    private const float PanelGap = 8f;
    private const int MaxCachedPreviews = 72;
    private const int ThumbnailLoadsPerRepaint = 2;
    private const float CardWidth = 144f;
    private const float CardHeight = 166f;
    private const string AllPropFolders = "All folders";
    private static readonly Dictionary<string, SC2GameAssetDiscoveryResult> SharedIndexes = new Dictionary<string, SC2GameAssetDiscoveryResult>(StringComparer.OrdinalIgnoreCase);

    // Favorite props and units, shared by every browser instance and kept across sessions. An asset is identified like the browser
    // identifies it everywhere else (AssetIdentity, which starts with its kind), so the copy of a prop under mods/<mod>/base|shadow/ is
    // the same favorite, and a unit and a prop never share one.
    private const string FavoritePropsPrefsKey = "SC2.PropBrowserFavorites";
    private static HashSet<string> _favoriteProps;
    // Where each browser was left (the Favorites switch, the folder of props or the faction of units, and the scroll position), so
    // reopening it - in the same session or the next - lands on the same page of the grid instead of starting again at the top.
    private const string PropViewPrefsKey = "SC2.PropBrowserView";
    private const string UnitViewPrefsKey = "SC2.UnitBrowserView";
    private bool _favoritesOnly;
    private GUIStyle _favoriteStarStyle;
    private GUISkin _favoriteStarSkin;

    private string _search = string.Empty;
    private bool _showUnavailable;
    private AssetKind _kind;
    private Action<SC2UnitPropBrowserSelection> _onPick;
    private Task<SC2GameAssetDiscoveryResult> _loadTask;
    private SC2GameAssetDiscoveryResult _result;
    private string _loadedRoot;
    private string _loadError;
    private Vector2 _scroll;
    private readonly Dictionary<string, Texture2D> _previewCache = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _previewOrder = new LinkedList<string>();
    private readonly Dictionary<string, LinkedListNode<string>> _previewNodes = new Dictionary<string, LinkedListNode<string>>(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _failedPreviewPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _unitIconPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private List<SC2GameAssetDiscoveryEntry> _visibleEntries = new List<SC2GameAssetDiscoveryEntry>();
    private string _visibleSignature;
    private string _selectedPropFolder = AllPropFolders;
    private List<PropFolderOption> _propFolderOptions = new List<PropFolderOption>();
    private string _propFolderSignature;
    // Units need filtering by faction (Cybran/
    // Illuminate/UEF/Scenario-Dev) and by real blueprint Categories tags
    // (Structure vs Mobile, Experimental, Land/Air/Naval/Amphibious, ...) —
    // same signature-cached-options pattern as the Props folder filter
    // above, scoped to AssetKind.Unit only (props' Categories are mostly
    // empty). Known faction tags checked in this order; anything else (or
    // no faction tag at all — scenario/dev/civilian blueprints) buckets
    // into AllFactions' implicit "Other" catch-all rather than being
    // hardcoded as its own tag, since its real name isn't confirmed.
    private static readonly string[] KnownFactionTags = { "UEF", "CYBRAN", "ILLUMINATE" };
    private const string AllFactions = "All factions";
    private const string OtherFaction = "Other / Scenario / Dev";
    private string _selectedUnitFaction = AllFactions;
    private readonly HashSet<string> _selectedUnitCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private List<string> _unitCategoryOptions = new List<string>();
    private string _unitCategorySignature;
    private bool _unitCategoriesExpanded;
    private Vector2 _categoriesScroll;
    private int _thumbnailLoadsThisRepaint;

    // Prop 3D preview: the card under the cursor is remembered across frames (the panel keeps
    // showing the last one when the cursor leaves the grid, so it can be dragged/zoomed) and only
    // becomes the previewed model after the cursor has rested on it for HoverPreviewDelaySeconds —
    // building a model parses an SCM and loads textures, which must not run for every card the
    // cursor merely passes over.
    private const float PropPreviewPanelWidth = 320f;
    // Prop cards show a still 3D render of the model instead of a texture. Each one parses an
    // SCM and loads its textures, so only one is built per repaint (cards scrolled into view fill
    // in over the next few frames) and the renders are kept in a bounded LRU cache.
    private const int MaxCachedModelThumbnails = 160;
    private const int ModelThumbnailsPerRepaint = 1;
    private const int ModelThumbnailWidth = 192;
    private const int ModelThumbnailHeight = 144;
    private readonly Dictionary<string, Texture2D> _modelThumbnails = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _modelThumbnailOrder = new LinkedList<string>();
    private readonly Dictionary<string, LinkedListNode<string>> _modelThumbnailNodes = new Dictionary<string, LinkedListNode<string>>(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _failedModelThumbnails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private int _modelThumbnailsThisRepaint;

    // Building anything (a thumbnail, or the preview model for the hovered card) is held back
    // while the grid is scrolling and for ScrollSettleSeconds after it stops, and thumbnails are
    // only built for cards actually inside the viewport: otherwise every frame of a scroll paid for
    // another model, and the cards that scrolled past were built for nothing.
    private const float ScrollSettleSeconds = 0.15f;
    private float _lastScrollY;
    private float _lastScrollChangeTime;
    private float _cardsViewHeight;
    private bool ScrollIdle => Time.realtimeSinceStartup - _lastScrollChangeTime > ScrollSettleSeconds;
    private const float HoverPreviewDelaySeconds = 0.2f;
    private readonly SC2PropModelPreview _propPreview = new SC2PropModelPreview();
    private float _contentWidth = 1096f;
    private float _cardWidth = CardWidth;
    private SC2GameAssetDiscoveryEntry _hoveredEntry;
    private SC2GameAssetDiscoveryEntry _previewedHoverCandidate;
    private float _hoveredSince;
    private SC2GameAssetDiscoveryEntry _previewedEntry;
    private GUIStyle _titleStyle, _cardLabelStyle, _cardStateReadyStyle, _cardStateIssueStyle, _fallbackLabelStyle;

    // Built once per catalog load (PollLoadTask/StartLoad's cache-hit path),
    // not per lookup - lets callers resolve an existing SC2ObjProp.
    // BlueprintPath / SC2ObjUnitNode.Type back to a real SC2GameAsset every
    // frame cheaply (O(1)), which is what SC2ObjectsShell needs to
    // auto-create a 3D preview for props/units that already exist when a
    // map is loaded/imported - previously the only way to get a preview was
    // to manually reopen that specific object's asset-picker panel, so
    // existing props/units on a freshly loaded or imported map showed no
    // model at all even though the catalog itself was already indexed.
    private Dictionary<string, SC2GameAsset> _propAssetsByGamePath;
    private Dictionary<string, SC2GameAsset> _unitAssetsById;

    public bool IsOpen => _onPick != null;
    // Shared with the startup screen and every other archive-aware system in
    // the editor (SC2GameInstallSettings.RootPath) — this browser used to
    // keep its own separate PlayerPrefs root, requiring the user to
    // configure the same gamedata folder twice. RootPath itself carries a
    // one-time migration fallback from this class's old key, so an existing
    // configured root isn't lost by this change.
    public string GameDataRoot => SC2GameInstallSettings.RootPath;
    public bool IsIndexing => _loadTask != null;
    public Action RepaintHost;

    public void Open(AssetKind kind, Action<SC2UnitPropBrowserSelection> onPick)
    {
        _kind = kind;
        _onPick = onPick ?? throw new ArgumentNullException(nameof(onPick));
        _search = string.Empty;
        _favoritesOnly = false;
        _selectedPropFolder = AllPropFolders;
        _selectedUnitFaction = AllFactions;
        _selectedUnitCategories.Clear();
        _unitCategoriesExpanded = false;
        _scroll = Vector2.zero;
        RestoreView();
        var root = GameDataRoot;
        bool rootChanged = string.IsNullOrWhiteSpace(root) || !string.Equals(_loadedRoot, root, StringComparison.OrdinalIgnoreCase);
        if ((_result == null || rootChanged) && _loadTask == null && !string.IsNullOrWhiteSpace(root)) StartLoad(false);
    }

    public void DrawModal()
    {
        if (!IsOpen) return;
        PollLoadTask();
        float width = Mathf.Min(1120f, Mathf.Max(680f, Screen.width - 80f));
        _contentWidth = width - WindowPadding * 2f;
        float height = Mathf.Min(740f, Mathf.Max(520f, Screen.height - 110f));
        var rect = new Rect((Screen.width - width) * 0.5f, (Screen.height - height) * 0.5f, width, height);

        // Same look as the texture browser: a dimmed backdrop and an opaque window. GUIStyle.none
        // keeps the skin's translucent window background out of the way; DrawWindow paints the
        // window itself. The backdrop is a window too (not a plain rect) because the hosts call
        // this from inside their own clipped panels, where a screen-sized rect would be clipped.
        GUI.Window(BackdropWindowId, new Rect(0f, 0f, Screen.width, Screen.height), DrawBackdrop, GUIContent.none, GUIStyle.none);
        GUI.BringWindowToBack(BackdropWindowId);
        GUI.ModalWindow(WindowId, rect, DrawWindow, GUIContent.none, GUIStyle.none);
    }

    private static void DrawBackdrop(int windowId)
    {
        DrawRect(new Rect(0f, 0f, Screen.width, Screen.height), new Color(0f, 0f, 0f, 0.72f));
    }

    private static void DrawRect(Rect rect, Color color)
    {
        var previous = GUI.color;
        GUI.color = color;
        GUI.DrawTexture(rect, Texture2D.whiteTexture);
        GUI.color = previous;
    }

    private static void DrawBorder(Rect rect, Color color)
    {
        DrawRect(new Rect(rect.x, rect.y, rect.width, 1f), color);
        DrawRect(new Rect(rect.x, rect.yMax - 1f, rect.width, 1f), color);
        DrawRect(new Rect(rect.x, rect.y, 1f, rect.height), color);
        DrawRect(new Rect(rect.xMax - 1f, rect.y, 1f, rect.height), color);
    }

    public void Close()
    {
        if (IsOpen) SaveView();
        _onPick = null;
        _search = string.Empty;
        _hoveredEntry = null;
        _previewedEntry = null;
        _propPreview.Dispose();
    }

    private void DrawWindow(int windowId)
    {
        // A window's callback runs after the host's own OnGUI has handed the skin back, so the editor's skin is put on again here:
        // without it the buttons and fields of this window come out in Unity's stock look.
        var previousSkin = GUI.skin;
        GUI.skin = SC2UnifiedSkin.Get();
        try { DrawWindowBody(); }
        finally { GUI.skin = previousSkin; }
    }

    private void DrawWindowBody()
    {
        _thumbnailLoadsThisRepaint = 0;
        _modelThumbnailsThisRepaint = 0;

        float windowWidth = _contentWidth + WindowPadding * 2f;
        float windowHeight = Mathf.Min(740f, Mathf.Max(520f, Screen.height - 110f));
        var windowRect = new Rect(0f, 0f, windowWidth, windowHeight);
        DrawRect(windowRect, SC2Theme.Bg2);
        DrawBorder(windowRect, SC2Theme.LineStrong);
        DrawRect(new Rect(0f, 0f, windowWidth, HeaderHeight), SC2Theme.Bg1);
        GUI.Label(new Rect(14f, 10f, 220f, 22f), _kind == AssetKind.Unit ? "Unit Browser" : "Prop Browser", TitleStyle);
        if (GUI.Button(new Rect(windowWidth - 82f, 8f, 68f, 26f), "Close")) { Close(); GUIUtility.ExitGUI(); }

        GUILayout.BeginArea(new Rect(WindowPadding, HeaderHeight + 8f, _contentWidth, windowHeight - HeaderHeight - 16f));
        DrawWindowContent();
        GUILayout.EndArea();
    }

    private void DrawWindowContent()
    {
        GUILayout.BeginVertical();

        if (_loadTask != null)
        {
            GUILayout.FlexibleSpace();
            GUILayout.Label("Indexing blueprints and resolving SCM/DDS from the game archives...", GUI.skin.box, GUILayout.Height(SC2UiScale.Pxf(42f)));
            GUILayout.FlexibleSpace();
            GUILayout.EndVertical();
            return;
        }
        if (!string.IsNullOrEmpty(_loadError)) GUILayout.Label(_loadError, GUI.skin.box);
        if (_result == null)
        {
            GUILayout.FlexibleSpace();
            GUILayout.Label("Set the gamedata root on the startup screen, then press Index.", GUI.skin.box, GUILayout.Height(SC2UiScale.Pxf(42f)));
            if (!string.IsNullOrWhiteSpace(GameDataRoot) && GUILayout.Button("Index", GUILayout.Width(SC2UiScale.Pxf(82f)))) StartLoad(true);
            GUILayout.FlexibleSpace();
            GUILayout.EndVertical();
            return;
        }

        // Units have a name search above the filters; the prop browser is kept to its favorites switch and the folder filter.
        if (_kind == AssetKind.Unit)
        {
            string nextSearch = GUILayout.TextField(_search ?? string.Empty, GUI.skin.textField, GUILayout.ExpandWidth(true));
            if (!string.Equals(nextSearch, _search, StringComparison.Ordinal)) { _search = nextSearch; InvalidateVisibleEntries(false); }
        }

        DrawFavoritesSwitch();
        if (_kind == AssetKind.Prop) DrawPropFolderFilter(); else DrawUnitFilterRow();
        var entries = GetVisibleEntries();
        if (Event.current.type == EventType.Repaint) _hoveredEntry = null;
        float cardsAreaWidth = _contentWidth - PropPreviewPanelWidth - PanelGap;
        GUILayout.BeginHorizontal();
        GUILayout.BeginVertical(GUILayout.Width(cardsAreaWidth));
        DrawCards(entries, cardsAreaWidth);
        GUILayout.EndVertical();
        GUILayout.Space(PanelGap);
        DrawPropPreviewPanel();
        GUILayout.EndHorizontal();
        GUILayout.EndVertical();
    }

    private void StartLoad(bool forceReindex)
    {
        _loadError = null;
        string root = GameDataRoot;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            _loadError = "Game data root is not configured — set it on the startup screen.";
            return;
        }

        _unitIconPaths.Clear();
        _failedPreviewPaths.Clear();
        ClearModelThumbnails();
        _secondaryLabels.Clear();
        InvalidateVisibleEntries();
        if (!forceReindex && SharedIndexes.TryGetValue(root, out _result))
        {
            _loadedRoot = root;
            BuildLookupIndexes();
            return;
        }
        _result = null;
        _loadedRoot = null;
        // A dedicated reader, independent of SC2GameArchiveRegistry's shared
        // one — this runs on a background Task.Run, and System.IO.
        // Compression.ZipArchive/ZipArchiveEntry give no thread-safety
        // guarantee for concurrent access from another thread while the main
        // thread might simultaneously use the shared reader for texture
        // loading elsewhere in the app (SC2TerrainMaterialFactory,
        // SC2TextureBrowser, ...). Opening a .scd's central-directory
        // listing a second time is cheap (confirmed elsewhere in this
        // codebase); disposed once indexing finishes, in PollLoadTask.
        _loadTask = Task.Run(() =>
        {
            using var reader = OpenIndependentArchiveReader(root);
            if (reader == null) throw new InvalidOperationException("No .scd archives found under the configured gamedata root.");
            var entryPaths = reader.Entries.Select(e => e.NormalizedPath).ToList();
            return SC2GameAssetDiscovery.BuildFromArchive(entryPaths, path => ReadArchiveEntryBytes(reader, path));
        });
    }

    /// <summary>Reads one archive entry fully into memory — ZipArchiveEntry.
    /// Open() (the real backing stream for a packed .scd) is forward-only/
    /// non-seekable, so callers that need to seek (SC2ScmModelReader.Read)
    /// buffer through this first.</summary>
    private static byte[] ReadArchiveEntryBytes(ISC2SourceContainerReader reader, string normalizedPath)
    {
        using var stream = reader.OpenEntry(normalizedPath);
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>Opens a fresh, caller-owned reader over the same archives
    /// SC2GameArchiveRegistry discovers under `root` — see StartLoad's own
    /// comment for why this is deliberately NOT the shared registry
    /// instance.</summary>
    private static ISC2SourceContainerReader OpenIndependentArchiveReader(string root)
    {
        // DiscoverArchives itself only sorts alphabetically — plain
        // filename order, with no notion of "this archive's content should
        // win when paths collide." A real install's DLC/patch content sits
        // in its own dedicated archive rather than only nested inside a
        // base archive's own "mods/" folder — e.g. z_lua_dlc1.scd carries
        // the SAME mods/DLC1/shadow/Units/.../*.bp paths bp.scd's own copy
        // already has, and per the game's own "z_" naming convention (every
        // DLC/patch archive here is z_-prefixed) it must win that path
        // collision, not the alphabetically-earlier "bp.scd". Reordering here
        // (stable — archives within each group keep DiscoverArchives' own
        // alphabetical order) is what actually decides that: the
        // SC2MultiSourceContainerReader below picks whichever reader is
        // FIRST in this list on a path collision.
        var archives = SC2GameInstallArchives.DiscoverArchives(root)
            .OrderBy(a => a.Stem.StartsWith("z_", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ToList();
        if (archives.Count == 0) return null;
        var factory = new SC2SourceContainerReaderFactory();
        var readers = new List<ISC2SourceContainerReader>();
        foreach (var archive in archives)
        {
            try { readers.Add(factory.Open(archive.Path)); }
            catch (Exception ex) { Debug.LogWarning($"[SC2 Unit/Prop Browser] Skipping archive '{archive.Path}': {ex.Message}"); }
        }
        if (readers.Count == 0) return null;
        return readers.Count == 1 ? readers[0] : new SC2MultiSourceContainerReader(readers);
    }

    /// <summary>Kicks off indexing if a game data root is configured but
    /// nothing has loaded it yet this session (no cache hit, no load
    /// already running) — lets a scene-load/import-time preview sync
    /// resolve automatically without requiring the user to have manually
    /// opened this picker first, as long as the root is already set.</summary>
    public void EnsureIndexed()
    {
        if (_result != null || _loadTask != null) return;
        if (string.IsNullOrWhiteSpace(GameDataRoot) || !Directory.Exists(GameDataRoot)) return;
        StartLoad(false);
    }

    /// <summary>Public so a caller that isn't drawing this browser's own
    /// modal (e.g. SC2MapImportPanel, which needs to wait for a
    /// pre-import reindex to finish) can still pump the background Task
    /// to completion every frame.</summary>
    public void PollLoadTask()
    {
        if (_loadTask == null || !_loadTask.IsCompleted) return;
        try
        {
            _result = _loadTask.GetAwaiter().GetResult();
            _loadedRoot = GameDataRoot;
            SharedIndexes[_loadedRoot] = _result;
            InvalidateVisibleEntries();
            BuildLookupIndexes();
        }
        catch (Exception exception) { _loadError = "Index failed: " + exception.Message; }
        finally { _loadTask = null; }
    }

    private void BuildLookupIndexes()
    {
        _propAssetsByGamePath = new Dictionary<string, SC2GameAsset>(StringComparer.OrdinalIgnoreCase);
        _unitAssetsById = new Dictionary<string, SC2GameAsset>(StringComparer.OrdinalIgnoreCase);
        if (_result == null) return;

        // Same de-dup/priority rule GetVisibleEntries already applies (a
        // blueprint id can resolve from multiple source paths, e.g. a
        // "/shadow/" or "/base/" variant of the same logical asset) - reuse
        // it here so an auto-created preview picks the same asset the user
        // would have picked by hand through the picker UI.
        foreach (var group in _result.Entries.GroupBy(AssetIdentity, StringComparer.OrdinalIgnoreCase))
        {
            var selected = SelectPreferredEntry(group);
            if (string.Equals(selected.Asset.Kind, "PropBlueprint", StringComparison.OrdinalIgnoreCase))
            {
                // Maps may store either the base or DLC alias. Both paths
                // resolve to the same preferred asset/preview implementation.
                foreach (var alias in group.Where(entry => string.Equals(entry.Asset.Kind, "PropBlueprint", StringComparison.OrdinalIgnoreCase)))
                    _propAssetsByGamePath[ToGamePath(alias.Asset.SourcePath)] = selected.Asset;
            }
            else if (string.Equals(selected.Asset.Kind, "UnitBlueprint", StringComparison.OrdinalIgnoreCase))
                _unitAssetsById[selected.Asset.Id] = selected.Asset;
        }
    }

    /// <summary>Resolves a stored SC2ObjProp.BlueprintPath ("/relative/game/
    /// path.bp") back to its catalog SC2GameAsset, or null if the catalog
    /// hasn't finished indexing yet or no matching asset exists.</summary>
    public SC2GameAsset FindPropAsset(string blueprintPath)
    {
        if (string.IsNullOrEmpty(blueprintPath) || _propAssetsByGamePath == null) return null;
        return _propAssetsByGamePath.TryGetValue(blueprintPath, out var asset) ? asset : null;
    }

    /// <summary>Resolves a stored SC2ObjUnitNode.Type (a lowercase blueprint
    /// id, e.g. "uca0101") back to its catalog SC2GameAsset, or null if the
    /// catalog hasn't finished indexing yet or no matching asset exists.</summary>
    public SC2GameAsset FindUnitAsset(string typeId)
    {
        if (string.IsNullOrEmpty(typeId) || _unitAssetsById == null) return null;
        return _unitAssetsById.TryGetValue(typeId, out var asset) ? asset : null;
    }

    /// <summary>First tag in KnownFactionTags this asset's Categories
    /// contains, or OtherFaction if none match (scenario/dev/civilian
    /// blueprints, or a faction not in that small known set).</summary>
    private static string DetectFaction(SC2GameAsset asset) =>
        KnownFactionTags.FirstOrDefault(tag => asset.Categories.Contains(tag)) ?? OtherFaction;

    private List<SC2GameAssetDiscoveryEntry> GetVisibleEntries()
    {
        string signature = _kind + "|" + _showUnavailable + "|" + _favoritesOnly + "|" + _selectedPropFolder + "|" + (_search ?? string.Empty) +
            "|" + _selectedUnitFaction + "|" + string.Join(",", _selectedUnitCategories.OrderBy(c => c, StringComparer.OrdinalIgnoreCase));
        if (signature == _visibleSignature) return _visibleEntries;

        var candidates = _result.Entries
            .Where(KindMatches)
            .GroupBy(AssetIdentity, StringComparer.OrdinalIgnoreCase)
            .Select(SelectPreferredEntry);

        if (!_showUnavailable) candidates = candidates.Where(entry => entry.IsReady);
        if (_kind == AssetKind.Prop && !string.Equals(_selectedPropFolder, AllPropFolders, StringComparison.OrdinalIgnoreCase))
            candidates = candidates.Where(entry => string.Equals(GetPropFolder(entry), _selectedPropFolder, StringComparison.OrdinalIgnoreCase));
        if (_favoritesOnly)
            candidates = candidates.Where(IsFavorite);
        if (_kind == AssetKind.Unit)
        {
            if (!string.Equals(_selectedUnitFaction, AllFactions, StringComparison.OrdinalIgnoreCase))
                candidates = candidates.Where(entry => string.Equals(DetectFaction(entry.Asset), _selectedUnitFaction, StringComparison.OrdinalIgnoreCase));
            // AND semantics: a unit must carry every checked category tag —
            // e.g. Experimental + Land together narrows to experimental
            // land units only, not either one alone.
            if (_selectedUnitCategories.Count > 0)
                candidates = candidates.Where(entry => _selectedUnitCategories.All(cat => entry.Asset.Categories.Contains(cat)));
        }
        if (!string.IsNullOrWhiteSpace(_search))
        {
            string query = _search.Trim();
            candidates = candidates.Where(entry =>
                Contains(entry.Asset.Id, query) || Contains(entry.Asset.DisplayName, query) || Contains(entry.Asset.SourcePath, query));
        }
        _visibleEntries = _kind == AssetKind.Prop
            ? candidates
                .OrderBy(GetPropFolder, StringComparer.OrdinalIgnoreCase)
                .ThenBy(GetPropRelativePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.Asset.DisplayName ?? entry.Asset.Id, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : candidates
                .OrderBy(entry => entry.Asset.DisplayName ?? entry.Asset.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();
        _visibleSignature = signature;
        return _visibleEntries;
    }

    private void DrawPropFolderFilter()
    {
        var options = GetPropFolderOptions();
        if (options.Count == 0) return;

        int selected = options.FindIndex(option => string.Equals(option.Name, _selectedPropFolder, StringComparison.OrdinalIgnoreCase));
        if (selected < 0)
        {
            _selectedPropFolder = AllPropFolders;
            selected = 0;
            InvalidateVisibleEntries(false);
        }

        GUILayout.Label("Folders");
        var labels = options
            .Select(option => new GUIContent(option.Name + " (" + option.Count + ")", option.Name))
            .ToArray();
        float windowWidth = Mathf.Min(1120f, Mathf.Max(680f, Screen.width - 80f));
        int columns = Mathf.Clamp(Mathf.FloorToInt((windowWidth - 22f) / 145f), 3, 7);
        int next = GUILayout.SelectionGrid(selected, labels, columns, GUI.skin.button);
        if (next == selected || next < 0 || next >= options.Count) return;

        _selectedPropFolder = options[next].Name;
        _scroll = Vector2.zero;
        InvalidateVisibleEntries(false);
        SaveView();
    }

    private List<PropFolderOption> GetPropFolderOptions()
    {
        string signature = _showUnavailable.ToString();
        if (signature == _propFolderSignature) return _propFolderOptions;

        var candidates = _result.Entries
            .Where(entry => string.Equals(entry.Asset.Kind, "PropBlueprint", StringComparison.OrdinalIgnoreCase))
            .GroupBy(AssetIdentity, StringComparer.OrdinalIgnoreCase)
            .Select(SelectPreferredEntry);
        if (!_showUnavailable) candidates = candidates.Where(entry => entry.IsReady);

        var grouped = candidates
            .GroupBy(GetPropFolder, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => new PropFolderOption(group.Key, group.Count()))
            .ToList();
        int total = grouped.Sum(option => option.Count);
        grouped.Insert(0, new PropFolderOption(AllPropFolders, total));
        _propFolderOptions = grouped;
        _propFolderSignature = signature;
        return _propFolderOptions;
    }

    /// <summary>Faction row (All/UEF/Cybran/Illuminate/Other) + a foldout of
    /// every OTHER real Categories tag actually present across indexed
    /// units — checkbox toggles, ANDed together in GetVisibleEntries. Built
    /// from the real catalog rather than a hardcoded taxonomy, since the
    /// full real tag vocabulary (movement type, Structure/Mobile,
    /// Experimental, weapon type, tech tier, ...) isn't known ahead of
    /// time; only the 3 faction names are recognized specially because the
    /// user asked for those as the primary grouping.</summary>
    private void DrawUnitFilterRow()
    {
        GUILayout.Label("Faction");
        var factionOptions = GetUnitFactionOptions();
        int selected = Array.IndexOf(factionOptions, _selectedUnitFaction);
        if (selected < 0) selected = 0;
        int nextFaction = GUILayout.SelectionGrid(selected, factionOptions, Mathf.Min(factionOptions.Length, 5), GUI.skin.button);
        if (nextFaction != selected)
        {
            _selectedUnitFaction = factionOptions[nextFaction];
            _scroll = Vector2.zero;
            InvalidateVisibleEntries(false);
            SaveView();
        }

        var categoryOptions = GetUnitCategoryOptions();
        if (categoryOptions.Count == 0) return;
        _unitCategoriesExpanded = GUILayout.Toggle(_unitCategoriesExpanded,
            (_unitCategoriesExpanded ? "▾ " : "▸ ") + "Categories" + (_selectedUnitCategories.Count > 0 ? $" ({_selectedUnitCategories.Count} active)" : ""),
            GUI.skin.button);
        if (!_unitCategoriesExpanded) return;

        // The tags are many (a hundred or so), so they sit in a short scrolling area with compact chips instead of a grid that
        // covers the whole window; a tag too long for its chip is cut with "...".
        var chipStyle = SC2UnifiedSkin.SmallButtonStyle;
        float areaWidth = _contentWidth - SC2UiScale.Pxf(18f);
        int columns = Mathf.Clamp(Mathf.FloorToInt(areaWidth / SC2UiScale.Pxf(118f)), 3, 10);
        float cellWidth = Mathf.Floor(areaWidth / columns) - SC2UiScale.Pxf(4f);
        _categoriesScroll = GUILayout.BeginScrollView(_categoriesScroll, false, false, GUIStyle.none, GUI.skin.verticalScrollbar,
            GUILayout.Height(SC2UiScale.Pxf(104f)));
        GUILayout.BeginHorizontal();
        for (int i = 0; i < categoryOptions.Count; i++)
        {
            if (i > 0 && i % columns == 0) { GUILayout.EndHorizontal(); GUILayout.BeginHorizontal(); }
            string category = categoryOptions[i];
            bool wasOn = _selectedUnitCategories.Contains(category);
            var content = new GUIContent(SC2StartupUi.Fit(category, chipStyle, cellWidth - chipStyle.padding.horizontal), category);
            bool nowOn = GUILayout.Toggle(wasOn, content, chipStyle, GUILayout.Width(cellWidth), GUILayout.Height(SC2UiScale.Pxf(22f)));
            if (nowOn != wasOn)
            {
                if (nowOn) _selectedUnitCategories.Add(category); else _selectedUnitCategories.Remove(category);
                InvalidateVisibleEntries(false);
            }
        }
        GUILayout.EndHorizontal();
        GUILayout.EndScrollView();
        if (_selectedUnitCategories.Count > 0 && GUILayout.Button("Clear category filters"))
        {
            _selectedUnitCategories.Clear();
            InvalidateVisibleEntries(false);
        }
    }

    private string[] GetUnitFactionOptions() =>
        new[] { AllFactions }.Concat(KnownFactionTags).Append(OtherFaction).ToArray();

    private List<string> GetUnitCategoryOptions()
    {
        string signature = _showUnavailable.ToString();
        if (signature == _unitCategorySignature) return _unitCategoryOptions;

        var known = new HashSet<string>(KnownFactionTags, StringComparer.OrdinalIgnoreCase);
        var candidates = _result.Entries
            .Where(entry => string.Equals(entry.Asset.Kind, "UnitBlueprint", StringComparison.OrdinalIgnoreCase))
            .GroupBy(AssetIdentity, StringComparer.OrdinalIgnoreCase)
            .Select(SelectPreferredEntry);
        if (!_showUnavailable) candidates = candidates.Where(entry => entry.IsReady);

        _unitCategoryOptions = candidates
            .SelectMany(entry => entry.Asset.Categories)
            .Where(tag => !known.Contains(tag))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _unitCategorySignature = signature;
        return _unitCategoryOptions;
    }

    private void InvalidateVisibleEntries(bool invalidateFolders = true)
    {
        _visibleSignature = null;
        _visibleEntries.Clear();
        if (!invalidateFolders) return;
        _propFolderSignature = null;
        _propFolderOptions.Clear();
        _unitCategorySignature = null;
        _unitCategoryOptions.Clear();
    }

    private static string GetPropFolder(SC2GameAssetDiscoveryEntry entry)
    {
        string relative = GetPropRelativePath(entry);
        if (string.IsNullOrEmpty(relative)) return "Other";
        int separator = relative.IndexOf('/');
        return separator > 0 ? relative.Substring(0, separator) : "Other";
    }

    private static string GetPropRelativePath(SC2GameAssetDiscoveryEntry entry)
    {
        string source = (entry?.Asset?.SourcePath ?? string.Empty).Replace('\\', '/');
        const string marker = "/props/";
        int markerIndex = source.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex >= 0) return source.Substring(markerIndex + marker.Length).TrimStart('/');
        if (source.StartsWith("props/", StringComparison.OrdinalIgnoreCase)) return source.Substring("props/".Length);
        return string.Empty;
    }

    private static string GetPropFolderBreadcrumb(SC2GameAssetDiscoveryEntry entry)
    {
        string relative = GetPropRelativePath(entry);
        if (string.IsNullOrEmpty(relative)) return "Other";
        int lastSeparator = relative.LastIndexOf('/');
        string folder = lastSeparator > 0 ? relative.Substring(0, lastSeparator) : GetPropFolder(entry);
        return folder.Replace("/", " / ");
    }

    // The second card line (folder path for props, id for units) is built once per entry: it used
    // to be re-derived with several string allocations for every card on every GUI event, which
    // showed up as garbage-collection hitches while scrolling.
    private readonly Dictionary<SC2GameAssetDiscoveryEntry, GUIContent> _secondaryLabels = new Dictionary<SC2GameAssetDiscoveryEntry, GUIContent>();

    private GUIContent GetSecondaryLabel(SC2GameAssetDiscoveryEntry entry)
    {
        if (_secondaryLabels.TryGetValue(entry, out var label)) return label;
        string text = _kind == AssetKind.Prop ? GetPropFolderBreadcrumb(entry) : entry.Asset.Id;
        label = new GUIContent(text, text);
        _secondaryLabels[entry] = label;
        return label;
    }

    private bool KindMatches(SC2GameAssetDiscoveryEntry entry)
    {
        return _kind == AssetKind.Unit
            ? string.Equals(entry.Asset.Kind, "UnitBlueprint", StringComparison.OrdinalIgnoreCase)
            : string.Equals(entry.Asset.Kind, "PropBlueprint", StringComparison.OrdinalIgnoreCase);
    }

    private static bool Contains(string value, string query)
    {
        return !string.IsNullOrEmpty(value) && value.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static int SourcePriority(SC2GameAssetDiscoveryEntry entry)
    {
        string source = entry.Asset.SourcePath.Replace('\\', '/');
        // mods/CampaignBranch/shadow/... is a single-player STORY-BRANCH
        // override — its stats only apply while a specific campaign mission
        // has that branch active (e.g. a scripted/boss-encounter buff), not
        // in skirmish or the map editor's own "real game data" reference
        // use. It also uses a "/shadow/" subfolder internally, so without
        // this check it tied with mods/DLC1/shadow/... at priority 3 and the
        // alphabetical tie-break ("CampaignBranch" < "DLC1") silently picked
        // the campaign-only value instead of DLC1's actual persistent stats
        // (confirmed live: UUB0301 showed RadarRadius=325 from
        // CampaignBranch instead of DLC1's 210). Checked first so this low
        // priority applies regardless of the /shadow//base/ checks below —
        // only a unit that exists NOWHERE else falls back to it.
        // NOTE: "mods" is the archive-relative path's own FIRST segment (a
        // real .scd entry name has no leading slash — confirmed directly
        // against bp.scd: entry.FullName starts with 'm', not '/'), so the
        // match must not require a leading '/' the way the mid-path
        // "/shadow/"/"/base/" checks below correctly do.
        if (source.IndexOf("mods/campaignbranch/", StringComparison.OrdinalIgnoreCase) >= 0) return 0;
        if (source.IndexOf("/shadow/", StringComparison.OrdinalIgnoreCase) >= 0) return 3;
        if (source.IndexOf("/base/", StringComparison.OrdinalIgnoreCase) >= 0) return 2;
        return 1;
    }

    private static string AssetIdentity(SC2GameAssetDiscoveryEntry entry)
    {
        string kind = entry.Asset.Kind ?? "Blueprint";
        // Prop blueprint ids repeat across folders (props/Debris/UEF/Debris_02 vs
        // props/Debris/Cybran/Debris_02), so the id alone would merge unrelated props into one
        // card. A prop is identified by its path inside props/; the only duplicates to collapse
        // are the same prop shipped again under mods/<mod>/base|shadow/.
        if (string.Equals(kind, "PropBlueprint", StringComparison.OrdinalIgnoreCase))
        {
            string source = (entry.Asset.SourcePath ?? string.Empty).Replace('\\', '/');
            int propsStart = source.StartsWith("props/", StringComparison.OrdinalIgnoreCase)
                ? 0
                : source.IndexOf("/props/", StringComparison.OrdinalIgnoreCase) + 1;
            if (propsStart > 0 || source.StartsWith("props/", StringComparison.OrdinalIgnoreCase))
                return kind + ":" + source.Substring(propsStart);
        }
        return kind + ":" + entry.Asset.Id;
    }

    private static SC2GameAssetDiscoveryEntry SelectPreferredEntry(IEnumerable<SC2GameAssetDiscoveryEntry> entries)
    {
        return entries
            .OrderByDescending(entry => entry.IsReady)
            .ThenByDescending(SourcePriority)
            .ThenBy(entry => entry.Asset.SourcePath, StringComparer.OrdinalIgnoreCase)
            .First();
    }

    private void DrawPropPreviewPanel()
    {
        if (Event.current.type == EventType.Repaint)
        {
            UpdatePreviewedEntry();
            RepaintHost?.Invoke(); // keeps the model turning and the hover delay ticking
        }

        GUILayout.BeginVertical(GUILayout.Width(PropPreviewPanelWidth));
        float side = PropPreviewPanelWidth;
        var modelRect = GUILayoutUtility.GetRect(side, side, GUILayout.ExpandWidth(false), GUILayout.ExpandHeight(false));
        _propPreview.Draw(modelRect);

        var entry = _previewedEntry;
        GUILayout.Label(entry == null ? (_kind == AssetKind.Unit ? "Hover a unit to preview it" : "Hover a prop to preview it") : entry.Asset.DisplayName ?? entry.Asset.Id, CardLabelStyle, GUILayout.Height(20f));
        GUILayout.Label(entry == null ? string.Empty : _kind == AssetKind.Unit ? entry.Asset.Id : GetPropFolderBreadcrumb(entry), CardLabelStyle, GUILayout.Height(18f));
        if (entry != null && _kind == AssetKind.Prop)
            GUILayout.Label(entry.Asset.Destructable ? "Destructible: breaks when hit" : "Not destructible: stays standing",
                entry.Asset.Destructable ? CardStateIssueStyle : CardLabelStyle, GUILayout.Height(18f));
        string status = entry == null || _propPreview.HasModel ? "Drag to rotate, wheel to zoom" : "No 3D model: " + (_propPreview.Error ?? "not loaded");
        GUILayout.Label(status, entry == null || _propPreview.HasModel ? CardStateReadyStyle : CardStateIssueStyle, GUILayout.Height(18f));
        GUILayout.FlexibleSpace();
        GUILayout.EndVertical();
    }

    /// <summary>Promotes the hovered card to the previewed one once the cursor has rested on it
    /// (see HoverPreviewDelaySeconds). Runs on Repaint only, so the panel's layout controls are
    /// identical between a frame's Layout and Repaint events.</summary>
    private void UpdatePreviewedEntry()
    {
        float now = Time.realtimeSinceStartup;
        if (_hoveredEntry != _previewedHoverCandidate)
        {
            _previewedHoverCandidate = _hoveredEntry;
            _hoveredSince = now;
        }
        if (_hoveredEntry == null || _hoveredEntry == _previewedEntry) return;
        if (now - _hoveredSince < HoverPreviewDelaySeconds || !ScrollIdle) return;
        _previewedEntry = _hoveredEntry;
        _propPreview.SetAsset(_previewedEntry.Asset, GameDataRoot);
    }

    private void DrawCards(IReadOnlyList<SC2GameAssetDiscoveryEntry> entries, float areaWidth)
    {
        // Columns fill the area exactly: the card width stretches a little past CardWidth to
        // absorb the remainder, so there is no empty strip beside the scrollbar or the preview.
        float usable = Mathf.Max(CardWidth * 2f, areaWidth - ScrollbarReserve);
        int columns = Mathf.Clamp(Mathf.FloorToInt(usable / CardWidth), 2, 8);
        _cardWidth = Mathf.Floor(usable / columns);
        float available = _cardWidth * columns;
        int rows = Mathf.CeilToInt(entries.Count / (float)columns);
        float totalHeight = Mathf.Max(1f, rows * CardHeight);

        _scroll = GUILayout.BeginScrollView(_scroll, false, true, GUIStyle.none, GUI.skin.verticalScrollbar, GUILayout.ExpandHeight(true));
        if (!Mathf.Approximately(_scroll.y, _lastScrollY))
        {
            _lastScrollY = _scroll.y;
            _lastScrollChangeTime = Time.realtimeSinceStartup;
        }
        Rect canvas = GUILayoutUtility.GetRect(available, totalHeight, GUILayout.Width(available), GUILayout.Height(totalHeight));
        float viewHeight = _cardsViewHeight > 1f ? _cardsViewHeight : Mathf.Max(260f, Screen.height - 250f);
        int visibleFirstRow = Mathf.Max(0, Mathf.FloorToInt(_scroll.y / CardHeight));
        int visibleLastRow = Mathf.FloorToInt((_scroll.y + viewHeight - 1f) / CardHeight);
        int firstRow = Mathf.Max(0, visibleFirstRow - 1);
        int lastRow = Mathf.Min(rows - 1, visibleLastRow + 1);
        bool mayBuild = ScrollIdle;
        for (int row = firstRow; row <= lastRow; row++)
        {
            for (int column = 0; column < columns; column++)
            {
                int index = row * columns + column;
                if (index >= entries.Count) break;
                Rect card = new Rect(canvas.x + column * _cardWidth, canvas.y + row * CardHeight, _cardWidth, CardHeight);
                DrawCard(card, entries[index], mayBuild && row >= visibleFirstRow && row <= visibleLastRow);
            }
        }
        GUILayout.EndScrollView();
        if (Event.current.type == EventType.Repaint) _cardsViewHeight = GUILayoutUtility.GetLastRect().height;
    }

    private void DrawCard(Rect rect, SC2GameAssetDiscoveryEntry entry, bool mayBuildThumbnail)
    {
        if (Event.current.type == EventType.Repaint && rect.Contains(Event.current.mousePosition))
            _hoveredEntry = entry;
        var cardInner = new Rect(rect.x + 2f, rect.y + 2f, rect.width - 4f, rect.height - 4f);
        bool highlighted = entry == _previewedEntry;
        DrawRect(cardInner, highlighted ? Color.Lerp(SC2Theme.Bg1, SC2Theme.Accent, 0.35f) : SC2Theme.Bg1);
        var previewRect = new Rect(rect.x + 8, rect.y + 8, rect.width - 16, 96);
        // A unit card shows the unit's game icon (its model is in the preview panel at the right); a prop card shows a render of the prop.
        Texture preview = _kind == AssetKind.Unit ? GetPreview(entry) : GetModelThumbnail(entry, mayBuildThumbnail);
        if (preview != null) DrawFittedTexture(previewRect, preview);
        else
        {
            Color old = GUI.color;
            GUI.color = _kind == AssetKind.Unit ? SC2Theme.Accent : GetPropColor(entry.Asset.Id);
            GUI.DrawTexture(previewRect, Texture2D.whiteTexture);
            GUI.color = old;
            GUI.Label(previewRect, _kind == AssetKind.Unit ? "UNIT" : "PROP", FallbackLabelStyle);
        }

        GUI.Label(new Rect(rect.x + 5, rect.y + 106, rect.width - 10, 20), entry.Asset.DisplayName ?? entry.Asset.Id, CardLabelStyle);
        GUI.Label(new Rect(rect.x + 5, rect.y + 126, rect.width - 10, 17), GetSecondaryLabel(entry), CardLabelStyle);
        string state = !entry.IsReady ? entry.Issues.First(issue => issue.IsBlocking).Kind : entry.Issues.Count == 0 ? "Ready" : "Ready (fallback)";
        GUI.Label(new Rect(rect.x + 5, rect.y + 144, rect.width - 10, 17), state, entry.IsReady ? CardStateReadyStyle : CardStateIssueStyle);
        // Before the card-sized button below, so a click on the star toggles the favorite instead of picking the prop.
        if (_kind == AssetKind.Prop) DrawDestructibleBadge(previewRect, entry.Asset.Destructable);
        DrawFavoriteStar(previewRect, entry);
        if (GUI.Button(rect, GUIContent.none, GUIStyle.none)) Select(entry);
    }

    private string ViewPrefsKey => _kind == AssetKind.Unit ? UnitViewPrefsKey : PropViewPrefsKey;

    private void SaveView()
    {
        var second = _kind == AssetKind.Unit ? _selectedUnitFaction : _selectedPropFolder ?? AllPropFolders;
        PlayerPrefs.SetString(ViewPrefsKey,
            (_favoritesOnly ? "1" : "0") + "\n" + second + "\n" + _scroll.y.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        PlayerPrefs.Save();
    }

    /// <summary>Puts back what SaveView stored. A folder that no longer exists falls back to All folders in DrawPropFolderFilter; a
    /// faction that is not one of the options is ignored.</summary>
    private void RestoreView()
    {
        var parts = PlayerPrefs.GetString(ViewPrefsKey, string.Empty).Split('\n');
        if (parts.Length != 3) return;
        _favoritesOnly = parts[0] == "1";
        if (!string.IsNullOrEmpty(parts[1]))
        {
            if (_kind == AssetKind.Prop) _selectedPropFolder = parts[1];
            else if (Array.IndexOf(GetUnitFactionOptions(), parts[1]) >= 0) _selectedUnitFaction = parts[1];
        }
        if (float.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var scrollY) && scrollY > 0f)
            _scroll = new Vector2(0f, scrollY);
    }

    /// <summary>The favorites filter, placed with the folder filter it combines with.</summary>
    private void DrawFavoritesSwitch()
    {
        bool next = GUILayout.Toggle(_favoritesOnly, "Favorites (" + FavoriteCount() + ")", GUI.skin.button, GUILayout.Width(SC2UiScale.Pxf(140f)), GUILayout.Height(SC2UiScale.Pxf(26f)));
        if (next == _favoritesOnly) return;
        _favoritesOnly = next;
        _scroll = Vector2.zero;
        InvalidateVisibleEntries(false);
        SaveView();
    }

    private static HashSet<string> FavoriteProps
    {
        get
        {
            if (_favoriteProps != null) return _favoriteProps;
            _favoriteProps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in PlayerPrefs.GetString(FavoritePropsPrefsKey, string.Empty).Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
                _favoriteProps.Add(id);
            return _favoriteProps;
        }
    }

    private static bool IsFavorite(SC2GameAssetDiscoveryEntry entry) => FavoriteProps.Contains(AssetIdentity(entry));

    /// <summary>How many favorites there are among the assets of the kind this browser shows (the set also holds the other kind's).</summary>
    private int FavoriteCount()
    {
        var prefix = (_kind == AssetKind.Unit ? "UnitBlueprint" : "PropBlueprint") + ":";
        return FavoriteProps.Count(id => id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private void ToggleFavorite(SC2GameAssetDiscoveryEntry entry)
    {
        var id = AssetIdentity(entry);
        if (!FavoriteProps.Add(id)) FavoriteProps.Remove(id);
        PlayerPrefs.SetString(FavoritePropsPrefsKey, string.Join("\n", FavoriteProps));
        PlayerPrefs.Save();
        // With the Favorites filter on, the card that was just un-starred has to leave the grid.
        if (_favoritesOnly) InvalidateVisibleEntries(false);
    }

    private GUIStyle _badgeStyle;

    /// <summary>Whether the prop breaks (its blueprint's Physics.Destructable): "Breaks" for a tree a unit knocks down, "Solid" for
    /// one that stays standing.</summary>
    private void DrawDestructibleBadge(Rect previewRect, bool destructable)
    {
        var rect = new Rect(previewRect.x + 3f, previewRect.y + 3f, 44f, 17f);
        DrawRect(rect, destructable ? new Color(0.82f, 0.42f, 0.12f, 0.95f) : new Color(0.07f, 0.08f, 0.1f, 0.8f));
        _badgeStyle ??= new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = SC2Type.Micro, fontStyle = FontStyle.Bold, clipping = TextClipping.Clip, padding = new RectOffset(0, 0, 0, 0) };
        _badgeStyle.normal.textColor = SC2Theme.TextBright;
        GUI.Label(rect, new GUIContent(destructable ? "Breaks" : "Solid", destructable ? "Destructible: breaks when a unit or a shot reaches it" : "Not destructible: stays standing"), _badgeStyle);
    }

    private void DrawFavoriteStar(Rect previewRect, SC2GameAssetDiscoveryEntry entry)
    {
        var starRect = new Rect(previewRect.xMax - 22f, previewRect.y + 3f, 19f, 19f);
        bool favorite = IsFavorite(entry);
        if (_favoriteStarSkin != GUI.skin) { _favoriteStarSkin = GUI.skin; _favoriteStarStyle = null; }
        _favoriteStarStyle ??= new GUIStyle(GUI.skin.button) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, padding = new RectOffset(0, 0, 0, 0) };
        var previousColor = GUI.backgroundColor;
        GUI.backgroundColor = favorite ? new Color(1f, 0.82f, 0.2f, 1f) : previousColor;
        if (GUI.Button(starRect, new GUIContent(favorite ? "*" : "+", favorite ? "Remove from favorites" : "Add to favorites"), _favoriteStarStyle))
            ToggleFavorite(entry);
        GUI.backgroundColor = previousColor;
    }

    private void Select(SC2GameAssetDiscoveryEntry entry)
    {
        SaveView();
        var callback = _onPick;
        _onPick = null;
        callback?.Invoke(new SC2UnitPropBrowserSelection(entry.Asset, ToGamePath(entry.Asset.SourcePath), entry.IsReady));
    }

    /// <summary>entry.Asset.SourcePath is now always a normalized
    /// archive-relative path already (see SC2GameAssetDiscovery.
    /// BuildFromArchive) — no absolute gamedata-root prefix left to strip,
    /// just the leading "/" SC2ObjProp.BlueprintPath's own contract expects
    /// (see FindPropAsset's doc comment).</summary>
    private static string ToGamePath(string sourcePath) => "/" + (sourcePath ?? string.Empty).Replace('\\', '/').TrimStart('/');

    private Texture2D GetPreview(SC2GameAssetDiscoveryEntry entry)
    {
        string path = _kind == AssetKind.Unit ? FindUnitIcon(entry.Asset.Id) : FindPropDiffuse(entry);
        if (string.IsNullOrEmpty(path) || _failedPreviewPaths.Contains(path)) return null;
        Texture2D texture;
        if (_previewCache.TryGetValue(path, out texture)) { TouchPreview(path); return texture; }
        if (Event.current.type != EventType.Repaint || _thumbnailLoadsThisRepaint >= ThumbnailLoadsPerRepaint) return null;
        if (!SC2GameArchiveRegistry.TryGetReader(out var reader) ||
            !SC2CustomTextureImporter.TryLoadPreviewFromArchive(reader, path, out texture, out _) || texture == null)
        {
            _failedPreviewPaths.Add(path);
            return null;
        }
        texture.name = "SC2 Browser Preview " + Path.GetFileName(path);
        _previewCache.Add(path, texture);
        _previewNodes[path] = _previewOrder.AddLast(path);
        _thumbnailLoadsThisRepaint++;
        while (_previewOrder.Count > MaxCachedPreviews)
        {
            string oldest = _previewOrder.First.Value;
            _previewOrder.RemoveFirst();
            _previewNodes.Remove(oldest);
            Texture2D evicted;
            if (_previewCache.TryGetValue(oldest, out evicted)) UnityEngine.Object.Destroy(evicted);
            _previewCache.Remove(oldest);
        }
        RepaintHost?.Invoke();
        return texture;
    }

    /// <summary>The cached 3D render for a prop card, or null while it is still pending (or when
    /// the prop has no model at all, in which case the card keeps its coloured placeholder).</summary>
    private Texture2D GetModelThumbnail(SC2GameAssetDiscoveryEntry entry, bool mayBuild)
    {
        string key = entry.Asset.SourcePath;
        if (string.IsNullOrEmpty(key) || _failedModelThumbnails.Contains(key)) return null;
        if (_modelThumbnails.TryGetValue(key, out var cached))
        {
            if (cached != null)
            {
                var node = _modelThumbnailNodes[key];
                _modelThumbnailOrder.Remove(node);
                _modelThumbnailOrder.AddLast(node);
                return cached;
            }
            _modelThumbnails.Remove(key);
        }
        if (!mayBuild || Event.current.type != EventType.Repaint || _modelThumbnailsThisRepaint >= ModelThumbnailsPerRepaint) return null;
        if (!entry.Asset.Lods.Any(lod => lod.ModelPaths != null && lod.ModelPaths.Count > 0))
        {
            _failedModelThumbnails.Add(key);
            return null;
        }

        _modelThumbnailsThisRepaint++;
        if (!SC2GameAssetScenePreview.TryBuildStandalonePreview(entry.Asset, GameDataRoot, out var model, out _, true))
        {
            _failedModelThumbnails.Add(key);
            return null;
        }
        Texture2D render;
        try { render = _propPreview.RenderThumbnail(model, ModelThumbnailWidth, ModelThumbnailHeight); }
        finally { model.Dispose(); }

        _modelThumbnails[key] = render;
        _modelThumbnailNodes[key] = _modelThumbnailOrder.AddLast(key);
        while (_modelThumbnailOrder.Count > MaxCachedModelThumbnails)
        {
            string oldest = _modelThumbnailOrder.First.Value;
            _modelThumbnailOrder.RemoveFirst();
            _modelThumbnailNodes.Remove(oldest);
            if (_modelThumbnails.TryGetValue(oldest, out var evicted)) DestroyThumbnail(evicted);
            _modelThumbnails.Remove(oldest);
        }
        RepaintHost?.Invoke();
        return render;
    }

    private void ClearModelThumbnails()
    {
        foreach (var render in _modelThumbnails.Values) DestroyThumbnail(render);
        _modelThumbnails.Clear();
        _modelThumbnailOrder.Clear();
        _modelThumbnailNodes.Clear();
        _failedModelThumbnails.Clear();
    }

    private static void DestroyThumbnail(Texture2D render)
    {
        if (render == null) return;
        if (Application.isPlaying) UnityEngine.Object.Destroy(render);
        else UnityEngine.Object.DestroyImmediate(render);
    }

    private void TouchPreview(string path)
    {
        LinkedListNode<string> node;
        if (!_previewNodes.TryGetValue(path, out node)) return;
        _previewOrder.Remove(node);
        _previewOrder.AddLast(node);
    }

    private string FindUnitIcon(string id)
    {
        string cached;
        if (_unitIconPaths.TryGetValue(id, out cached)) return string.IsNullOrEmpty(cached) ? null : cached;
        if (!SC2GameArchiveRegistry.TryGetReader(out var reader)) return null;
        const string icons = "textures/ui/common/icons";
        // Some units (e.g. UUX0101/UUS0105) render as flat white silhouettes
        // instead of a real colored portrait. Traced against the real game
        // data on disk: icons/units/
        // {id}_icon.dds is a small, DXT5-compressed icon that only exists
        // for SOME units and decodes to a washed-out/near-blank result —
        // while icons/hud/pc_units/{id}.dds is a much richer, uncompressed
        // 58x58 ARGB icon confirmed to have real per-pixel color detail for
        // every unit checked (both the ones that already rendered correctly
        // and the broken ones). The old candidate order checked the small
        // DXT5 icon FIRST, so any unit that happened to have one used it
        // instead of the real portrait; try hud/pc_units first now.
        string[] candidates =
        {
            icons + "/hud/pc_units/" + id + ".dds",
            icons + "/hud/pc_units/" + id + ".win.dds",
            icons + "/units/" + id + "_icon.dds",
            icons + "/units/" + id + "_icon.win.dds",
        };
        string result = candidates.FirstOrDefault(candidate =>
            reader.Entries.Any(e => string.Equals(e.NormalizedPath, candidate, StringComparison.OrdinalIgnoreCase))) ?? string.Empty;
        _unitIconPaths[id] = result;
        return string.IsNullOrEmpty(result) ? null : result;
    }

    private GUIStyle TitleStyle => _titleStyle ??= new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, fontSize = SC2Type.Nav, normal = { textColor = Color.white } };
    private GUIStyle CardLabelStyle => _cardLabelStyle ??= new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, wordWrap = false, clipping = TextClipping.Clip };
    private GUIStyle CardStateReadyStyle => _cardStateReadyStyle ??= new GUIStyle(CardLabelStyle) { normal = { textColor = new Color(0.42f, 0.9f, 0.55f) } };
    private GUIStyle CardStateIssueStyle => _cardStateIssueStyle ??= new GUIStyle(CardLabelStyle) { normal = { textColor = new Color(1f, 0.62f, 0.28f) } };
    private GUIStyle FallbackLabelStyle => _fallbackLabelStyle ??= new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };

    private static string FindPropDiffuse(SC2GameAssetDiscoveryEntry entry)
    {
        var texture = entry.Textures.FirstOrDefault(candidate =>
            string.Equals(candidate.FieldName, "DiffuseName", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(candidate.ResolvedPath));
        return texture == null ? null : texture.ResolvedPath;
    }

    private static void DrawFittedTexture(Rect rect, Texture texture)
    {
        float scale = Mathf.Min(rect.width / texture.width, rect.height / texture.height);
        float width = texture.width * scale;
        float height = texture.height * scale;
        var fitted = new Rect(rect.x + (rect.width - width) * 0.5f, rect.y + (rect.height - height) * 0.5f, width, height);
        GUI.DrawTexture(fitted, texture, ScaleMode.StretchToFill, true);
    }

    private static Color GetPropColor(string id)
    {
        unchecked
        {
            int hash = 17;
            foreach (char character in id ?? string.Empty) hash = hash * 31 + character;
            float hue = (hash & 0xFF) / 255f;
            return Color.HSVToRGB(hue, 0.48f, 0.56f);
        }
    }

}

public sealed class SC2UnitPropBrowserSelection
{
    public readonly SC2GameAsset Asset;
    public readonly string BlueprintPath;
    public readonly bool IsComplete;

    public SC2UnitPropBrowserSelection(SC2GameAsset asset, string blueprintPath, bool isComplete)
    {
        Asset = asset;
        BlueprintPath = blueprintPath;
        IsComplete = isComplete;
    }
}
