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
    private const int MaxCachedPreviews = 72;
    private const int ThumbnailLoadsPerRepaint = 2;
    private const float CardWidth = 144f;
    private const float CardHeight = 166f;
    private const string AllPropFolders = "All folders";
    private static readonly Dictionary<string, SC2GameAssetDiscoveryResult> SharedIndexes = new Dictionary<string, SC2GameAssetDiscoveryResult>(StringComparer.OrdinalIgnoreCase);

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
    private int _thumbnailLoadsThisRepaint;
    private GUIStyle _cardLabelStyle, _cardStateReadyStyle, _cardStateIssueStyle, _fallbackLabelStyle;

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
        _selectedPropFolder = AllPropFolders;
        _selectedUnitFaction = AllFactions;
        _selectedUnitCategories.Clear();
        _unitCategoriesExpanded = false;
        _scroll = Vector2.zero;
        var root = GameDataRoot;
        bool rootChanged = string.IsNullOrWhiteSpace(root) || !string.Equals(_loadedRoot, root, StringComparison.OrdinalIgnoreCase);
        if ((_result == null || rootChanged) && _loadTask == null && !string.IsNullOrWhiteSpace(root)) StartLoad(false);
    }

    public void DrawModal()
    {
        if (!IsOpen) return;
        PollLoadTask();
        float width = Mathf.Min(1120f, Mathf.Max(680f, Screen.width - 80f));
        float height = Mathf.Min(740f, Mathf.Max(520f, Screen.height - 110f));
        var rect = new Rect((Screen.width - width) * 0.5f, (Screen.height - height) * 0.5f, width, height);
        GUI.ModalWindow(WindowId, rect, DrawWindow, _kind == AssetKind.Unit ? "Unit Browser" : "Prop Browser");
    }

    public void Close()
    {
        _onPick = null;
        _search = string.Empty;
    }

    private void DrawWindow(int windowId)
    {
        _thumbnailLoadsThisRepaint = 0;
        GUILayout.BeginVertical();
        GUILayout.Label(_kind == AssetKind.Unit
            ? "Blueprint -> SCM -> DDS. Unit cards use the game's UI icon when it exists."
            : "Blueprint -> SCM -> DDS. Prop cards use their resolved diffuse texture as a lightweight thumbnail.");

        GUILayout.BeginHorizontal();
        // Read-only: the gamedata root is configured once, on the startup
        // screen (SC2GameInstallSettings.RootPath) — every archive-aware
        // system in the editor shares it, this browser included, instead of
        // asking the user to paste the same path again here.
        GUILayout.Label("Game data root: " + (string.IsNullOrWhiteSpace(GameDataRoot) ? "(not configured — set on the startup screen)" : GameDataRoot), GUILayout.ExpandWidth(true));
        // Restores whatever GUI.enabled was BEFORE this button (not
        // hardcode true) — see SC2ObjectsShell.DrawUndoRedoRow's own
        // comment for why.
        var guiEnabledBeforeIndexButton = GUI.enabled;
        GUI.enabled = guiEnabledBeforeIndexButton && _loadTask == null && !string.IsNullOrWhiteSpace(GameDataRoot);
        if (GUILayout.Button(_result == null ? "Index" : "Reindex", GUILayout.Width(SC2UiScale.Pxf(82f)))) StartLoad(true);
        GUI.enabled = guiEnabledBeforeIndexButton;
        if (GUILayout.Button("Close", GUILayout.Width(SC2UiScale.Pxf(66f)))) { Close(); GUIUtility.ExitGUI(); }
        GUILayout.EndHorizontal();

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
            GUILayout.FlexibleSpace();
            GUILayout.EndVertical();
            return;
        }

        GUILayout.BeginHorizontal();
        string nextSearch = GUILayout.TextField(_search ?? string.Empty, GUI.skin.textField, GUILayout.ExpandWidth(true));
        if (!string.Equals(nextSearch, _search, StringComparison.Ordinal)) { _search = nextSearch; InvalidateVisibleEntries(); }
        bool nextShowUnavailable = GUILayout.Toggle(_showUnavailable, "Show incomplete", GUILayout.Width(SC2UiScale.Pxf(126f)));
        if (nextShowUnavailable != _showUnavailable) { _showUnavailable = nextShowUnavailable; InvalidateVisibleEntries(); }
        GUILayout.Label(GetVisibleEntries().Count + " assets", GUILayout.Width(SC2UiScale.Pxf(76f)));
        GUILayout.EndHorizontal();

        if (_kind == AssetKind.Prop) DrawPropFolderFilter();
        if (_kind == AssetKind.Unit) DrawUnitFilterRow();
        var entries = GetVisibleEntries();
        DrawCards(entries);
        GUILayout.EndVertical();
        GUI.DragWindow(new Rect(0, 0, 10000, 22));
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
        var archives = SC2GameInstallArchives.DiscoverArchives(root);
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
        string signature = _kind + "|" + _showUnavailable + "|" + _selectedPropFolder + "|" + (_search ?? string.Empty) +
            "|" + _selectedUnitFaction + "|" + string.Join(",", _selectedUnitCategories.OrderBy(c => c, StringComparer.OrdinalIgnoreCase));
        if (signature == _visibleSignature) return _visibleEntries;

        var candidates = _result.Entries
            .Where(KindMatches)
            .GroupBy(AssetIdentity, StringComparer.OrdinalIgnoreCase)
            .Select(SelectPreferredEntry);

        if (!_showUnavailable) candidates = candidates.Where(entry => entry.IsReady);
        if (_kind == AssetKind.Prop && !string.Equals(_selectedPropFolder, AllPropFolders, StringComparison.OrdinalIgnoreCase))
            candidates = candidates.Where(entry => string.Equals(GetPropFolder(entry), _selectedPropFolder, StringComparison.OrdinalIgnoreCase));
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
        float w = Mathf.Min(1120f, Mathf.Max(680f, Screen.width - 80f));
        GUILayout.Label("Faction");
        var factionOptions = GetUnitFactionOptions();
        int selected = Array.IndexOf(factionOptions, _selectedUnitFaction);
        if (selected < 0) selected = 0;
        int nextFaction = GUILayout.SelectionGrid(selected, factionOptions, Mathf.Min(factionOptions.Length, 5), GUI.skin.button);
        if (nextFaction != selected)
        {
            _selectedUnitFaction = factionOptions[nextFaction];
            InvalidateVisibleEntries(false);
        }

        var categoryOptions = GetUnitCategoryOptions();
        if (categoryOptions.Count == 0) return;
        _unitCategoriesExpanded = GUILayout.Toggle(_unitCategoriesExpanded,
            (_unitCategoriesExpanded ? "▾ " : "▸ ") + "Categories" + (_selectedUnitCategories.Count > 0 ? $" ({_selectedUnitCategories.Count} active)" : ""),
            GUI.skin.button);
        if (!_unitCategoriesExpanded) return;

        int columns = Mathf.Clamp(Mathf.FloorToInt((w - 8f) / 130f), 2, 8);
        GUILayout.BeginHorizontal();
        for (int i = 0; i < categoryOptions.Count; i++)
        {
            if (i > 0 && i % columns == 0) { GUILayout.EndHorizontal(); GUILayout.BeginHorizontal(); }
            string category = categoryOptions[i];
            bool wasOn = _selectedUnitCategories.Contains(category);
            bool nowOn = GUILayout.Toggle(wasOn, category, GUI.skin.button, GUILayout.Width(SC2UiScale.Pxf(126f)));
            if (nowOn != wasOn)
            {
                if (nowOn) _selectedUnitCategories.Add(category); else _selectedUnitCategories.Remove(category);
                InvalidateVisibleEntries(false);
            }
        }
        GUILayout.EndHorizontal();
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
        if (source.IndexOf("/shadow/", StringComparison.OrdinalIgnoreCase) >= 0) return 3;
        if (source.IndexOf("/base/", StringComparison.OrdinalIgnoreCase) >= 0) return 2;
        return 1;
    }

    private static string AssetIdentity(SC2GameAssetDiscoveryEntry entry)
    {
        return (entry.Asset.Kind ?? "Blueprint") + ":" + entry.Asset.Id;
    }

    private static SC2GameAssetDiscoveryEntry SelectPreferredEntry(IEnumerable<SC2GameAssetDiscoveryEntry> entries)
    {
        return entries
            .OrderByDescending(entry => entry.IsReady)
            .ThenByDescending(SourcePriority)
            .ThenBy(entry => entry.Asset.SourcePath, StringComparer.OrdinalIgnoreCase)
            .First();
    }

    private void DrawCards(IReadOnlyList<SC2GameAssetDiscoveryEntry> entries)
    {
        float available = Mathf.Max(500f, Screen.width - 150f);
        int columns = Mathf.Clamp(Mathf.FloorToInt((available - 30f) / CardWidth), 3, 7);
        int rows = Mathf.CeilToInt(entries.Count / (float)columns);
        float totalHeight = Mathf.Max(1f, rows * CardHeight);

        _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));
        Rect canvas = GUILayoutUtility.GetRect(available, totalHeight, GUILayout.Width(available), GUILayout.Height(totalHeight));
        int firstRow = Mathf.Max(0, Mathf.FloorToInt(_scroll.y / CardHeight) - 1);
        int lastRow = Mathf.Min(rows - 1, Mathf.CeilToInt((_scroll.y + Mathf.Max(260f, Screen.height - 250f)) / CardHeight) + 1);
        for (int row = firstRow; row <= lastRow; row++)
        {
            for (int column = 0; column < columns; column++)
            {
                int index = row * columns + column;
                if (index >= entries.Count) break;
                Rect card = new Rect(canvas.x + column * CardWidth, canvas.y + row * CardHeight, CardWidth, CardHeight);
                DrawCard(card, entries[index]);
            }
        }
        GUILayout.EndScrollView();
    }

    private void DrawCard(Rect rect, SC2GameAssetDiscoveryEntry entry)
    {
        GUI.Box(rect, GUIContent.none);
        var previewRect = new Rect(rect.x + 8, rect.y + 8, rect.width - 16, 96);
        Texture2D preview = GetPreview(entry);
        if (preview != null) DrawFittedTexture(previewRect, preview);
        else
        {
            Color old = GUI.color;
            GUI.color = _kind == AssetKind.Unit ? new Color(0.16f, 0.35f, 0.62f) : GetPropColor(entry.Asset.Id);
            GUI.DrawTexture(previewRect, Texture2D.whiteTexture);
            GUI.color = old;
            GUI.Label(previewRect, _kind == AssetKind.Unit ? "UNIT" : "PROP", FallbackLabelStyle);
        }

        GUI.Label(new Rect(rect.x + 5, rect.y + 106, rect.width - 10, 20), entry.Asset.DisplayName ?? entry.Asset.Id, CardLabelStyle);
        string secondary = _kind == AssetKind.Prop ? GetPropFolderBreadcrumb(entry) : entry.Asset.Id;
        GUI.Label(new Rect(rect.x + 5, rect.y + 126, rect.width - 10, 17), new GUIContent(secondary, secondary), CardLabelStyle);
        string state = !entry.IsReady ? entry.Issues.First(issue => issue.IsBlocking).Kind : entry.Issues.Count == 0 ? "Ready" : "Ready (fallback)";
        GUI.Label(new Rect(rect.x + 5, rect.y + 144, rect.width - 10, 17), state, entry.IsReady ? CardStateReadyStyle : CardStateIssueStyle);
        if (GUI.Button(rect, GUIContent.none, GUIStyle.none)) Select(entry);
    }

    private void Select(SC2GameAssetDiscoveryEntry entry)
    {
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

    private static void DrawFittedTexture(Rect rect, Texture2D texture)
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
