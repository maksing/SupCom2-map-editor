using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEditor;
using SC2.MapEditor.Document;

/// <summary>
/// The mandatory first screen — both in the Editor Game View and in a real
/// Build &amp; Run player, per the approved startup-screen plan. Lets the user
/// configure a game-install root (once; SC2GameInstallSettings persists it),
/// then either open a vanilla/archive map found inside it, open one of their
/// own previously-saved maps, browse an arbitrary .scd file/folder (reusing
/// the existing Import Map panel), or create a brand new map on the live
/// runtime terrain system. Drawn as a full-screen overlay from OnGUI, same
/// GUILayout-only, no-EditorGUILayout pattern every other shell in this file
/// already uses so it renders identically in both contexts.
/// </summary>
public sealed partial class SC2GameViewEditorUI
{
    // Public (not a plain private bool) so Unity's automatic field
    // serialization carries it across an Editor domain reload the same way
    // RuntimeTerrainData already does — a private field would silently reset
    // to false on every script recompile, forcing the startup screen back
    // open mid-edit, not just once per real session.
    public bool StartupComplete;

    // Which map was most recently opened/created through this screen —
    // Editor identifies it by asset path (PlayerPrefs survives an Editor
    // restart the same way the game-install root setting does), Build by
    // the saved map name (SC2RuntimeTerrainPersistence's own identity).
    private const string LastOpenedAssetPathKey = "SC2.GameInstall.LastOpenedAssetPath";
    private const string LastOpenedMapNameKey = "SC2.GameInstall.LastOpenedMapName";

    private string _startupRootPathInput = "";
    private string _startupError;
    private bool _startupArchivesScanned;
    private List<SC2ScenarioCandidate> _startupArchiveCandidates;
    private string _startupNewMapName = "New Map";
    private float _startupNewMapSizeWu = 1024f;
    private Vector2 _startupScroll;

    // Collapsed by default — a real game install's own archives commonly
    // hold 50+ vanilla/community maps, which used to push "Your custom
    // maps" and everything else in this list far down the scroll view on
    // every single visit to this screen, even for someone who only ever
    // opens their own maps here.
    private bool _startupArchiveMapsExpanded;

    // Session-only (not persisted) — defaults to the previous fixed
    // alphabetical-by-name behavior so this is purely additive.
    private enum MapSortMode { Name, DateCreated, DateModified }
    private MapSortMode _customMapSortMode = MapSortMode.Name;

    // Loaded from Resources so both the Editor and a real Build & Run find
    // the same asset (matching SharedTerrainDataResourceName's own
    // convention) - the actual image is added later as a real Resources
    // asset at this path; until then this stays null and only the plain
    // dark tint below shows, no error either way.
    private const string StartupBackgroundResourceName = "SC2StartupBackground";
    private static Texture2D _startupBackground;
    private static bool _startupBackgroundLoadAttempted;


    private const string StartupMadeByText = "maksing";
    private const string StartupThanksToText = "FoxTermelon, Crya and r3code";

    private void DrawStartupOverlay(int screenWidth, int screenHeight)
    {
        var panelWidth = Mathf.Min(760f, screenWidth - 40f);
        var panelHeight = Mathf.Min(680f, screenHeight - 60f);
        var rect = new Rect((screenWidth - panelWidth) * 0.5f, (screenHeight - panelHeight) * 0.5f, panelWidth, panelHeight);

        if (!_startupBackgroundLoadAttempted)
        {
            _startupBackground = Resources.Load<Texture2D>(StartupBackgroundResourceName);
            _startupBackgroundLoadAttempted = true;
        }
        if (_startupBackground != null)
            GUI.DrawTexture(new Rect(0, 0, screenWidth, screenHeight), _startupBackground, ScaleMode.ScaleAndCrop);

        var prevColor = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, 0.6f);
        GUI.DrawTexture(new Rect(0, 0, screenWidth, screenHeight), Texture2D.whiteTexture);
        GUI.color = prevColor;

        GUI.Box(rect, GUIContent.none);
        var innerRect = new Rect(rect.x + 14f, rect.y + 12f, rect.width - 28f, rect.height - 24f);
        GUILayout.BeginArea(innerRect);

        GUILayout.Label("SC2 Map Editor", StartupTitleStyle());
        GUILayout.Space(8);

        // An unwanted horizontal scrollbar can show up across the whole
        // panel if none of this screen's Labels (the game-install path in
        // particular, a full OS path, plus each archive candidate's own
        // "from: <filename>" line) have any width constraint, since
        // GUILayout's vertical scroll view then sizes its content to
        // whichever Label's full, unwrapped text happens to be longest —
        // routinely wider than the panel itself. Constraining the whole
        // column to the panel's own known inner width (instead of letting
        // content dictate it) is what
        // actually fixes this — a wrap/clip style on the individual long
        // labels below is a second safety net, not the real fix on its own,
        // since GUILayout still measures a wrapped Label's natural width
        // from its UNWRAPPED text unless something else caps the column.
        GUILayout.BeginVertical(GUILayout.Width(innerRect.width), GUILayout.MaxWidth(innerRect.width));

        // While a "Delete Saved Terrain" confirmation is up (drawn on top,
        // after this content — see below), a click meant for its Cancel/
        // Delete button can otherwise land on an "Open"/"Delete" row still
        // sitting underneath at the same screen position and get consumed
        // here first, since this content is laid out earlier in the same
        // OnGUI pass. GUI.enabled = false makes these controls ignore input
        // entirely instead of reacting to (and swallowing) the click.
#if !UNITY_EDITOR
        var previousEnabled = GUI.enabled;
        if (SC2RuntimeConfirm.IsOpen) GUI.enabled = false;
#endif
        if (!SC2GameInstallSettings.IsConfigured)
            DrawStartupRootPicker();
        else
            DrawStartupMapPicker();
#if !UNITY_EDITOR
        GUI.enabled = previousEnabled;
#endif

        GUILayout.EndVertical();
        GUILayout.EndArea();

        DrawStartupCredits(screenWidth, screenHeight);

#if !UNITY_EDITOR
        // Must draw (and let it claim the click on its own Cancel/Delete
        // buttons) BEFORE the "swallow every click on the background" catch-
        // all just below — that catch-all unconditionally calls
        // Event.current.Use() on any MouseDown reaching this point, which
        // flips the event's type to EventType.Used; a control checked
        // AFTER that (this dialog's own GUILayout.Button calls) never sees
        // a real MouseDown/MouseUp at all, so its buttons would appear to
        // do nothing. Confirmed as the actual cause of a "Delete Saved
        // Terrain" confirmation's own Cancel button having no effect when
        // opened from this startup screen.
        SC2RuntimeConfirm.DrawOverlays();
#endif

        // Swallow every click on the background — nothing meaningful is
        // drawn behind this overlay anyway (no terrain shell exists yet).
        var e = Event.current;
        if (e != null && e.type == EventType.MouseDown) e.Use();
    }

    private void DrawStartupCredits(int screenWidth, int screenHeight)
    {
        if (string.IsNullOrEmpty(StartupMadeByText) && string.IsNullOrEmpty(StartupThanksToText))
            return;

        // A fixed 320x60 area is too narrow for the real credits text
        // ("Thanks to: ...") at any reasonable font size; without word-wrap
        // enabled on the labels, BeginArea's own clip would cut the line
        // off mid-word wherever it ran out of room instead of wrapping to a
        // second line.
        var width = Mathf.Min(480f, screenWidth - 24f);
        var height = 80f;
        GUILayout.BeginArea(new Rect(12f, screenHeight - height - 10f, width, height));
        var prevContentColor = GUI.contentColor;
        GUI.contentColor = new Color(0.75f, 0.75f, 0.75f);
        if (!string.IsNullOrEmpty(StartupMadeByText)) GUILayout.Label("Made by: " + StartupMadeByText, StartupWrapLabelStyle());
        if (!string.IsNullOrEmpty(StartupThanksToText)) GUILayout.Label("Thanks to: " + StartupThanksToText, StartupWrapLabelStyle());
        GUI.contentColor = prevContentColor;
        GUILayout.EndArea();
    }

    // Cached, lazily built — a plain GUI.skin.label clone with word-wrap
    // turned on, for any text here whose length isn't bounded by design
    // (an OS path, a filename) so it wraps to a second line inside the
    // now width-constrained column instead of forcing the whole panel
    // wider or clipping off mid-string.
    private static GUIStyle _startupWrapLabelStyle;
    private static GUIStyle StartupWrapLabelStyle()
    {
        if (_startupWrapLabelStyle == null)
            _startupWrapLabelStyle = new GUIStyle(GUI.skin.label) { wordWrap = true };
        return _startupWrapLabelStyle;
    }

    // The screen's own title used to share the exact same plain GUI.skin.
    // label style as every other line on it (a full OS path, a section
    // header, a map name) — nothing set it apart as the page's heading.
    private static GUIStyle _startupTitleStyle;
    private static GUIStyle StartupTitleStyle()
    {
        if (_startupTitleStyle == null)
            _startupTitleStyle = new GUIStyle(GUI.skin.label) { fontSize = SC2UiScale.F(18), fontStyle = FontStyle.Bold };
        return _startupTitleStyle;
    }

    // Secondary/de-emphasized info (the game-install path, an archive
    // candidate's "from: <file>" line) — dimmer than normal body text so
    // it reads as supporting detail, not on par with the section headers
    // and map names it sits next to.
    private static GUIStyle _startupDimLabelStyle;
    private static GUIStyle StartupDimLabelStyle()
    {
        if (_startupDimLabelStyle == null)
        {
            _startupDimLabelStyle = new GUIStyle(GUI.skin.label) { wordWrap = true };
            _startupDimLabelStyle.normal.textColor = new Color(0.62f, 0.62f, 0.66f);
        }
        return _startupDimLabelStyle;
    }

    private void DrawStartupRootPicker()
    {
        GUILayout.Label("Point this at your SupCom2 game installation folder (containing maps.scd, " +
                        "lua.scd, textures.scd, etc. — or an already-extracted copy of them).", StartupWrapLabelStyle());
        GUILayout.Space(6);
        GUILayout.BeginHorizontal();
        _startupRootPathInput = GUILayout.TextField(_startupRootPathInput ?? "", GUILayout.ExpandWidth(true));
        if (GUILayout.Button("Browse...", GUILayout.Width(SC2UiScale.Pxf(90f))))
        {
#if UNITY_EDITOR
            var picked = EditorUtility.OpenFolderPanel("Select game installation folder", "", "");
#else
            var picked = SC2RuntimeFileDialog.OpenFolder("Select game installation folder");
#endif
            if (!string.IsNullOrEmpty(picked)) _startupRootPathInput = picked;
        }
        GUILayout.EndHorizontal();

        if (!string.IsNullOrEmpty(_startupError))
        {
            var prevContentColor = GUI.contentColor;
            GUI.contentColor = new Color(0.9f, 0.4f, 0.4f);
            GUILayout.Label(_startupError, StartupWrapLabelStyle());
            GUI.contentColor = prevContentColor;
        }

        GUILayout.Space(6);
        GUI.enabled = !string.IsNullOrWhiteSpace(_startupRootPathInput);
        if (GUILayout.Button("Continue", GUILayout.Height(SC2UiScale.Pxf(28f))))
            ApplyStartupRootPath();
        GUI.enabled = true;

        GUILayout.Space(10);
        if (GUILayout.Button("Skip for now — create a new map without a game install configured", "label"))
            StartupComplete = true;
    }

    private void ApplyStartupRootPath()
    {
        if (!Directory.Exists(_startupRootPathInput))
        {
            _startupError = "That folder doesn't exist.";
            return;
        }
        SC2GameInstallSettings.RootPath = _startupRootPathInput;
        SC2GameArchiveRegistry.Reopen();
        SC2TextureCatalog.Instance.Reload();
        _startupError = null;
        _startupArchivesScanned = false;
    }

    private void DrawStartupMapPicker()
    {
        GUILayout.Label("Game install: " + SC2GameInstallSettings.RootPath, StartupDimLabelStyle());
        if (GUILayout.Button("Change game path...", GUILayout.Width(SC2UiScale.Pxf(160f))))
        {
            _startupRootPathInput = SC2GameInstallSettings.RootPath;
            SC2GameInstallSettings.RootPath = "";
            SC2GameArchiveRegistry.Close();
            return;
        }
        GUILayout.Space(10);

        _startupScroll = GUILayout.BeginScrollView(_startupScroll);

        DrawLastOpenedMapSection();
        DrawCreateNewMapSection();

        if (!_startupArchivesScanned)
            ScanStartupArchiveCandidates();
        DrawArchiveOpenFailures();

        SC2EnvFieldGui.SectionLabel("Your custom maps");
        DrawMapSortModeSelector();
#if UNITY_EDITOR
        var customAssets = new List<SC2TerrainData>();
        foreach (var guid in AssetDatabase.FindAssets("t:SC2TerrainData"))
        {
            var asset = AssetDatabase.LoadAssetAtPath<SC2TerrainData>(AssetDatabase.GUIDToAssetPath(guid));
            if (asset != null) customAssets.Add(asset);
        }
        SortCustomMaps(customAssets, a => CustomMapDisplayName(a), a => AssetDatabase.GetAssetPath(a));
        if (customAssets.Count == 0)
        {
            GUILayout.Label("No saved maps found in this project yet.");
        }
        else
        {
            foreach (var asset in customAssets)
            {
                GUILayout.BeginHorizontal(GUI.skin.box);
                GUILayout.Label(CustomMapDisplayName(asset), GUILayout.ExpandWidth(true));
                if (GUILayout.Button("Open", GUILayout.Width(SC2UiScale.Pxf(70f))))
                    OpenExistingTerrainAsset(asset);
                if (GUILayout.Button("Delete", GUILayout.Width(SC2UiScale.Pxf(70f))))
                    RequestDeleteTerrainAsset(asset);
                GUILayout.EndHorizontal();
            }
        }
#else
        var saved = SC2TerrainShell.ListSavedTerrains();
        SortCustomMaps(saved, name => name, name => SC2RuntimeTerrainPersistence.GetSavePath(name));
        if (saved.Count == 0)
        {
            GUILayout.Label("No saved maps found yet.");
        }
        else
        {
            foreach (var mapName in saved)
            {
                GUILayout.BeginHorizontal(GUI.skin.box);
                GUILayout.Label(mapName, GUILayout.ExpandWidth(true));
                if (GUILayout.Button("Open", GUILayout.Width(SC2UiScale.Pxf(70f))))
                    OpenSavedTerrainByName(mapName);
                if (GUILayout.Button("Delete", GUILayout.Width(SC2UiScale.Pxf(70f))))
                    RequestDeleteTerrainName(mapName);
                GUILayout.EndHorizontal();
            }
        }
#endif

        GUILayout.Space(14);
        var archiveCount = _startupArchiveCandidates?.Count(c => c.CanImport) ?? 0;
        var archiveLabel = "Vanilla / archive maps" + (archiveCount > 0 ? $" ({archiveCount})" : "");
        if (SC2EnvFieldGui.Foldout(ref _startupArchiveMapsExpanded, archiveLabel))
        {
            GUILayout.Space(4);
            if (archiveCount == 0)
            {
                GUILayout.Label("No maps found in the configured game install.");
            }
            else
            {
                var sorted = _startupArchiveCandidates
                    .Where(c => c.CanImport)
                    .OrderBy(c => c.DisplayName ?? c.BdfStem ?? "", StringComparer.OrdinalIgnoreCase);
                foreach (var candidate in sorted)
                {
                    GUILayout.BeginVertical(GUI.skin.box);
                    GUILayout.BeginHorizontal();
                    GUILayout.Label(candidate.DisplayName ?? candidate.BdfStem ?? "(unnamed)", GUILayout.ExpandWidth(true));
                    if (GUILayout.Button("Open", GUILayout.Width(SC2UiScale.Pxf(70f))))
                        OpenArchiveMap(candidate);
                    GUILayout.EndHorizontal();
                    if (!string.IsNullOrEmpty(candidate.SourceArchivePath))
                        GUILayout.Label("from: " + Path.GetFileName(candidate.SourceArchivePath.TrimEnd('\\', '/')), StartupDimLabelStyle());
                    GUILayout.EndVertical();
                }
            }
        }

        GUILayout.Space(14);
        if (GUILayout.Button("Browse .scd file/folder...", GUILayout.Height(SC2UiScale.Pxf(26f))))
            BeginBrowseArbitraryMap();

        GUILayout.EndScrollView();
    }

    /// <summary>Drawn right after "Last opened" (see DrawStartupMapPicker) —
    /// starting a brand new map is at least as common an entry point as
    /// resuming a previous one, so it sits with it above the project/archive
    /// map lists rather than at the very bottom of the scroll view.</summary>
    private void DrawCreateNewMapSection()
    {
        SC2EnvFieldGui.SectionLabel("Create new map");
        GUILayout.BeginHorizontal();
        GUILayout.Label("Name", GUILayout.Width(SC2UiScale.Pxf(50f)));
        _startupNewMapName = GUILayout.TextField(_startupNewMapName ?? "New Map", GUILayout.ExpandWidth(true));
        GUILayout.EndHorizontal();
        GUILayout.BeginHorizontal();
        GUILayout.Label("Size", GUILayout.Width(SC2UiScale.Pxf(50f)));
        StartupSizeButton(512);
        StartupSizeButton(1024);
        StartupSizeButton(2048);
        GUILayout.EndHorizontal();
        if (GUILayout.Button("Create New Map", GUILayout.Height(SC2UiScale.Pxf(28f))))
            CreateAndOpenNewMap(_startupNewMapName, _startupNewMapSizeWu);
        GUILayout.Space(14);
    }

#if UNITY_EDITOR
    private static string CustomMapDisplayName(SC2TerrainData asset) =>
        string.IsNullOrEmpty(asset.MapName) ? asset.name : asset.MapName;

    /// <summary>Editor counterpart to SC2GameViewEditorUI's own Build-only
    /// RequestDeleteTerrainName/ApplyDeleteTerrainName (File menu > Delete
    /// Saved Terrain) — a saved map here is a plain SC2TerrainData asset
    /// (no separate on-disk save file to also remove), so deleting it is
    /// exactly what a user would otherwise do manually in the Project
    /// window, per this screen's own "delete the .asset file from the
    /// Project window" guidance for the Editor case. EditorUtility.
    /// DisplayDialog blocks synchronously here (real Editor context, not
    /// the Build-only stub that always returns true) so this needs no
    /// async confirm overlay the way the Build path does.</summary>
    private static void RequestDeleteTerrainAsset(SC2TerrainData asset)
    {
        if (asset == null) return;
        var displayName = CustomMapDisplayName(asset);
        if (!EditorUtility.DisplayDialog(
                "Delete Saved Terrain",
                $"Permanently delete the saved terrain '{displayName}'? This cannot be undone.",
                "Delete", "Cancel"))
            return;
        AssetDatabase.DeleteAsset(AssetDatabase.GetAssetPath(asset));
    }
#endif

    /// <summary>Shows whatever map was most recently opened/created through
    /// this screen (Editor: the asset path; Build: the saved map name),
    /// pinned above every other section — see RecordLastOpenedMap.</summary>
    private void DrawLastOpenedMapSection()
    {
#if UNITY_EDITOR
        var path = PlayerPrefs.GetString(LastOpenedAssetPathKey, "");
        if (string.IsNullOrEmpty(path)) return;
        var asset = AssetDatabase.LoadAssetAtPath<SC2TerrainData>(path);
        if (asset == null) return; // moved/deleted since last time
        SC2EnvFieldGui.SectionLabel("Last opened");
        GUILayout.BeginHorizontal(GUI.skin.box);
        GUILayout.Label(CustomMapDisplayName(asset), GUILayout.ExpandWidth(true));
        if (GUILayout.Button("Open", GUILayout.Width(SC2UiScale.Pxf(70f))))
            OpenExistingTerrainAsset(asset);
        GUILayout.EndHorizontal();
#else
        var name = PlayerPrefs.GetString(LastOpenedMapNameKey, "");
        if (string.IsNullOrEmpty(name) || !SC2TerrainShell.ListSavedTerrains().Contains(name)) return;
        SC2EnvFieldGui.SectionLabel("Last opened");
        GUILayout.BeginHorizontal(GUI.skin.box);
        GUILayout.Label(name, GUILayout.ExpandWidth(true));
        if (GUILayout.Button("Open", GUILayout.Width(SC2UiScale.Pxf(70f))))
            OpenSavedTerrainByName(name);
        GUILayout.EndHorizontal();
#endif
        GUILayout.Space(14);
    }

    private void DrawArchiveOpenFailures()
    {
        var failures = SC2GameArchiveRegistry.LastOpenFailures;
        if (failures == null || failures.Count == 0) return;
        var prevContentColor = GUI.contentColor;
        GUI.contentColor = new Color(0.9f, 0.7f, 0.3f);
        foreach (var failure in failures)
            GUILayout.Label($"Couldn't open '{Path.GetFileName(failure.Path)}': {failure.Error}", StartupWrapLabelStyle());
        GUI.contentColor = prevContentColor;
        GUILayout.Space(10);
    }

    private void StartupSizeButton(float wu)
    {
        var active = Mathf.Approximately(_startupNewMapSizeWu, wu);
        var c = GUI.color;
        if (active) GUI.color = new Color(0.6f, 1f, 0.7f);
        if (GUILayout.Button($"{wu:F0}", GUILayout.Height(SC2UiScale.Pxf(24f)))) _startupNewMapSizeWu = wu;
        GUI.color = c;
    }

    private void DrawMapSortModeSelector()
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label("Sort by", GUILayout.Width(SC2UiScale.Pxf(50f)));
        MapSortModeButton(MapSortMode.Name, "Name");
        MapSortModeButton(MapSortMode.DateCreated, "Date Created");
        MapSortModeButton(MapSortMode.DateModified, "Date Modified");
        GUILayout.EndHorizontal();
        GUILayout.Space(4);
    }

    private void MapSortModeButton(MapSortMode mode, string label)
    {
        var active = _customMapSortMode == mode;
        var c = GUI.color;
        if (active) GUI.color = new Color(0.6f, 1f, 0.7f);
        if (GUILayout.Button(label, GUILayout.Height(SC2UiScale.Pxf(20f)))) _customMapSortMode = mode;
        GUI.color = c;
    }

    /// <summary>Shared by both the Editor (SC2TerrainData asset) and Build
    /// (saved-terrain name) "Your custom maps" listings — Name sorts
    /// alphabetically (the original, only-ever behavior before this sort
    /// picker existed); DateCreated/DateModified read the underlying file's
    /// own OS timestamps (the .asset file in the Editor, the .sc2terrain.json
    /// save file in a Build) and sort newest-first, since "which map did I
    /// touch most recently" is what someone picking a sort-by-date view is
    /// almost always looking for.</summary>
    private void SortCustomMaps<T>(List<T> items, Func<T, string> nameSelector, Func<T, string> pathSelector)
    {
        switch (_customMapSortMode)
        {
            case MapSortMode.DateCreated:
                items.Sort((a, b) => GetFileTimeSafe(pathSelector(b), created: true).CompareTo(GetFileTimeSafe(pathSelector(a), created: true)));
                break;
            case MapSortMode.DateModified:
                items.Sort((a, b) => GetFileTimeSafe(pathSelector(b), created: false).CompareTo(GetFileTimeSafe(pathSelector(a), created: false)));
                break;
            default:
                items.Sort((a, b) => string.Compare(nameSelector(a), nameSelector(b), StringComparison.OrdinalIgnoreCase));
                break;
        }
    }

    private static DateTime GetFileTimeSafe(string path, bool created)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return DateTime.MinValue;
            return created ? File.GetCreationTime(path) : File.GetLastWriteTime(path);
        }
        catch { return DateTime.MinValue; }
    }

    private void ScanStartupArchiveCandidates()
    {
        _startupArchivesScanned = true;
        _startupArchiveCandidates = null;
        if (!SC2GameArchiveRegistry.TryGetReader(out var reader)) return;
        try { _startupArchiveCandidates = CleanAndDedupeCandidates(SC2MapImportOrchestrator.FindCandidates(reader)); }
        catch (Exception ex) { _startupError = "Scan failed: " + ex.Message; }
    }

    // Real scenario.lua 'name' fields look like "<LOC SC2_MAPNAME_0050>[3]
    // Shiva Prime (FFA)" — the LOC tag is translation-tooling metadata kept
    // inline with the default English text right after it, not a separate
    // key needing an external localization-table lookup, so a simple prefix
    // strip recovers the real display name. Older/rougher maps sometimes
    // have no LOC tag at all (a plain dev-authored name, or just the raw
    // BdfStem) — left as-is in that case, since there's no better name to
    // recover from the data.
    private static readonly Regex StartupLocTagPattern = new Regex(@"^\s*<LOC\s+[^>]*>\s*", RegexOptions.IgnoreCase);

    private static string CleanCandidateDisplayName(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return raw;
        var stripped = StartupLocTagPattern.Replace(raw, "");
        return string.IsNullOrWhiteSpace(stripped) ? raw : stripped;
    }

    /// <summary>A real game install commonly has BOTH a compiled scenario.lua
    /// (lua.scd) and its plain-text mirror (uncompiled_lua.scd) for the same
    /// map, so FindCandidates' own "every *scenario.lua entry" scan
    /// legitimately returns the same map twice — this list is a simple map
    /// picker, not an import diagnostic view (SC2MapImportPanel keeps the
    /// raw, undeduped list for that), so collapse to one row per scenario,
    /// preferring the uncompiled/text copy — confirmed authoritative for
    /// what the real game's own UI displays.</summary>
    private static List<SC2ScenarioCandidate> CleanAndDedupeCandidates(List<SC2ScenarioCandidate> raw)
    {
        var result = new List<SC2ScenarioCandidate>();
        var indexByKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in raw)
        {
            candidate.DisplayName = CleanCandidateDisplayName(candidate.DisplayName);
            if (!candidate.CanImport)
            {
                result.Add(candidate);
                continue;
            }

            var key = ScenarioSlotKey(candidate);
            if (indexByKey.TryGetValue(key, out var existingIndex))
            {
                if (IsBetterCandidate(candidate, result[existingIndex]))
                    result[existingIndex] = candidate;
                continue;
            }

            indexByKey[key] = result.Count;
            result.Add(candidate);
        }
        return result;
    }

    /// <summary>Dedup key: the scenario.lua's OWN containing folder (its map
    /// "slot"), not BdfStem. Confirmed a real, common case against a real
    /// install: a custom map-pack scenario can legitimately point its "map"
    /// field at a DIFFERENT, already-existing terrain (reusing vanilla or
    /// another pack's geometry for a remixed layout/mode) — e.g. a real
    /// scenario at "maps/SC2_MP_305B/..." whose own map field resolves to
    /// BdfStem "SC2_MP_106", genuinely distinct from (and not a duplicate
    /// of) the real "maps/SC2_MP_106/..." scenario that also resolves to
    /// that same BdfStem. Deduping by BdfStem silently collapsed these two
    /// entirely different, independently-playable scenarios into one row,
    /// discarding whichever lost the tie-break — confirmed against a real
    /// 73-archive install where this cut the map list from 168 real distinct
    /// scenarios down to 86. Stripping a leading "uncompiled/" still
    /// correctly merges a scenario's own compiled+text copy pair, since
    /// those two ARE the same scenario at the same slot.</summary>
    private static string ScenarioSlotKey(SC2ScenarioCandidate candidate)
    {
        var path = candidate.ScenarioEntryPath ?? "";
        const string uncompiledPrefix = "uncompiled/";
        if (path.StartsWith(uncompiledPrefix, StringComparison.OrdinalIgnoreCase))
            path = path.Substring(uncompiledPrefix.Length);
        return path;
    }

    private static bool IsBetterCandidate(SC2ScenarioCandidate candidate, SC2ScenarioCandidate existing)
    {
        // Uncompiled (plain-text) scenario.lua is confirmed authoritative
        // for what the real game's own UI displays - always prefer it over
        // a compiled-bytecode copy of the same map when both exist.
        if (existing.IsCompiledBytecode != candidate.IsCompiledBytecode)
            return !candidate.IsCompiledBytecode;
        // Same "compiled-ness" - prefer whichever actually has a real name.
        return string.IsNullOrEmpty(existing.DisplayName) && !string.IsNullOrEmpty(candidate.DisplayName);
    }

    private void OpenArchiveMap(SC2ScenarioCandidate candidate)
    {
        if (!SC2GameArchiveRegistry.TryGetReader(out var reader))
        {
            SetStatus("No game archive reader available.");
            return;
        }
        EnsureTerrainShell();
        // This seeds SC2TerrainData.MapName — the TECHNICAL
        // file/folder identifier (see that field's own doc comment) — so it
        // must prefer candidate.BdfStem (the real terrain.bdf filename, e.g.
        // "SC2_D3_Eclipse"), not candidate.DisplayName (the in-game display
        // name, e.g. "!Eclipse map"). SC2MapImportOrchestrator.ImportSelected
        // below never touches data.MapName itself, so whatever gets seeded
        // here is what sticks; the real display name is picked up separately
        // (correctly) via KnownFields.Name once the Scenario tab loads.
        var data = CreateBlankInitializedTerrainData(candidate.BdfStem ?? candidate.DisplayName ?? "Imported Map", 1024f);
        AdoptNewlyCreatedTerrainData(data);

        var result = SC2MapImportOrchestrator.ImportSelected(data, _mapSession, reader, candidate);
        SetStatus(result.Summary);
        OnMapImportComplete(result);
        // Importing a vanilla/archive map here should default to
        // Import-only mode (data.ImportOnlyMode) — this whole flow's own
        // purpose is to bring in a REAL, already-authored terrain.bdf onto
        // a throwaway blank plane (CreateBlankInitializedTerrainData
        // above), so the live plane contributing geometry to the export is
        // never wanted here; leaving it off by default would require a
        // manual follow-up trip to the Terrain tab on every vanilla import
        // to avoid the z-fighting/live-plane-showing-through-gaps issue
        // ImportOnlyMode's own doc comment describes. Gated on
        // result.Success so a failed import (no real terrain.bdf geometry
        // actually added) doesn't leave the map in Import-only mode with
        // nothing to export.
        if (result.Success)
            data.ImportOnlyMode = true;
        PersistFreshlyCreatedTerrainData(data);
        RecordLastOpenedMap(data);
        StartupComplete = true;
    }

#if UNITY_EDITOR
    private void OpenExistingTerrainAsset(SC2TerrainData asset)
    {
        EnsureTerrainShell();
        RuntimeTerrainData = asset;
        _terrainShell.SetData(asset, RuntimeFieldTerrainSourceLabel(asset));
        RecordLastOpenedMap(asset);
        StartupComplete = true;
        SetStatus("Opened: " + (string.IsNullOrEmpty(asset.MapName) ? asset.name : asset.MapName));
    }
#else
    private void OpenSavedTerrainByName(string mapName)
    {
        ApplyLoadedTerrainName(mapName);
        RecordLastOpenedMap(_terrainShell?.Data);
        StartupComplete = true;
    }
#endif

    private static void RecordLastOpenedMap(SC2TerrainData data)
    {
        if (data == null) return;
#if UNITY_EDITOR
        var path = AssetDatabase.GetAssetPath(data);
        if (string.IsNullOrEmpty(path)) return;
        PlayerPrefs.SetString(LastOpenedAssetPathKey, path);
#else
        if (string.IsNullOrEmpty(data.MapName)) return;
        PlayerPrefs.SetString(LastOpenedMapNameKey, data.MapName);
#endif
        PlayerPrefs.Save();
    }

    private void BeginBrowseArbitraryMap()
    {
        EnsureTerrainShell();
        if (_terrainShell.Data == null || !_terrainShell.Data.IsInitialized)
        {
            var data = CreateBlankInitializedTerrainData("New Map", 1024f);
            AdoptNewlyCreatedTerrainData(data);
        }
        StartupComplete = true;
        OpenMapDocumentImportPanel();
    }

    private void CreateAndOpenNewMap(string mapName, float sizeWu)
    {
        EnsureTerrainShell();
        var data = CreateBlankInitializedTerrainData(string.IsNullOrWhiteSpace(mapName) ? "New Map" : mapName, sizeWu);
        AdoptNewlyCreatedTerrainData(data);
        PersistFreshlyCreatedTerrainData(data);
        RecordLastOpenedMap(data);
        StartupComplete = true;
        SetStatus("Created new map: " + data.MapName);
    }

    private static SC2TerrainData CreateBlankInitializedTerrainData(string mapName, float sizeWu)
    {
        var data = ScriptableObject.CreateInstance<SC2TerrainData>();
        data.MapName = mapName;
        var (side, cellSize) = SC2TerrainShell.ComputeFlatPlaneGrid(sizeWu);
        data.InitializeFlatPlane(side, side, cellSize);
        // ComputeFlatPlaneGrid's own `side` is a fixed triangle-budget
        // sculpting resolution, unrelated to sizeWu (the world-space size
        // the "512"/"1024"/"2048" buttons here actually pick) - CostsGridSize
        // (the exported hfield/Costs grid's own real resolution, see that
        // field's own doc comment) was left at its 1024 default regardless
        // of which size button the user picked, so a map created at "512"
        // silently exported/painted Costs at 1024.
        data.CostsGridSize = Mathf.RoundToInt(sizeWu);
        return data;
    }

    private void AdoptNewlyCreatedTerrainData(SC2TerrainData data)
    {
#if UNITY_EDITOR
        RuntimeTerrainData = data;
#endif
        _terrainShell.SetData(data, "Created: " + data.MapName);
    }

    /// <summary>Writes a real backing .asset (Editor) / named JSON save
    /// (Build) immediately, so a freshly created/imported map survives a
    /// domain reload or Editor restart even before the user explicitly hits
    /// "Save Terrain" — matching how DrawNewDataPanel's own "Create shared
    /// SC2TerrainData" button already persists right away.</summary>
    private void PersistFreshlyCreatedTerrainData(SC2TerrainData data)
    {
#if UNITY_EDITOR
        const string dir = "Assets/SC2/Resources/Maps";
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        var safeName = SanitizeAssetFileName(data.MapName);
        var path = AssetDatabase.GenerateUniqueAssetPath($"{dir}/{safeName}.asset");
        AssetDatabase.CreateAsset(data, path);
        AssetDatabase.SaveAssets();
        data.name = Path.GetFileNameWithoutExtension(path);
#else
        _terrainShell.SaveTerrainState(writeAssets: false);
#endif
    }

    private static string SanitizeAssetFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "NewMap";
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray();
        return new string(chars);
    }
}
