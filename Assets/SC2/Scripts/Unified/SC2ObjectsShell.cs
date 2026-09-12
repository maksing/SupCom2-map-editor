using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;
using SC2.MapEditor.Document;
using SC2.MapEditor.SmartExport;

/// <summary>
/// Build-compatible Objects Editor shell — operates
/// on the procedural SC2TerrainData.Obj* lists (SC2ObjectsModel.cs) instead
/// of the Editor-only SC2MapRawData scene hierarchy, so every subtab here
/// works identically in the Unity Editor and in a Build &amp; Run player (same
/// pattern as SC2EnvironmentShell/SC2CostsShell — plain GUILayout/GUI only,
/// never EditorGUILayout/EditorStyles).
///
/// UX architecture (per the practical-recommendations pass): every list-
/// bearing category (Props/Markers/Units) is a Browser (search + system
/// filter + compact rows) that swaps to a single-object Inspector the
/// moment exactly one item is selected — selection identity is by stable
/// SC2Obj*.Id (SC2TerrainData.AllocateObjectId()), not list index, so it
/// survives inserts/deletes/undo/import. Zones/Chains/Armies/Paths keep a
/// lighter single-select list+inspector (smaller lists, less churn) but
/// share the same Id-based selection convention.
/// </summary>
public sealed class SC2ObjectsShell
{
    private readonly SC2TerrainShell _terrainShell;
    /// <summary>Set by SC2GameViewEditorUI.EnsureObjectsShell(). Null is a
    /// valid, supported state (the position-drag cutover below degrades to
    /// the original legacy-only PushUndo() behavior without it) — same
    /// reasoning as SC2CostsShell.Session.</summary>
    public SC2MapSession Session;

    /// <summary>Real Smart Export dirty-tracking for the Objects domain. Almost every
    /// real edit here (delete, add, symmetry link/unlink, Graph/Zone/Chain
    /// structural edits, marker rename, the Props brush) mutates
    /// Data.ObjProps/ObjMarkers/ObjArmies/ObjUnits/ObjZones/ObjChains/
    /// ObjGraphs directly with no ISC2DocumentCommand at all — unlike
    /// Terrain sculpting/painting (one MouseUp handler each), there is no
    /// single choke point for Objects (confirmed via a full-file audit:
    /// EnsureObjectsDocumentSynced only runs once per map load/import, not
    /// per-edit, so it can't serve as one either) — every real mutation site
    /// below calls this directly. One shared helper (instead of inlining
    /// Session?.MarkDirty(SC2DirtyFlags.Objects) at each of the ~25 sites)
    /// so a future audit of "is every gap covered" is a single grep for
    /// MarkObjectsDirty rather than needing to re-derive the right flag at
    /// each call site.</summary>
    private void MarkObjectsDirty() => Session?.MarkDirty(SC2DirtyFlags.Objects);

    public Action RepaintHost;
    public Action<string> ReportStatus;
    /// <summary>Host callback so Overview's "jump to filtered list" cards can
    /// switch the pill sub-tab bar the shell itself has no control over
    /// (subTab is a parameter the host passes in, not shell-owned state).</summary>
    public Action<int> RequestSubTab;
    /// <summary>Host callback: re-center the game view camera on a world
    /// point, keeping current orientation/distance (SC2GameViewEditorUI.
    /// FocusCameraOnWorldPoint) — Objects have no Transform to feed
    /// Selection.activeTransform, so this is a dedicated hook.</summary>
    public Action<Vector3> RequestCameraFocus;
    /// <summary>Host callback: a reasonable world point "in front of the
    /// camera" for contextual Add actions, so new objects don't default to
    /// world origin (SC2GameViewEditorUI wires this to camera position +
    /// forward*40).</summary>
    public Func<Vector3> GetCameraFocusPoint;
    /// <summary>Host callback: every imported terrain.bdf GameObject's
    /// MeshCollider (SC2GameViewEditorUI owns _importedTerrains; each entry's
    /// Root now gets a MeshCollider at import time — see
    /// SC2TerrainBdfImporter.Import). Null/empty is a valid, common state
    /// (no imported terrain on this map) — height queries below just fall
    /// back to the live procedural terrain's analytic heightfield.</summary>
    public Func<List<Collider>> GetImportedTerrainColliders;

    private SC2TerrainData Data => _terrainShell?.Data;
    private bool IsReady => Data != null && Data.IsInitialized;
    /// <summary>Read by SC2GameViewEditorUI's Visibility menu to decide
    /// whether to show 2 (Land/Air) or 4 (+Naval/Amphibious) path-graph
    /// toggles — same signal RequiredGraphKinds already uses below.</summary>
    public bool PlayableWater => Data?.PlayableWater ?? false;

    private static readonly Regex ArmyPositionRe = new(@"^ARMY_[A-Za-z0-9_]+$", RegexOptions.Compiled);
    /// <summary>The plain form every generated army name uses (see
    /// NextArmyName) — a STRICT subset of ArmyPositionRe above, which also
    /// matches non-standard names (e.g. a manually-renamed "ARMY_Custom")
    /// that still export as a valid start position but aren't what any
    /// skirmish/multiplayer map's ARMY_* markers actually look like.</summary>
    private static readonly Regex ArmyCleanNameRe = new(@"^ARMY_\d+$", RegexOptions.Compiled);

    /// <summary>Exposed for SC2MarkerScenePreview, which needs to tell an
    /// Army start-position marker (typed "Blank Marker" but named ARMY_*)
    /// apart from an ordinary Blank Marker, so it can show the real Army
    /// gizmo instead of the generic Blank Marker one.</summary>
    public static bool IsArmyStartPosition(string markerName) => ArmyPositionRe.IsMatch(markerName ?? "");

    public const int TabArmies = 0, TabUnits = 1, TabProps = 2, TabMarkers = 3, TabPaths = 4, TabZones = 5, TabChains = 6;

    // ── Selection (stable Ids, per category) ────────────────────────────
    private readonly HashSet<int> _selProps = new();
    private readonly HashSet<int> _selMarkers = new();
    private readonly HashSet<int> _selUnits = new();
    private int? _lastClickedPropIdx, _lastClickedMarkerIdx, _lastClickedUnitIdx;
    private int _selectedArmyId, _selectedZoneId, _selectedChainId, _selectedGraphId;

    /// <summary>Per-Area "activate proportional edge editing" toggles for
    /// the X/Z axes — while on, dragging an Area's edge (either the numeric
    /// Left/Right/Bottom/Top fields or the viewport edge handles) also moves
    /// its opposite edge outward/inward by the same amount, instead of that
    /// edge moving alone. Replaces the old one-shot "Mirror X/Z/XZ" reflect
    /// buttons, which read as confusing/wrong one-time actions — this makes
    /// Mirror X/Z the actual activation switch for live proportional dragging rather
    /// than a single-click action.</summary>
    private bool _areaMirrorX, _areaMirrorZ;

    // ── Search / system filters ──────────────────────────────────────────
    private enum SysFilter { All, Selected, Invalid, Unanchored }
    private string _searchProps = "", _searchMarkers = "", _searchUnits = "", _searchArmies = "", _searchZones = "", _searchChains = "";
    private SysFilter _filterProps, _filterMarkers;
    private bool _propsByBlueprintExpanded;
    private Vector2 _scrollPropsByBlueprint;
    private bool _markersByTypeExpanded;
    private Vector2 _scrollMarkersByType;

    // ── Misc UI state ────────────────────────────────────────────────────
    private string _unitsArmyFilter = "";
    // Keyed by SC2ObjUnitNode.Id (not list index — indices shift on any
    // insert/delete anywhere in Data.ObjUnits, which previously left
    // collapse/edit state silently attached to the wrong node after any
    // unrelated tree edit; same fragility class already fixed for Armies).
    private readonly HashSet<int> _unitCollapsed = new();
    /// <summary>Session-only (never exported) — units hidden from the
    /// viewport's 3D preview and 2D dot overlay via a group's Hide/Isolate
    /// action. Cleared by "Show All", never persisted.</summary>
    private readonly HashSet<int> _hiddenUnitIds = new();
    /// <summary>Session-only (never exported) — props hidden from the
    /// viewport's 3D preview via the Props tab's "Hide selected"/blueprint
    /// bulk actions. Mirrors _hiddenUnitIds' own contract exactly. Cleared
    /// by "Show All", never persisted.</summary>
    private readonly HashSet<int> _hiddenPropIds = new();
    /// <summary>Session-only (never exported) — markers hidden from the
    /// viewport's 3D gizmo preview (SC2MarkerScenePreview) and flat 2D dot
    /// via the Markers tab's "By type" panel Hide/Isolate actions. Mirrors
    /// _hiddenUnitIds' own contract exactly. Cleared by "Show All", never
    /// persisted.</summary>
    private readonly HashSet<int> _hiddenMarkerIds = new();
    private bool _unitsGroupsTabActive = true; // Groups tab vs. Units tab, mirrors FAForeverMapEditor's own left-panel split
    private int _selGroupId; // the group shown by DrawSelectedGroupInspector below the tree, 0 = none
    /// <summary>Last unit actually placed/added this session — lets the
    /// Units tab keep offering "Add unit"/"Place in viewport..." (targeting
    /// that unit's own parent group) without requiring the user to
    /// re-select a group on the Groups tab first.</summary>
    private int _lastActiveUnitId;
    private Vector2 _scrollA, _scrollB, _scrollC;
    private bool _markerTemplatePicking;
    private bool _markerAdvancedExpanded;
    private string _renamePrefixMarkers = "Marker";
    private string _assignArmyInput = "";
    private string _lastImportSummary;
    private string _lastActionSummary;
    private readonly SC2UnitPropBrowser _assetBrowser = new SC2UnitPropBrowser();
    private int _lastSubTab;
    private int _pathPickMode; // 0 = none, 1 = picking Node A, 2 = picking Node B
    private int _pathNodeA = -1, _pathNodeB = -1;

    // ── Undo (JSON snapshot of the whole Objects domain) ────────────────
    [Serializable]
    private class ObjSnapshot
    {
        public List<SC2ObjProp> Props;
        public List<SC2ObjMarker> Markers;
        public List<SC2ObjGraph> Graphs;
        public List<SC2ObjArmy> Armies;
        public List<SC2ObjUnitNode> Units;
        public List<SC2ObjZone> Zones;
        public List<SC2ObjChain> Chains;
        public int NextObjectId;
        public List<SC2ObjSymmetryGroup> SymmetryGroups;
    }
    private readonly List<string> _undoStack = new();
    private readonly List<string> _redoStack = new();
    private const int MaxUndo = 30;

    /// <summary>Perf: SyncTransforms/SyncMissingAssetPreviews (called from
    /// DrawEmbeddedGUI below) used to run unconditionally on EVERY OnGUI
    /// event (Layout, Repaint, MouseMove, ...) — a real
    /// bottleneck at scale (2871 props): a full rescan of every prop/unit,
    /// rebuilding matrices and a fresh HashSet, several times per rendered
    /// frame. Bumped by PushUndo/DoUndo/DoRedo below (the established "about
    /// to mutate Data" convention already used at ~50 call sites across this
    /// file, so this piggybacks on an existing discipline instead of
    /// requiring a fresh audit of every mutation site) and consumed by
    /// DrawEmbeddedGUI to skip the resync entirely when nothing changed.
    /// Not a perfect signal (a handful of legacy call sites mutate a single
    /// object's fields directly without going through PushUndo — e.g.
    /// DrawPropInspector's raw Blueprint text field) — the periodic safety
    /// net below (PreviewSyncSafetyNetInterval) bounds the worst-case
    /// staleness from any such gap to about half a second instead of it
    /// silently going stale forever.</summary>
    private int _dataVersion;
    private int _lastSyncedDataVersion = -1;
    private int _previewSyncSafetyNetTicks;
    private const int PreviewSyncSafetyNetInterval = 30; // ~0.5s at 60 Repaint events/sec

    private void BumpDataVersion() => _dataVersion++;

    /// <summary>Same dirty-gating idea as _dataVersion above, but for
    /// SC2ObjectMarkerRenderer's own Sync — kept as a SEPARATE gate (own
    /// counter, own last-seen version) rather than reusing
    /// _previewSyncSafetyNetTicks/_lastSyncedDataVersion directly, because
    /// this one is driven from OnGameViewGUI (runs cross-tab whenever any
    /// Objects overlay flag is on — see HandleObjectsGameViewInput) while
    /// the other is driven from DrawEmbeddedGUI (Objects tab open only);
    /// sharing one counter across two call sites with different real
    /// firing frequencies would make the "~30 ticks" safety-net interval
    /// mean a different real time span depending on which tab is active.
    /// Also tracks a cheap selection signature separately from
    /// _dataVersion, since selecting/deselecting an object (a bare HashSet
    /// mutation, not preceded by PushUndo) doesn't bump _dataVersion at
    /// all, but does need an immediate visual response (selected color/
    /// size), not a half-second-late one.</summary>
    private int _lastMarkerRendererDataVersion = -1;
    private int _lastMarkerRendererSelectionSig;
    private int _markerRendererSafetyNetTicks;

    private static int ComputeSelectionSignature(HashSet<int> a, HashSet<int> b, HashSet<int> c)
    {
        var sig = 17;
        foreach (var id in a) sig = sig * 31 + id;
        foreach (var id in b) sig = sig * 31 + id + 7;
        foreach (var id in c) sig = sig * 31 + id + 13;
        return sig;
    }

    private void SyncMarkerRendererIfNeeded(bool showProps, bool showUnits, bool showMarkers)
    {
        if (Event.current != null && Event.current.type != EventType.Repaint) return;
        _markerRendererSafetyNetTicks++;
        var dueForSafetyNet = _markerRendererSafetyNetTicks >= PreviewSyncSafetyNetInterval;
        var selectionSig = ComputeSelectionSignature(_selProps, _selUnits, _selMarkers);
        if (!dueForSafetyNet && !_dragActive &&
            _dataVersion == _lastMarkerRendererDataVersion && selectionSig == _lastMarkerRendererSelectionSig)
            return;

        SC2ObjectMarkerRenderer.Sync(Data, _selProps, _selUnits, _selMarkers, _hiddenPropIds, _hiddenUnitIds, _hiddenMarkerIds,
            showProps, showUnits, showMarkers);
        _lastMarkerRendererDataVersion = _dataVersion;
        _lastMarkerRendererSelectionSig = selectionSig;
        if (dueForSafetyNet) _markerRendererSafetyNetTicks = 0;
    }

    /// <summary>Debug fallback (off by default), for the GPU
    /// billboard-marker migration's own debug path. Off: SC2ObjectMarkerRenderer
    /// (GPU-instanced billboards) draws every Prop/Unit/Marker pick-dot,
    /// a handful of Graphics.DrawMeshInstanced calls total regardless of
    /// object count. On: reverts to the original per-object IMGUI
    /// GUI.DrawTexture path (useful for visually A/B-comparing the two, or
    /// if the GPU path ever needs debugging) — the two are mutually
    /// exclusive, not drawn simultaneously (see the toggle-transition
    /// handling next to this field's UI checkbox).</summary>
    private bool _legacyImguiMarkers;
    private bool _lastLegacyImguiMarkers;

    /// <summary>Drag-to-move only: a
    /// completed viewport drag of Props/Markers/Units now records real
    /// ISC2DocumentCommand instances through Session.History instead of a
    /// whole-domain JSON snapshot — but every OTHER mutation in this shell
    /// (add/delete/duplicate/link/rename/zone-rect edits — the other ~44
    /// PushUndo() call sites) still uses the legacy snapshot stack above,
    /// unchanged. The combined marker-stack that
    /// lets this shell's Undo/Redo dispatch each step to whichever system
    /// actually owns it (in true chronological order) is now the shared
    /// SC2ShellUndoCoordinator instead of a hand-copied private enum/stack —
    /// same class SC2TerrainCombinedUndo/SC2CostsShell now use too.</summary>
    private readonly SC2ShellUndoCoordinator _undo = new();

    /// <summary>Lets the global Ctrl+Z/Y handler compare every
    /// shell's most recent action and dispatch to whichever is actually
    /// newest, instead of only ever asking Terrain.</summary>
    public bool HasUndo => _undo.CanUndo;
    public bool HasRedo => _undo.CanRedo;
    public long LastActionTimestamp => _undo.LastActionTimestamp;
    /// <summary>See SC2ShellUndoCoordinator.LastRedoTimestamp — the global
    /// Redo dispatcher must compare this, not LastActionTimestamp, when
    /// deciding which shell's Redo()/DoRedo() to call.</summary>
    public long LastRedoTimestamp => _undo.LastRedoTimestamp;

    /// <summary>Tracks which SC2TerrainData Session.Document.Objects was last
    /// synced from (see EnsureObjectsDocumentSynced) — mirrors SC2Environment
    /// Shell/SC2Collision2Shell's own _loadedForData field.</summary>
    private SC2TerrainData _objectsDocumentLoadedForData;

    [Serializable] private class PropsClipboard { public List<SC2ObjProp> Items = new(); }
    [Serializable] private class MarkersClipboard { public List<SC2ObjMarker> Items = new(); }

    public SC2ObjectsShell(SC2TerrainShell terrainShell)
    {
        _terrainShell = terrainShell;
    }

    /// <summary>Pure, non-drawing equivalent of the Groups tree's width-
    /// driving walk (DrawArmyGroupsRoot/DrawGroupTreeNode below) — computes
    /// how wide the left panel needs to be for the CURRENTLY VISIBLE (not
    /// collapsed) group tree's deepest row not to clip its trailing action
    /// buttons, without any GUILayout calls. SC2GameViewEditorUI's
    /// UpdateLayoutRects calls this directly so the panel sizes correctly
    /// on the SAME frame the Objects tab is active, every frame — no lag.
    /// (A previous version of this used a static field bumped only while
    /// the Groups tree actually drew, read one frame late by
    /// UpdateLayoutRects, with a same-frame repaint request to paper over
    /// the lag — fragile right after a map import, since the import
    /// handler switches the main tab away from Objects before the Groups
    /// tree ever gets a chance to draw and report its own width need, and
    /// depended on the requested repaint actually landing before the
    /// Editor's next Game View redraw, which isn't guaranteed.)
    /// Only returns non-zero while the Units tab's own "Groups" sub-view is
    /// what's actually showing (subTab == TabUnits AND the Groups/Units
    /// pill is on Groups) — any other Objects sub-tab (Armies, Props,
    /// Markers, Paths, Areas, Chains) or the flat "Units" list view has no
    /// group tree to clip against, so the panel must not stay artificially
    /// widened while viewing them.</summary>
    public float ComputeGroupsPanelMinWidth(int subTab)
    {
        if (Data == null || subTab != TabUnits || !_unitsGroupsTabActive) return 0f;
        var maxWidth = 0f;
        var shownArmyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var army in Data.ObjArmies)
        {
            shownArmyNames.Add(army.Name);
            maxWidth = Mathf.Max(maxWidth, ComputeArmyRootMinWidth(army.Name));
        }
        foreach (var armyName in Data.ObjUnits.Select(u => u.ArmyName).Distinct().Where(a => !shownArmyNames.Contains(a)))
            maxWidth = Mathf.Max(maxWidth, ComputeArmyRootMinWidth(armyName));
        return maxWidth;
    }

    private float ComputeArmyRootMinWidth(string armyName)
    {
        if (_unitArmyCollapsed.Contains(armyName)) return 0f;
        var maxWidth = 0f;
        foreach (var i in GetChildGroupIndices(armyName, -1))
            maxWidth = Mathf.Max(maxWidth, ComputeGroupNodeMinWidth(i, 1));
        return maxWidth;
    }

    /// <summary>Mirrors DrawGroupTreeNode's own "depth * 14f + 280f" row-
    /// width formula and its collapse-respecting recursion exactly — keep
    /// both in sync if either changes. Scaled through SC2UiScale, same as
    /// DrawGroupTreeNode's own `reserved` calc — S/H/I/- buttons used to get
    /// pushed off the panel's right edge because this generous flat
    /// estimate stayed fixed while the row's real reserved-button-width
    /// grew with Scale, so the panel this method sizes stopped being wide
    /// enough for its own content.</summary>
    private float ComputeGroupNodeMinWidth(int index, int depth)
    {
        if (index < 0 || index >= Data.ObjUnits.Count) return 0f;
        var node = Data.ObjUnits[index];
        var maxWidth = SC2UiScale.Pxf(depth * 14f + 280f);
        if (_unitCollapsed.Contains(node.Id)) return maxWidth;
        foreach (var i in GetChildGroupIndices(node.ArmyName, index))
            maxWidth = Mathf.Max(maxWidth, ComputeGroupNodeMinWidth(i, depth + 1));
        return maxWidth;
    }

    public void DrawEmbeddedGUI(float panelWidth, int subTab)
    {
        _assetBrowser.RepaintHost = RepaintHost;
        // DrawUnifiedNote/BeginUnifiedPanelBox (used throughout this file)
        // size themselves against a static width that's otherwise only ever
        // refreshed by SC2TerrainShell's own OnEmbeddedGUI — sync it to this
        // tab's real panel width too, or their content sizes against a stale
        // width and overflows under the vertical scrollbar once this tab's
        // content is tall enough to need one. See SyncEffectiveViewWidth's
        // own doc comment.
        SC2TerrainShell.SyncEffectiveViewWidth(panelWidth);
        // Arming Props' paint brush (or any click-to-place
        // tool), then switching to a different Objects sub-tab (e.g.
        // Markers) left the brush/tool armed — the next viewport click on
        // the new tab would still place/paint using the OLD tool. Any active
        // placement/paint/pick tool is scoped to the sub-tab it was armed
        // in, so leaving that sub-tab should cancel it, same as pressing Esc
        // already does (see the KeyDown.Escape handler in OnGameViewGUI).
        if (subTab != _lastSubTab)
            CancelActiveTools();
        _lastSubTab = subTab;
        // Esc should back out of a single-item detail
        // panel (the Markers/Props/Units "← Back to list" screens, and the
        // Armies/Zones/Chains inline single-selection detail) the same way
        // it already cancels an armed placement tool in the viewport.
        // Skipped while a tool is actually armed OR a rotate/scale gesture
        // is in progress, so this doesn't override Esc's existing "cancel
        // the active thing first" priority (OnGameViewGUI's own
        // KeyDown.Escape handlers) — pressing Esc while
        // rotating a selected prop was clearing the prop's selection
        // (closing its detail panel) INSTEAD of cancelling the rotation,
        // because this handler ran first in the same frame and consumed
        // the event before OnGameViewGUI's gesture-cancel handler ever saw
        // it. Only the first non-empty one closes per press, since
        // sub-tabs keep these independent in practice.
        if (_placementKind == PlacementKind.None && !_propsBrushActive && _propsGestureKind == PropsGestureKind.None &&
            Event.current != null && Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Escape)
        {
            var closedSomething = true;
            if (_selMarkers.Count == 1) _selMarkers.Clear();
            else if (_selProps.Count == 1) _selProps.Clear();
            else if (_selUnits.Count == 1) _selUnits.Clear();
            else if (_selectedArmyId != 0) _selectedArmyId = 0;
            else if (_selectedZoneId != 0) _selectedZoneId = 0;
            else if (_selectedChainId != 0) _selectedChainId = 0;
            else closedSomething = false;
            if (closedSomething)
            {
                Event.current.Use();
                RepaintHost?.Invoke();
            }
        }
        var w = Mathf.Max(1f, panelWidth);
        GUILayout.BeginVertical(GUILayout.Width(w), GUILayout.MaxWidth(w));

        if (!IsReady)
        {
            SC2TerrainShell.DrawUnifiedNote("Create or load a terrain first (Terrain tab) — Objects edits the procedural map's props, markers, armies, units, zones and chains, all stored on that same terrain asset.");
            GUILayout.EndVertical();
            _assetBrowser.DrawModal();
            return;
        }
        EnsureObjectsDocumentSynced();
        // SyncTransforms/SyncMissingAssetPreviews (real 3D prop/unit
        // preview models) and SC2MarkerScenePreview.Sync (markers) moved to
        // OnGameViewGUI — that method already runs cross-tab whenever any
        // Objects overlay flag is on (OverlayProps/Units/Markers default to
        // true — see HandleObjectsGameViewInput), not just while this
        // Objects tab panel itself is open. Indexing the catalog at load
        // time alone is not enough for prop/unit models to render on map
        // load, because THIS gated call — the
        // one that actually creates the preview instances, not just the
        // catalog lookup — would otherwise only run once the Objects tab is drawn.
        // See OnGameViewGUI's own comment for the preserved gating logic.

        DrawUndoRedoRow(w);
        DrawImportAndValidation(w);
        if (!string.IsNullOrEmpty(_missingPreviewWarning) && (subTab == TabProps || subTab == TabUnits))
            SC2EnvFieldGui.WrapLabel(_missingPreviewWarning, w);
        // Off by default — see _legacyImguiMarkers' own doc comment.
        // Visible across every Objects sub-tab (not just Props) since
        // Units/Markers share the exact same pick-dot mechanism.
        _legacyImguiMarkers = SC2EnvFieldGui.Toggle("Legacy IMGUI pick-dots (debug)", _legacyImguiMarkers);
        if (_legacyImguiMarkers != _lastLegacyImguiMarkers)
        {
            // Toggle transition — clear whichever renderer just went
            // inactive so its last-drawn dots don't linger on top of the
            // other system's fresh output for one stale frame.
            if (_legacyImguiMarkers) SC2ObjectMarkerRenderer.ClearAll();
            _lastLegacyImguiMarkers = _legacyImguiMarkers;
        }

        switch (subTab)
        {
            case TabArmies: DrawArmies(w); break;
            case TabUnits: DrawUnits(w); break;
            case TabProps: DrawProps(w); break;
            case TabMarkers: DrawMarkers(w); break;
            case TabPaths: DrawPaths(w); break;
            case TabZones: DrawZones(w); break;
            default: DrawChains(w); break;
        }

        GUILayout.Space(6);
        if (SC2EnvFieldGui.Button("Save Terrain (includes Objects)", GUILayout.Height(SC2UiScale.Pxf(26f)), GUILayout.Width(w)))
            _terrainShell.SaveTerrainState(true);
        if (!string.IsNullOrEmpty(_lastActionSummary))
            SC2EnvFieldGui.WrapLabel(_lastActionSummary, w);

        GUILayout.EndVertical();
        _assetBrowser.DrawModal();
    }

    // ── Undo/Redo ────────────────────────────────────────────────────────

    private void DrawUndoRedoRow(float w)
    {
        GUILayout.BeginHorizontal();
        // Restore whatever GUI.enabled was BEFORE this row (not hardcode
        // true) — a caller can legitimately disable the
        // whole panel around this call (SC2GameViewEditorUI does this while
        // a top-panel dropdown is open, to stop clicks reaching panel
        // content underneath it — e.g. "Path: Naval" in the Visibility menu
        // triggering "Import Objects from terrain.bdf..." underneath),
        // and unconditionally re-enabling here silently undid that for
        // everything drawn after this row.
        var guiEnabledBeforeUndoRow = GUI.enabled;
        GUI.enabled = guiEnabledBeforeUndoRow && _undo.CanUndo;
        if (SC2EnvFieldGui.Button($"↩ Undo ({_undo.UndoCount})", GUILayout.Height(SC2UiScale.Pxf(20f)), GUILayout.Width(w * 0.48f))) DoUndo();
        GUI.enabled = guiEnabledBeforeUndoRow && _undo.CanRedo;
        if (SC2EnvFieldGui.Button($"↪ Redo ({_undo.RedoCount})", GUILayout.Height(SC2UiScale.Pxf(20f)), GUILayout.Width(w * 0.48f))) DoRedo();
        GUI.enabled = guiEnabledBeforeUndoRow;
        GUILayout.EndHorizontal();
    }

    /// <summary>Deep-clones the live SC2TerrainData Obj* lists into
    /// Session.Document.Objects via the same JSON round-trip this shell's
    /// own legacy undo stack already uses (CaptureSnapshotJson/
    /// RestoreSnapshotJson) — deliberately NOT SC2ObjectsLegacyBridge.
    /// BuildFromLegacy, which shares ELEMENT REFERENCES with the live lists
    /// (see that class's own doc comment: "nothing writes live edits
    /// through this document yet"). Now that real edits DO write through it
    /// (CommitDragPositions below), sharing references would break Undo:
    /// SC2SetMarkerPositionCommand.Do() reads _previousPosition from
    /// whatever's currently on the SC2ObjMarker instance — if that instance
    /// is the SAME one the live drag just moved, "previous" would always
    /// equal "new". Document and live data must stay independent, synced
    /// explicitly like every other cut-over domain (does NOT clear
    /// Session.History itself — SC2TerrainCombinedUndo.Reset(), called from
    /// SC2TerrainShell.SetData(), already does that once at the actual
    /// load/create moment; see that class's doc comment).</summary>
    /// <summary>Public so SC2TerrainShell.DataChanged (fired at the exact
    /// load/create moment) can trigger this sync even when the Objects tab
    /// itself is never drawn this session — see that event's own doc
    /// comment. Still a no-op via
    /// the _objectsDocumentLoadedForData == Data guard below if the shell's
    /// own DrawEmbeddedGUI/OnGameViewGUI already handled this Data.</summary>
    public void EnsureObjectsDocumentSynced()
    {
        if (_objectsDocumentLoadedForData == Data) return;
        _objectsDocumentLoadedForData = Data;
        if (Session?.Document == null) return;

        var snap = JsonUtility.FromJson<ObjSnapshot>(CaptureSnapshotJson());
        var doc = Session.Document.Objects;
        doc.Props = snap.Props ?? new List<SC2ObjProp>();
        doc.Markers = snap.Markers ?? new List<SC2ObjMarker>();
        doc.Graphs = snap.Graphs ?? new List<SC2ObjGraph>();
        doc.Armies = snap.Armies ?? new List<SC2ObjArmy>();
        doc.Units = snap.Units ?? new List<SC2ObjUnitNode>();
        doc.Zones = snap.Zones ?? new List<SC2ObjZone>();
        doc.Chains = snap.Chains ?? new List<SC2ObjChain>();
        doc.SymmetryGroups = snap.SymmetryGroups ?? new List<SC2ObjSymmetryGroup>();
        doc.NextObjectId = snap.NextObjectId;

        // Unlike Props/Markers/etc. above (which drift
        // out of sync the moment the user touches any of the many Objects
        // edit paths that still don't go through a real ISC2DocumentCommand),
        // these two
        // strings are only ever WRITTEN at import time (SC2ObjectsImport.cs)
        // and never mutated again for the lifetime of a loaded map — safe to
        // capture once, here, and trust for the rest of the session.
        doc.SaveLuaTextOriginal = Data.ObjSaveLuaTextOriginal ?? "";
        doc.ScenarioLuaTextOriginal = Data.ObjScenarioLuaTextOriginal ?? "";
    }

    /// <summary>Without this, prop/unit blueprint indexing would only start
    /// once the mapmaker opened the Objects tab (SyncMissingAssetPreviews,
    /// gated to that tab's own Repaint) or the Unit/Prop Browser modal
    /// itself (SC2UnitPropBrowser.Open's own on-demand StartLoad) — a real,
    /// noticeable stall the first time either happens after a map load,
    /// even though a Game data root is already configured. Public so
    /// SC2TerrainShell.DataChanged (fired at the exact load/create moment,
    /// same wiring EnsureObjectsDocumentSynced above already uses) can kick
    /// off indexing immediately at map-load time regardless of which tab is
    /// active — SC2UnitPropBrowser.EnsureIndexed itself is already a safe
    /// no-op if a load is already running or the root isn't configured.
    /// Also polls the background indexing Task to completion (PollLoadTask):
    /// without this, indexing itself starts correctly, but models would
    /// still only appear after manually opening the Browser modal, since
    /// nothing else would consume the completed Task into the catalog's
    /// lookup dictionaries except that modal's own DrawModal call.</summary>
    public void EnsureAssetsIndexed()
    {
        _assetBrowser.PollLoadTask();
        _assetBrowser.EnsureIndexed();
    }

    private string CaptureSnapshotJson()
    {
        var snap = new ObjSnapshot
        {
            Props = Data.ObjProps,
            Markers = Data.ObjMarkers,
            Graphs = Data.ObjGraphs,
            Armies = Data.ObjArmies,
            Units = Data.ObjUnits,
            Zones = Data.ObjZones,
            Chains = Data.ObjChains,
            NextObjectId = Data.NextObjectId,
            SymmetryGroups = Data.ObjSymmetryGroups
        };
        return JsonUtility.ToJson(snap);
    }

    private void RestoreSnapshotJson(string json)
    {
        var snap = JsonUtility.FromJson<ObjSnapshot>(json);
        if (snap == null) return;
        Data.ObjProps = snap.Props ?? new List<SC2ObjProp>();
        Data.ObjMarkers = snap.Markers ?? new List<SC2ObjMarker>();
        Data.ObjGraphs = snap.Graphs ?? new List<SC2ObjGraph>();
        Data.ObjArmies = snap.Armies ?? new List<SC2ObjArmy>();
        Data.ObjUnits = snap.Units ?? new List<SC2ObjUnitNode>();
        Data.ObjZones = snap.Zones ?? new List<SC2ObjZone>();
        Data.ObjChains = snap.Chains ?? new List<SC2ObjChain>();
        Data.NextObjectId = snap.NextObjectId;
        Data.ObjSymmetryGroups = snap.SymmetryGroups ?? new List<SC2ObjSymmetryGroup>();
    }

    /// <summary>Call BEFORE any batch/destructive mutation — captures the
    /// PRE-state as one undo step, so e.g. "Duplicate with symmetry" over 8
    /// selected markers undoes as a single transaction, not 8.</summary>
    private void PushUndo()
    {
        _undoStack.Add(CaptureSnapshotJson());
        if (_undoStack.Count > MaxUndo) _undoStack.RemoveAt(0);
        _redoStack.Clear();
        _undo.Record(new SC2DelegateUndoChannel(TryLegacyUndo, TryLegacyRedo));
        BumpDataVersion();
    }

    /// <summary>Public so the global Ctrl+Z/Y handler in
    /// SC2GameViewEditorUI dispatches to whichever shell's LastActionTimestamp
    /// is newest, not only ever Terrain's own button.</summary>
    public void DoUndo()
    {
        if (!_undo.Undo()) return;
        ClearAllSelections(keepGraphSelection: true);
        BumpDataVersion();
        RepaintHost?.Invoke();
    }

    public void DoRedo()
    {
        if (!_undo.Redo()) return;
        ClearAllSelections(keepGraphSelection: true);
        BumpDataVersion();
        RepaintHost?.Invoke();
    }

    private bool TryCommandUndo()
    {
        if (Session?.Document == null || !Session.History.CanUndo) return false;
        Session.History.Undo(Session.Document);
        PullObjectsPositionsFromDocument();
        return true;
    }

    private bool TryCommandRedo()
    {
        if (Session?.Document == null || !Session.History.CanRedo) return false;
        Session.History.Redo(Session.Document);
        PullObjectsPositionsFromDocument();
        return true;
    }

    private bool TryLegacyUndo()
    {
        if (_undoStack.Count == 0) return false;
        var snap = _undoStack[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);
        _redoStack.Add(CaptureSnapshotJson());
        RestoreSnapshotJson(snap);
        return true;
    }

    private bool TryLegacyRedo()
    {
        if (_redoStack.Count == 0) return false;
        var snap = _redoStack[^1];
        _redoStack.RemoveAt(_redoStack.Count - 1);
        _undoStack.Add(CaptureSnapshotJson());
        RestoreSnapshotJson(snap);
        return true;
    }

    /// <summary>Ctrl+Z after connecting markers on the
    /// Paths tab must not reset which graph (Land/Air/Naval/Amphibious) is
    /// active, since undoing a connect/disconnect has no reason to change
    /// which graph the mapmaker is working in — DoUndo/DoRedo called this with
    /// no way to keep it. keepGraphSelection preserves _selectedGraphId for
    /// exactly that case; every other caller (e.g. after an Objects import,
    /// which can plausibly replace the whole graph set) keeps clearing it
    /// as before. Safe to leave stale even if the selected graph really was
    /// removed by the undo — every reader of _selectedGraphId already
    /// null-checks the FirstOrDefault lookup.</summary>
    private void ClearAllSelections(bool keepGraphSelection = false)
    {
        _selProps.Clear(); _selMarkers.Clear(); _selUnits.Clear();
        _selectedArmyId = _selectedZoneId = _selectedChainId = 0;
        if (!keepGraphSelection) _selectedGraphId = 0;
    }

    // ── Shared small helpers ─────────────────────────────────────────────

    private static bool MatchSearch(string haystack, string query) =>
        string.IsNullOrEmpty(query) || (haystack != null && haystack.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);

    private static string ShortBlueprint(string path)
    {
        if (string.IsNullOrEmpty(path)) return "(no blueprint)";
        var idx = path.LastIndexOf('/');
        return idx >= 0 && idx < path.Length - 1 ? path.Substring(idx + 1) : path;
    }

    /// <summary>GUILayout.Label with a fixed Width doesn't actually clip
    /// overflowing text to that rect (Unity's own layout quirk — the width
    /// only constrains layout math, not rendering), so a long filename in a
    /// fixed-width status label visibly spills past its row and overflows
    /// the panel. Truncates the string itself so it's guaranteed to fit
    /// instead of relying on style-level clipping.</summary>
    private static string TruncateForLabel(string s, float pixelWidth)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var maxChars = Mathf.Max(3, Mathf.FloorToInt(pixelWidth / 6.2f)); // ~6.2px/char, default GUI label font
        return s.Length <= maxChars ? s : s.Substring(0, maxChars - 1) + "…";
    }

    /// <summary>Ctrl = toggle membership, Shift = range from the last plain
    /// click within the CURRENT visible/filtered order, plain click =
    /// replace selection with just this item (which is what flips the tab
    /// into single-item Inspector mode).</summary>
    private void HandleRowClick(HashSet<int> selection, List<int> visibleIdsInOrder, int clickedId, ref int? lastClickedIdx)
    {
        var e = Event.current;
        var clickedIdx = visibleIdsInOrder.IndexOf(clickedId);
        if (SC2Hotkeys.ModifierHeld("Objects.SingleSelectAdditiveModifier", e))
        {
            if (!selection.Add(clickedId)) selection.Remove(clickedId);
            lastClickedIdx = clickedIdx;
        }
        else if (e != null && e.shift && lastClickedIdx.HasValue && clickedIdx >= 0)
        {
            var a = Mathf.Min(lastClickedIdx.Value, clickedIdx);
            var b = Mathf.Max(lastClickedIdx.Value, clickedIdx);
            for (var i = a; i <= b && i < visibleIdsInOrder.Count; i++) selection.Add(visibleIdsInOrder[i]);
        }
        else
        {
            selection.Clear();
            selection.Add(clickedId);
            lastClickedIdx = clickedIdx;
        }
    }

    private void HandleViewportClick(HashSet<int> selection, int clickedId)
    {
        var e = Event.current;
        if (SC2Hotkeys.ModifierHeld("Objects.SingleSelectAdditiveModifier", e))
        {
            if (!selection.Add(clickedId)) selection.Remove(clickedId);
        }
        else
        {
            selection.Clear();
            selection.Add(clickedId);
        }
    }

    private static Vector2 WorldToGui(Camera camera, Rect viewportRect, Vector3 world)
    {
        var pixel = SC2TopViewMirror.WorldToGui(camera, world);
        return new Vector2(pixel.x, pixel.y);
    }

    /// <summary>Previously sampled ONLY the live
    /// procedural terrain's analytic heightfield, so any marker/prop/unit
    /// placed on a fully-Blender-imported map (which has no real data in
    /// Data.Heights — that array only ever reflects the live default flat
    /// plane) silently landed at the wrong height. Imported terrain.bdf
    /// geometry now carries a real MeshCollider (SC2TerrainBdfImporter.
    /// Import), so a straight-down raycast against it is tried first and,
    /// when it hits, wins over the live analytic sample below — matching
    /// how the actual exported terrain.bdf/hfield.bdf already layer imported
    /// geometry on top of the live plane (SC2TerrainExporter.
    /// MergeImportedTerrain).</summary>
    private float SampleHeight(float x, float z)
    {
        if (TryRaycastImportedTerrain(new Vector3(x, 100000f, z), Vector3.down, out var importedHit))
            return importedHit.y;

        if (Data.Heights == null || Data.Heights.Length == 0 || Data.CellSize <= 0f) return 0f;
        var stride = Data.GridWidth + 1;
        var fx = Mathf.Clamp(x / Data.CellSize, 0f, Data.GridWidth);
        var fz = Mathf.Clamp(z / Data.CellSize, 0f, Data.GridHeight);
        var x0 = Mathf.FloorToInt(fx); var z0 = Mathf.FloorToInt(fz);
        var x1 = Mathf.Min(x0 + 1, Data.GridWidth); var z1 = Mathf.Min(z0 + 1, Data.GridHeight);
        var tx = fx - x0; var tz = fz - z0;
        float H(int gx, int gz) => Data.Heights[gz * stride + gx];
        var h00 = H(x0, z0); var h10 = H(x1, z0); var h01 = H(x0, z1); var h11 = H(x1, z1);
        var hx0 = Mathf.Lerp(h00, h10, tx); var hx1 = Mathf.Lerp(h01, h11, tx);
        return Mathf.Lerp(hx0, hx1, tz);
    }

    /// <summary>Closest hit across every imported-terrain MeshCollider along
    /// (origin, direction) — shared by SampleHeight (straight-down probe)
    /// and TryGetDragPoint (real view ray) so both use the same "does
    /// imported terrain exist here" logic instead of two parallel checks.</summary>
    private bool TryRaycastImportedTerrain(Vector3 origin, Vector3 direction, out Vector3 hit)
    {
        hit = default;
        var colliders = GetImportedTerrainColliders?.Invoke();
        if (colliders == null || colliders.Count == 0) return false;

        var ray = new Ray(origin, direction);
        var found = false;
        var closestDistance = float.MaxValue;
        foreach (var col in colliders)
        {
            if (col == null) continue;
            if (!col.Raycast(ray, out var rayHit, 1_000_000f)) continue;
            if (rayHit.distance >= closestDistance) continue;
            closestDistance = rayHit.distance;
            hit = rayHit.point;
            found = true;
        }
        return found;
    }

    private Vector3 GetSpawnPoint()
    {
        var focus = GetCameraFocusPoint != null ? GetCameraFocusPoint() : Vector3.zero;
        return new Vector3(focus.x, SampleHeight(focus.x, focus.z), focus.z);
    }

    /// <summary>Every non-Prop object type (Markers, Units, Armies - which
    /// are just markers under the hood) binds to whole heightmap grid cells
    /// on X/Z, unlike Props which stay freeform for natural-looking scatter.
    /// Applied everywhere a non-Prop position is created or edited: click-to-
    /// place, "Add at camera focus", inspector position fields, viewport
    /// drag, and symmetry-slot generation.</summary>
    private static Vector3 SnapXZ(Vector3 pos) => new(Mathf.Round(pos.x), pos.y, Mathf.Round(pos.z));

    private List<Vector3> GetUniqueSnappedMarkerSymmetryPositions(Vector3 position) =>
        SC2MarkerDeduplication.DistinctPositions(Data.GetSymmetricPositions(position).Select(SnapXZ));

    private SC2ObjMarker BuildMarkerPlacementCandidate(Vector3 position, string markerType)
    {
        var marker = new SC2ObjMarker
        {
            Position = SnapXZ(position),
            OrientationEuler = new Vector3(0f, _placementRotationY, 0f)
        };
        var template = SC2MarkerTemplates.Find(markerType);
        if (template != null) ApplyMarkerTemplate(marker, template);
        else marker.Type = markerType;
        return marker;
    }

    private bool HasEquivalentPlacedMarker(SC2ObjMarker candidate, int exceptId = 0) =>
        Data.ObjMarkers.Any(marker => marker.Id != exceptId && SC2MarkerDeduplication.SamePlacement(marker, candidate));

    private bool HasArmyAt(Vector3 position) =>
        Data.ObjMarkers.Any(marker => ArmyPositionRe.IsMatch(marker.Name) &&
                                      SC2MarkerDeduplication.SamePosition(marker.Position, SnapXZ(position)));

    private void ReprojectProp(SC2ObjProp p) => p.Position = new Vector3(p.Position.x, SampleHeight(p.Position.x, p.Position.z) + p.HeightOffset, p.Position.z);
    private void ReprojectMarker(SC2ObjMarker m)
    {
        var projected = new Vector3(m.Position.x, SampleHeight(m.Position.x, m.Position.z) + m.HeightOffset, m.Position.z);
        if (!HasEquivalentPlacedMarker(new SC2ObjMarker
            {
                Name = m.Name,
                Type = m.Type,
                Prop = m.Prop,
                Position = projected
            }, m.Id))
            m.Position = projected;
    }
    private void ReprojectUnit(SC2ObjUnitNode u) => u.Position = new Vector3(u.Position.x, SampleUnitHeight(u.Position.x, u.Position.z, u.Type) + u.HeightOffset, u.Position.z);

    /// <summary>Name-based heuristic for "this unit type should float on the
    /// water surface over submerged terrain rather than sink to the seabed
    /// height SampleHeight alone would give it." SC2's blueprint catalog
    /// (SC2GameAsset/SC2GameAssetCatalog) doesn't currently expose a real
    /// physics-layer/amphibious flag the way FAForeverMapEditor's own
    /// blueprint model does (BP.PhysicsLayerSub/PhysicsLayerWater,
    /// UnitInstance.GetSnapPosition) - matching that exact mechanism would
    /// need the real SC2 .bp field name confirmed first. This substring
    /// heuristic is a placeholder signal, safe to replace once that's
    /// confirmed (it only ever changes WHERE a unit's height sits, nothing
    /// about its Type/data), not a claim that it's blueprint-verified.</summary>
    private static bool IsAmphibiousUnitType(string type)
    {
        if (string.IsNullOrEmpty(type)) return false;
        return type.IndexOf("naval", StringComparison.OrdinalIgnoreCase) >= 0
            || type.IndexOf("amphib", StringComparison.OrdinalIgnoreCase) >= 0
            || type.IndexOf("sub", StringComparison.OrdinalIgnoreCase) >= 0
            || type.IndexOf("ship", StringComparison.OrdinalIgnoreCase) >= 0
            || type.IndexOf("boat", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>Water-aware variant of SampleHeight for unit placement —
    /// mirrors FAForeverMapEditor's UnitInstance.GetSnapPosition, simplified
    /// to the data we actually have (no per-blueprint PhysicsElevation to
    /// interpolate against, so it's a hard surface-vs-seabed choice, not a
    /// graded blend): a naval/amphibious unit type sitting over terrain
    /// that's below the map's WaterElevation floats at the water surface
    /// instead of sinking to the submerged terrain height.</summary>
    private float SampleUnitHeight(float x, float z, string unitType)
    {
        var terrainHeight = SampleHeight(x, z);
        if (!IsAmphibiousUnitType(unitType)) return terrainHeight;

        var waterElevation = SC2ObjectsExport.GetWaterElevation(Data, Session?.Document?.Environment?.WorkingHeader);
        return terrainHeight < waterElevation ? waterElevation : terrainHeight;
    }

    // ── Live symmetry links (practical-recommendations #3) ──────────────
    // A group's Members each record which "slot" of Data.GetSymmetricPositions
    // they occupy. When one member moves, we infer what the CANONICAL
    // (slot-0) position would have to be to produce that member's new
    // position — using the per-mode inverse of the same formulas
    // SC2TerrainData.GetSymmetricPositions already applies — then
    // regenerate every other member's position from that canonical point.
    // This lets you drag ANY member (not just the "original") and have the
    // rest follow correctly, including for true-rotation modes (Radial6/8).

    private SC2ObjSymmetryGroup FindSymmetryGroup(int objectId) =>
        Data.ObjSymmetryGroups.FirstOrDefault(g => g.Members.Any(m => m.ObjectId == objectId));

    private Vector3 InferCanonicalFromSlot(Vector3 slotPos, int slotIndex)
    {
        if (slotIndex == 0) return slotPos;
        var c = Data.SymmetryCenter;
        var rel = slotPos - c;
        switch (Data.Symmetry)
        {
            case SymmetryMode.X: return c + new Vector3(-rel.x, rel.y, rel.z); // self-inverse
            case SymmetryMode.Y: return c + new Vector3(rel.x, rel.y, -rel.z); // self-inverse
            case SymmetryMode.XY:
                // GetSymmetricPositions' slot order for XY: 1=mirrorX, 2=mirrorZ, 3=mirrorXZ — each self-inverse.
                return slotIndex switch
                {
                    1 => c + new Vector3(-rel.x, rel.y, rel.z),
                    2 => c + new Vector3(rel.x, rel.y, -rel.z),
                    3 => c + new Vector3(-rel.x, rel.y, -rel.z),
                    _ => slotPos
                };
            case SymmetryMode.Radial:
                // Same SC2RadialSymmetryMath.RotationAngleSteps sequence
                // GetSymmetricPositions uses to build the forward slots —
                // index into it the same way, then rotate backward by that
                // step to invert it.
                var steps = SC2RadialSymmetryMath.RotationAngleSteps(Data.RadialCount).ToList();
                if (slotIndex - 1 < 0 || slotIndex - 1 >= steps.Count) return slotPos;
                var ang = Mathf.Atan2(rel.z, rel.x); var r = new Vector2(rel.x, rel.z).magnitude;
                var inverseAngle = ang - steps[slotIndex - 1] * Mathf.Deg2Rad;
                return c + new Vector3(r * Mathf.Cos(inverseAngle), rel.y, r * Mathf.Sin(inverseAngle));
            default: return slotPos;
        }
    }

    /// <summary>Call after setting movedObjectId's own Position — if it's
    /// part of a live symmetry group, recomputes and applies every other
    /// member's position. No-op (and safe to call unconditionally) if the
    /// object isn't linked.</summary>
    private void PropagateSymmetryMove(int movedObjectId, Vector3 newPos)
    {
        var group = FindSymmetryGroup(movedObjectId);
        if (group == null) return;
        var movedMember = group.Members.FirstOrDefault(m => m.ObjectId == movedObjectId);
        if (movedMember == null) return;

        var canonical = InferCanonicalFromSlot(newPos, movedMember.SlotIndex);
        var slots = Data.GetSymmetricPositions(canonical);
        foreach (var member in group.Members)
        {
            if (member.ObjectId == movedObjectId) continue;
            if (member.SlotIndex < 0 || member.SlotIndex >= slots.Count) continue;
            SetObjectPositionRaw(member.ObjectId, slots[member.SlotIndex]);
        }
    }

    private void SetObjectPositionRaw(int objectId, Vector3 pos)
    {
        var marker = Data.ObjMarkers.FirstOrDefault(m => m.Id == objectId);
        if (marker != null) { marker.Position = SnapXZ(pos); return; }
        var prop = Data.ObjProps.FirstOrDefault(p => p.Id == objectId);
        if (prop != null) { prop.Position = pos; return; } // Props stay freeform, never snapped
        var unit = Data.ObjUnits.FirstOrDefault(u => u.Id == objectId);
        if (unit != null) unit.Position = SnapXZ(pos);
    }

    /// <summary>Single entry point for "this marker's position changed" —
    /// used by the Inspector's Position field, viewport drag, and Reproject,
    /// so symmetry propagation only has to be wired in one place.</summary>
    private void SetMarkerPosition(SC2ObjMarker m, Vector3 newPos)
    {
        var snapped = SnapXZ(newPos);
        if (!CanMoveMarkerWithoutDuplicates(m, snapped))
        {
            _lastActionSummary = "Marker not moved: the move or its symmetry slots would create a duplicate.";
            return;
        }
        m.Position = snapped;
        PropagateSymmetryMove(m.Id, m.Position);
    }

    private bool CanMoveMarkerWithoutDuplicates(SC2ObjMarker moved, Vector3 newPosition)
    {
        var group = FindSymmetryGroup(moved.Id);
        if (group == null)
        {
            var probe = new SC2ObjMarker { Name = moved.Name, Type = moved.Type, Prop = moved.Prop, Position = newPosition };
            return !HasEquivalentPlacedMarker(probe, moved.Id);
        }

        var movedMember = group.Members.FirstOrDefault(member => member.ObjectId == moved.Id);
        if (movedMember == null) return true;
        var canonical = InferCanonicalFromSlot(newPosition, movedMember.SlotIndex);
        var slots = Data.GetSymmetricPositions(canonical);
        var groupIds = new HashSet<int>(group.Members.Select(member => member.ObjectId));
        var candidates = new List<SC2ObjMarker>();

        foreach (var member in group.Members)
        {
            if (member.SlotIndex < 0 || member.SlotIndex >= slots.Count) continue;
            var source = Data.ObjMarkers.FirstOrDefault(marker => marker.Id == member.ObjectId);
            if (source == null) continue;
            var candidate = new SC2ObjMarker
            {
                Name = source.Name,
                Type = source.Type,
                Prop = source.Prop,
                Position = SnapXZ(slots[member.SlotIndex])
            };
            if (candidates.Any(existing => SC2MarkerDeduplication.SamePlacement(existing, candidate))) return false;
            if (Data.ObjMarkers.Any(existing => !groupIds.Contains(existing.Id) &&
                SC2MarkerDeduplication.SamePlacement(existing, candidate))) return false;
            candidates.Add(candidate);
        }
        return true;
    }

    private void SetPropPosition(SC2ObjProp p, Vector3 newPos)
    {
        p.Position = newPos;
        PropagateSymmetryMove(p.Id, newPos);
    }

    /// <summary>Units were previously excluded from the live
    /// symmetry system entirely (viewport drag set u.Position directly,
    /// never through a symmetry-aware setter) — this is the Unit equivalent
    /// of SetMarkerPosition/SetPropPosition above, so dragging or auto-
    /// placing a symmetry-linked Unit now propagates to its siblings the
    /// same way Markers/Props already do.</summary>
    private void SetUnitPosition(SC2ObjUnitNode u, Vector3 newPos)
    {
        u.Position = SnapXZ(newPos);
        PropagateSymmetryMove(u.Id, u.Position);
    }

    /// <summary>Disambiguates a generated marker name (e.g. "Foo_sym1")
    /// against every existing SC2ObjMarker.Name, appending a numeric suffix
    /// until unique — same convention as SC2TerrainExporter.UniqueFileName
    /// for texture name collisions. Needed because chain.MarkerNames looks
    /// markers up BY NAME, and DrawChainOverlays builds a
    /// name-&gt;marker Dictionary every viewport repaint; two markers sharing
    /// a name threw ArgumentException ("same key already added") on every
    /// single frame once created (confirmed via a live build's log) —
    /// e.g. running "Link Symmetry" or "Duplicate with
    /// Symmetry" twice on the same source marker, both of which previously
    /// generated the exact same "_sym&lt;slot&gt;" name with no uniqueness
    /// check at all.</summary>
    private string MakeUniqueMarkerName(string desired)
    {
        if (Data.ObjMarkers.All(m => !string.Equals(m.Name, desired, StringComparison.OrdinalIgnoreCase)))
            return desired;
        var suffix = 2;
        string candidate;
        do { candidate = $"{desired}_{suffix}"; suffix++; }
        while (Data.ObjMarkers.Any(m => string.Equals(m.Name, candidate, StringComparison.OrdinalIgnoreCase)));
        return candidate;
    }

    /// <summary>Same reasoning/collision-suffix convention as
    /// MakeUniqueMarkerName above, for SC2ObjUnitNode.Name — units don't sit
    /// in a scripting-format-constrained table the way ARMY_* markers do
    /// (see NextArmyName's own comment), but two units sharing a literal
    /// name is still real, avoidable bad data: LinkUnitsWithSymmetry's
    /// "_sym&lt;slot&gt;" suffix and PlaceUnitAt's counter-based "Unit_N" name
    /// were both previously generated with no uniqueness check at all —
    /// the same class of bug MakeUniqueMarkerName above guards against,
    /// just for Units.</summary>
    private string MakeUniqueUnitName(string desired)
    {
        if (Data.ObjUnits.All(u => !string.Equals(u.Name, desired, StringComparison.OrdinalIgnoreCase)))
            return desired;
        var suffix = 2;
        string candidate;
        do { candidate = $"{desired}_{suffix}"; suffix++; }
        while (Data.ObjUnits.Any(u => string.Equals(u.Name, candidate, StringComparison.OrdinalIgnoreCase)));
        return candidate;
    }

    /// <summary>Lowest-numbered unused "ARMY_N" — the ONLY name form any
    /// code path that generates a new army start-position marker should ever
    /// produce (never "ARMY_3_sym1"/"ARMY_3_2" — those broke the real
    /// vanilla ARMY_* convention scenario.lua/save.lua export relies on).
    /// Checks both ObjMarkers and ObjArmies so a gap left by a deleted army
    /// (e.g. ARMY_2 removed, leaving ARMY_1/ARMY_3) still gets reused rather
    /// than skipped, and so this stays correct when called repeatedly inside
    /// one symmetry loop (each new marker is added to ObjMarkers before the
    /// next call, so it's naturally seen as taken).</summary>
    private string NextArmyName()
    {
        var n = 1;
        while (Data.ObjMarkers.Any(m => string.Equals(m.Name, $"ARMY_{n}", StringComparison.OrdinalIgnoreCase))
            || Data.ObjArmies.Any(a => string.Equals(a.Name, $"ARMY_{n}", StringComparison.OrdinalIgnoreCase)))
            n++;
        return $"ARMY_{n}";
    }

    /// <summary>Only campaign maps are allowed a start-position marker whose
    /// name doesn't follow the plain "ARMY_N" convention (e.g. a
    /// story-specific name) — every skirmish/multiplayer map's ARMY_*
    /// markers are always plain-numbered in the real vanilla corpus. Type/
    /// CampaignId only ever come from an imported .scd's real scenario.lua
    /// (SC2ScenarioShell's own doc comment: from-scratch maps have no UI
    /// source for these yet) — a from-scratch map's Type is empty, so this
    /// correctly defaults to "not a campaign map" until that changes.</summary>
    private bool IsCampaignMap()
    {
        var fields = Session?.Document?.Scenario?.KnownFields;
        if (fields == null) return false;
        return (fields.Type?.IndexOf("campaign", StringComparison.OrdinalIgnoreCase) >= 0)
            || !string.IsNullOrEmpty(fields.CampaignId);
    }

    /// <summary>Links objectIds into one live symmetry group. If fewer
    /// members are passed than the current Symmetry mode has slots, the
    /// missing slots are created fresh (as a symmetric duplicate) rather
    /// than left unlinked — e.g. linking a single marker under XY symmetry
    /// creates its other 3 mirrors and links all 4.</summary>
    private void LinkMarkersWithSymmetry(List<SC2ObjMarker> selected)
    {
        if (selected.Count == 0) return;
        var canonical = selected[0];
        var isArmy = ArmyPositionRe.IsMatch(canonical.Name);
        var slots = GetUniqueSnappedMarkerSymmetryPositions(canonical.Position);
        if (slots.Count <= 1)
        {
            _lastActionSummary = "Symmetry produced no distinct marker position; no duplicate was created.";
            return;
        }
        var group = new SC2ObjSymmetryGroup { Id = Data.AllocateObjectId(), Kind = "Marker" };
        group.Members.Add(new SC2ObjSymmetryMember { ObjectId = canonical.Id, SlotIndex = 0 });

        for (var slot = 1; slot < slots.Count; slot++)
        {
            SC2ObjMarker member = slot - 1 < selected.Count - 1 ? selected[slot] : null;
            if (member == null)
            {
                var probe = new SC2ObjMarker { Type = canonical.Type, Prop = canonical.Prop, Position = slots[slot] };
                member = Data.ObjMarkers.FirstOrDefault(marker => marker.Id != canonical.Id &&
                    SC2MarkerDeduplication.SamePlacement(marker, probe));
            }
            if (member == null)
            {
                member = new SC2ObjMarker
                {
                    // Army start positions always get a clean "ARMY_N" name,
                    // never "<name>_sym<slot>" — see NextArmyName's own comment.
                    Id = Data.AllocateObjectId(), Name = isArmy ? NextArmyName() : MakeUniqueMarkerName(canonical.Name + "_sym" + slot), Type = canonical.Type,
                    Position = slots[slot], OrientationEuler = canonical.OrientationEuler,
                    OrientationIsQuaternion = canonical.OrientationIsQuaternion, ColorHex = canonical.ColorHex,
                    Prop = canonical.Prop, ExtraFields = new List<SC2LuaField>(canonical.ExtraFields),
                    AnchorToTerrain = canonical.AnchorToTerrain, HeightOffset = canonical.HeightOffset
                };
                Data.ObjMarkers.Add(member);
                if (isArmy) EnsureArmyMetadataLinked(member);
            }
            else
            {
                member.Position = slots[slot];
            }
            group.Members.Add(new SC2ObjSymmetryMember { ObjectId = member.Id, SlotIndex = slot });
        }
        Data.ObjSymmetryGroups.Add(group);
        _lastActionSummary = $"Linked {group.Members.Count} marker(s) with live symmetry.";
        MarkObjectsDirty();
    }

    /// <param name="onMemberCreated">Invoked for each brand-new mirrored
    /// prop this call adds (not for a reused/already-selected one) — lets
    /// callers that need a live visual (e.g. the brush's ShowBrushPropPreview)
    /// hook in without this method needing to know about scene preview at
    /// all.</param>
    private void LinkPropsWithSymmetry(List<SC2ObjProp> selected, Action<SC2ObjProp> onMemberCreated = null)
    {
        if (selected.Count == 0) return;
        var canonical = selected[0];
        var slots = Data.GetSymmetricPositions(canonical.Position);
        if (slots.Count <= 1)
        {
            _lastActionSummary = "Symmetry produced no distinct prop position; no duplicate was created.";
            return;
        }
        var group = new SC2ObjSymmetryGroup { Id = Data.AllocateObjectId(), Kind = "Prop" };
        group.Members.Add(new SC2ObjSymmetryMember { ObjectId = canonical.Id, SlotIndex = 0 });

        for (var slot = 1; slot < slots.Count; slot++)
        {
            SC2ObjProp member = slot - 1 < selected.Count - 1 ? selected[slot] : null;
            if (member == null)
            {
                member = new SC2ObjProp
                {
                    Id = Data.AllocateObjectId(), BlueprintPath = canonical.BlueprintPath, Position = slots[slot],
                    Rotation = canonical.Rotation, Scale = canonical.Scale, Flags = canonical.Flags,
                    AnchorToTerrain = canonical.AnchorToTerrain, HeightOffset = canonical.HeightOffset
                };
                Data.ObjProps.Add(member);
                onMemberCreated?.Invoke(member);
            }
            else
            {
                member.Position = slots[slot];
            }
            group.Members.Add(new SC2ObjSymmetryMember { ObjectId = member.Id, SlotIndex = slot });
        }
        Data.ObjSymmetryGroups.Add(group);
        _lastActionSummary = $"Linked {group.Members.Count} prop(s) with live symmetry.";
        MarkObjectsDirty();
    }

    /// <summary>Units were previously excluded from
    /// SC2ObjSymmetryGroup entirely (Kind was only ever "Marker"/"Prop") -
    /// same pattern as LinkMarkersWithSymmetry/LinkPropsWithSymmetry above,
    /// creating new root-level leaf units (ParentIndex=-1, IsGroup=false)
    /// for any symmetry slot not already covered by the selection. Existing
    /// unit trees (a selected unit that's part of a group hierarchy) are
    /// intentionally not deep-cloned here - this is scoped to the click-to-
    /// place case (a single new leaf unit), not general tree duplication.</summary>
    private void LinkUnitsWithSymmetry(List<SC2ObjUnitNode> selected)
    {
        if (selected.Count == 0) return;
        var canonical = selected[0];
        var slots = Data.GetSymmetricPositions(canonical.Position);
        var group = new SC2ObjSymmetryGroup { Id = Data.AllocateObjectId(), Kind = "Unit" };
        group.Members.Add(new SC2ObjSymmetryMember { ObjectId = canonical.Id, SlotIndex = 0 });

        for (var slot = 1; slot < slots.Count; slot++)
        {
            SC2ObjUnitNode member = slot - 1 < selected.Count - 1 ? selected[slot] : null;
            if (member == null)
            {
                member = new SC2ObjUnitNode
                {
                    Id = Data.AllocateObjectId(), ArmyName = canonical.ArmyName,
                    // Mirrors the canonical unit's own parent — an army is
                    // not itself a group, so a symmetric duplicate must land
                    // in the same group as the unit it duplicates, never
                    // bare at the army root.
                    Name = MakeUniqueUnitName(canonical.Name + "_sym" + slot), ParentIndex = canonical.ParentIndex, IsGroup = false,
                    Type = canonical.Type, Position = SnapXZ(slots[slot]), OrientationEuler = canonical.OrientationEuler,
                    AnchorToTerrain = canonical.AnchorToTerrain, HeightOffset = canonical.HeightOffset
                };
                Data.ObjUnits.Add(member);
            }
            else
            {
                member.Position = SnapXZ(slots[slot]);
            }
            group.Members.Add(new SC2ObjSymmetryMember { ObjectId = member.Id, SlotIndex = slot });
        }
        Data.ObjSymmetryGroups.Add(group);
        _lastActionSummary = $"Linked {group.Members.Count} unit(s) with live symmetry.";
        MarkObjectsDirty();
    }

    private void UnlinkSymmetryGroup(int objectId)
    {
        var group = FindSymmetryGroup(objectId);
        if (group == null) return;
        Data.ObjSymmetryGroups.Remove(group);
        _lastActionSummary = "Symmetry link broken (objects are now independent).";
        MarkObjectsDirty();
    }

    /// <summary>Shared "Attach to terrain / Height offset / Absolute Y /
    /// Reproject" block — the same model for
    /// Props/Markers/Units instead of each having its own bespoke button.</summary>
    /// <summary>reproject/setAbsoluteY are the ONLY two ways this block
    /// mutates the caller's position — it never takes position by ref
    /// itself, precisely so a caller can't accidentally re-apply a stale
    /// snapshot captured before one of these callbacks already wrote the
    /// real, current value (that used to happen here: the old ref-Vector3
    /// version had the caller do `position = ...; DrawAnchorBlock(ref
    /// position, ...); p.Position = position;`, which silently undid
    /// "Reproject now" by overwriting it with the pre-click value).</summary>
    private void DrawAnchorBlock(ref bool anchor, ref float offset, float currentY, Action reproject, Action<float> setAbsoluteY)
    {
        SC2EnvFieldGui.SectionLabel("Height anchoring");
        anchor = SC2EnvFieldGui.Toggle("Attach to terrain", anchor);
        if (anchor)
        {
            offset = SC2EnvFieldGui.FloatField("Height offset", offset, "anchor_offset");
            if (SC2EnvFieldGui.Button("Reproject now", GUILayout.Height(SC2UiScale.Pxf(18f)))) reproject();
        }
        else
        {
            var y = SC2EnvFieldGui.FloatField("Absolute Y", currentY, "anchor_absy");
            if (!Mathf.Approximately(y, currentY)) setAbsoluteY(y);
        }
    }

    private void DrawSystemFilterRow(float w, ref SysFilter filter)
    {
        GUILayout.BeginHorizontal();
        DrawFilterButton(w, "All", SysFilter.All, ref filter);
        DrawFilterButton(w, "Selected", SysFilter.Selected, ref filter);
        DrawFilterButton(w, "Invalid", SysFilter.Invalid, ref filter);
        DrawFilterButton(w, "Unanchored", SysFilter.Unanchored, ref filter);
        GUILayout.EndHorizontal();
    }

    private void DrawFilterButton(float w, string label, SysFilter value, ref SysFilter current)
    {
        var isActive = current == value;
        if (SC2EnvFieldGui.Button((isActive ? "● " : "") + label, GUILayout.Width(w / 4f - 2f), GUILayout.Height(SC2UiScale.Pxf(18f))))
            current = value;
    }

    /// <summary>Generic batch-action bar shown once 1+ items are selected —
    /// each category supplies only the (label, action) pairs that make
    /// sense for it (e.g. "Assign army" only for Units).</summary>
    private void DrawBatchBar(float w, HashSet<int> selection, List<(string label, Action act)> actions)
    {
        if (selection.Count == 0) return;
        SC2EnvFieldGui.SectionLabel($"Batch actions ({selection.Count} selected)");
        var col = 0;
        GUILayout.BeginHorizontal();
        foreach (var (label, act) in actions)
        {
            if (SC2EnvFieldGui.Button(label, GUILayout.Height(SC2UiScale.Pxf(20f)), GUILayout.Width(w / 2f - 6f)))
                act();
            col++;
            if (col % 2 == 0) { GUILayout.EndHorizontal(); GUILayout.BeginHorizontal(); }
        }
        GUILayout.EndHorizontal();
        if (SC2EnvFieldGui.Button("Clear selection", GUILayout.Height(SC2UiScale.Pxf(18f))))
            selection.Clear();
        GUILayout.Space(4);
    }

    /// <summary>Import + Validation used to live inside a dedicated
    /// "Overview" tab, since removed — Objects no longer needs a
    /// separate landing tab. Both stay real, reachable functionality: drawn
    /// unconditionally above the per-domain tab body, not gated behind any
    /// one specific subTab, since both apply across every domain (Armies,
    /// Units, Props, Markers, Paths, Areas, Chains alike).</summary>
    private void DrawImportAndValidation(float w)
    {
        var problems = Validate();
        GUILayout.BeginHorizontal();
        SC2EnvFieldGui.SectionLabel(problems.Count == 0 ? "Validation: no issues found" : $"Validation: {problems.Count} issue(s)");
        GUILayout.EndHorizontal();
        if (problems.Count > 0)
        {
            _validationExpanded = GUILayout.Toggle(_validationExpanded, _validationExpanded ? "Hide details" : "Show details", "Button", GUILayout.Width(SC2UiScale.Pxf(120f)));
            if (_validationExpanded)
                foreach (var p in problems) SC2EnvFieldGui.WrapLabel("• " + p, w);
        }

        GUILayout.Space(6);
        if (SC2EnvFieldGui.Button("Import Objects from terrain.bdf...", GUILayout.Height(SC2UiScale.Pxf(24f)), GUILayout.Width(w)))
            ImportObjectsFromFile();
        if (!string.IsNullOrEmpty(_lastImportSummary))
            SC2EnvFieldGui.WrapLabel(_lastImportSummary, w);
        GUILayout.Space(6);
    }

    private bool _validationExpanded;

    private int CountDisconnectedGraphIslands()
    {
        var extra = 0;
        foreach (var g in Data.ObjGraphs)
        {
            var nodes = new HashSet<int>();
            foreach (var (a, b) in SC2ObjectGraphRefs.Enumerate(g)) { nodes.Add(a); nodes.Add(b); }
            if (nodes.Count == 0) continue;

            var parent = nodes.ToDictionary(n => n, n => n);
            int Find(int x) { while (parent[x] != x) x = parent[x]; return x; }

            foreach (var (a, b) in SC2ObjectGraphRefs.Enumerate(g))
            {
                if (!nodes.Contains(a) || !nodes.Contains(b)) continue;
                var ra = Find(a); var rb = Find(b);
                if (ra != rb) parent[ra] = rb;
            }
            var roots = nodes.Select(Find).Distinct().Count();
            extra += Mathf.Max(0, roots - 1);
        }
        return extra;
    }

    private void ImportObjectsFromFile()
    {
        string path;
#if UNITY_EDITOR
        path = UnityEditor.EditorUtility.OpenFilePanel("Select terrain.bdf (Objects import)", "", "bdf");
#else
        path = SC2RuntimeFileDialog.OpenFile("Select terrain.bdf (Objects import)", "Terrain BDF (*.bdf)", "bdf");
#endif
        if (string.IsNullOrEmpty(path))
        {
            _lastImportSummary = "Import cancelled.";
            return;
        }
        ImportObjectsFromPath(path);
        RepaintHost?.Invoke();
    }

    /// <summary>Public so SC2GameViewEditorUI.OpenTerrainBdfImport can chain
    /// this on right after importing the same terrain.bdf as a mesh — per
    /// user report, importing a map's terrain.bdf alone left Armies (and
    /// every other _save.lua-only domain: Units/Chains/Areas) empty, since
    /// that action never used to call this Objects-side import at all; the
    /// user had to separately remember to come to the Objects tab and click
    /// "Import Objects from terrain.bdf..." with the SAME file a second
    /// time. Returns the import summary so the caller can fold it into its
    /// own status line instead of only this shell's _lastImportSummary.</summary>
    public string ImportObjectsFromPath(string path)
    {
        PushUndo();
        var result = SC2ObjectsImport.ImportFromTerrainBdfPath(Data, path);

        // Imported Props/Markers/Units keep whatever Y was baked into
        // mapobjs.bdf/_save.lua at the source map's own export time -
        // AnchorToTerrain defaults to true (SC2ObjProp's own field
        // default), but nothing actually resamples against the terrain
        // that's loaded right now until the object is next moved/
        // reprojected by hand. If the live or imported terrain.bdf differs
        // at all from whatever the source map was authored against, every
        // anchored object - buildings (Props) especially - lands at the
        // wrong height (floating or clipping into the ground). Reproject
        // everything anchored once, right after import, against whatever
        // terrain is actually loaded right now.
        foreach (var p in Data.ObjProps) if (p.AnchorToTerrain) ReprojectProp(p);
        foreach (var m in Data.ObjMarkers) if (m.AnchorToTerrain) ReprojectMarker(m);
        foreach (var u in Data.ObjUnits) if (u.AnchorToTerrain && !u.IsGroup) ReprojectUnit(u);

        _lastImportSummary = result.Summary + (result.Warnings.Count > 0 ? "\n" + string.Join("\n", result.Warnings) : "");
        ClearAllSelections();
        return _lastImportSummary;
    }

    private List<string> Validate()
    {
        var issues = new List<string>();
        var markerNames = new HashSet<string>(Data.ObjMarkers.Select(m => m.Name));
        var armyNames = new HashSet<string>(Data.ObjArmies.Select(a => a.Name));

        // Every real vanilla map (D1 multiplayer + CA campaign corpus,
        // confirmed 2..8 start positions across every sample) has at least
        // 2 armies — a map with 0 or 1 has no opponent to play against.
        var startPositionCount = Data.ObjMarkers.Count(m => ArmyPositionRe.IsMatch(m.Name));
        if (startPositionCount < 2)
            issues.Add($"Map has only {startPositionCount} start position(s) (ARMY_* marker) — at least 2 are required.");

        // A non-standard start-position name ("ARMY_Custom" rather than
        // "ARMY_3") only ever occurs in the real vanilla corpus on campaign
        // maps — every generated name is already the clean form (see
        // NextArmyName), so surviving one here means the user manually
        // renamed a marker (SC2ObjMarker Name field in the Markers tab).
        if (!IsCampaignMap())
        {
            var nonStandard = Data.ObjMarkers.Where(m => ArmyPositionRe.IsMatch(m.Name) && !ArmyCleanNameRe.IsMatch(m.Name)).ToList();
            foreach (var m in nonStandard)
                issues.Add($"Start position '{m.Name}' isn't the plain \"ARMY_N\" form — that's only valid on a campaign map (Scenario tab > Type).");
        }

        foreach (var g in Data.ObjGraphs)
            foreach (var (a, b) in SC2ObjectGraphRefs.Enumerate(g))
            {
                if (!SC2ObjectGraphRefs.TryGetMarker(Data, a, out _) || !SC2ObjectGraphRefs.TryGetMarker(Data, b, out _))
                    issues.Add($"Graph '{g.Name}' has a missing-marker edge ({a},{b}).");
            }

        foreach (var chain in Data.ObjChains)
            foreach (var m in chain.MarkerNames)
                if (!markerNames.Contains(m))
                    issues.Add($"Chain '{chain.Name}' references missing marker '{m}'.");

        foreach (var army in Data.ObjArmies)
            foreach (var al in army.Alliances)
                if (!armyNames.Contains(al.OtherArmy))
                    issues.Add($"Army '{army.Name}' alliance references missing army '{al.OtherArmy}'.");

        foreach (var u in Data.ObjUnits)
            if (!armyNames.Contains(u.ArmyName))
                issues.Add($"Unit '{u.Name}' belongs to unknown army '{u.ArmyName}'.");

        return issues;
    }

    // ── 1. Armies ────────────────────────────────────────────────────────
    private void DrawArmies(float w)
    {
        GUILayout.BeginHorizontal();
        var armyIcon = LoadMarkerIcon("EditorIcons/Markers/" + SC2MarkerTemplates.ArmyIconKey);
        if (armyIcon != null) GUILayout.Label(armyIcon, GUILayout.Width(SC2UiScale.Pxf(24f)), GUILayout.Height(SC2UiScale.Pxf(24f)));
        SC2EnvFieldGui.SectionLabel("Start Positions (ARMY_* markers)");
        GUILayout.EndHorizontal();
        var starts = Data.ObjMarkers.Where(m => ArmyPositionRe.IsMatch(m.Name)).ToList();

        // Desync detection: a start-position marker whose name isn't the
        // plain "ARMY_N" form real skirmish/multiplayer maps use (e.g. a
        // leftover "ARMY_1_sym1" — NextArmyName's own doc comment explains
        // that pattern used to leak out of "Duplicate with symmetry" before
        // it was fixed to always assign a clean ARMY_N name to army
        // markers) still counts as a start position for export purposes
        // (matches ArmyPositionRe), but has no real corresponding entry
        // under that exact name in the Armies list below — this is the
        // concrete, visible shape of "armies aren't synced" reports.
        // Surfaced here (not only in Validate(), which only runs on
        // export/diagnostics) with a one-click fix, since renaming is safe
        // — Chains reference markers by name and get updated automatically
        // (RenameMarker), Graphs reference by id so are unaffected.
        if (!IsCampaignMap())
        {
            foreach (var bad in starts.Where(m => !ArmyCleanNameRe.IsMatch(m.Name)).ToList())
            {
                SC2TerrainShell.DrawUnifiedNote($"⚠ '{bad.Name}' isn't a plain \"ARMY_N\" start position — this is why it doesn't line up with the Armies list below.");
                if (SC2EnvFieldGui.Button($"Fix name ('{bad.Name}' → next free ARMY_N)", GUILayout.Height(SC2UiScale.Pxf(20f))))
                {
                    PushUndo();
                    RenameMarker(bad, NextArmyName());
                    EnsureArmyMetadataLinked(bad);
                }
            }
        }

        foreach (var m in starts)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(TruncateForLabel(m.Name, w * 0.4f), GUILayout.Width(w * 0.4f));
            m.Position = SnapXZ(SC2EnvFieldGui.Vector3Field("", m.Position, "objarmy_pos_" + m.Name));
            GUILayout.EndHorizontal();
        }
        // "Add Start Position"/"Add Army" (instant blank create) and "Duplicate
        // with symmetry" (per-marker, used to sit in the loop above) are all
        // gone — "Place in viewport..." below is the only way left to create
        // an Army/marker pair. Every prior desync bug on this tab (duplicate
        // names, orphaned markers, "_sym1" naming) traced back to having
        // several independent, uncoordinated creation paths. This one stays
        // manual/opt-in, not armed by default — auto-arming it on tab-entry
        // (tried earlier) placed unwanted armies from ordinary viewport
        // clicks.
        var placingArmy = _placementKind == PlacementKind.Army;
        if (SC2EnvFieldGui.Button(placingArmy ? "Stop placing" : "Place in viewport...", GUILayout.Height(SC2UiScale.Pxf(24f))))
        {
            if (placingArmy) DisarmPlacement();
            else ArmPlacement(PlacementKind.Army, "");
        }

        GUILayout.Space(10);
        SC2EnvFieldGui.SectionLabel("Armies");
        _searchArmies = SC2EnvFieldGui.TextField("Search", _searchArmies, "armies_search");
        // Duplicate-named armies are always bad data — _save.lua's Armies
        // table is a real Lua table keyed by name, so two entries can never
        // both be genuine (EnsureArmyMetadataLinked now also guards against
        // creating new ones this way; this catches any that already exist).
        foreach (var dupName in Data.ObjArmies.GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                     .Where(g => g.Count() > 1).Select(g => g.Key).ToList())
        {
            SC2TerrainShell.DrawUnifiedNote($"⚠ Army '{dupName}' appears more than once — only one can be real.");
            if (SC2EnvFieldGui.Button($"Remove duplicate(s) of '{dupName}'", GUILayout.Height(SC2UiScale.Pxf(20f))))
            {
                PushUndo();
                var dupes = Data.ObjArmies.Where(a => string.Equals(a.Name, dupName, StringComparison.OrdinalIgnoreCase)).ToList();
                for (var i = 1; i < dupes.Count; i++) Data.ObjArmies.Remove(dupes[i]);
                MarkObjectsDirty();
            }
        }

        var armies = Data.ObjArmies.Where(a => MatchSearch(a.Name, _searchArmies)).ToList();
        foreach (var a in armies)
        {
            GUILayout.BeginHorizontal();
            var armyLabel = string.IsNullOrEmpty(a.Name) ? $"Army {a.Id}" : a.Name;
            var sel = GUILayout.Toggle(_selectedArmyId == a.Id, TruncateForLabel(armyLabel, w * 0.7f), GUILayout.Width(w * 0.7f));
            if (sel) _selectedArmyId = a.Id; else if (_selectedArmyId == a.Id) _selectedArmyId = 0;
            if (SC2EnvFieldGui.Button("X", GUILayout.Width(SC2UiScale.Pxf(24f))))
            {
                PushUndo();
                RemoveArmyAndLinkedMarker(a);
                _selectedArmyId = 0;
                GUILayout.EndHorizontal();
                break;
            }
            GUILayout.EndHorizontal();
        }

        var selArmy = Data.ObjArmies.FirstOrDefault(x => x.Id == _selectedArmyId);
        if (selArmy != null)
        {
            GUILayout.Space(6);
            var newArmyName = SC2EnvFieldGui.TextField("Name", selArmy.Name, "army_name");
            if (newArmyName != selArmy.Name)
            {
                if (Session?.Document != null)
                {
                    Session.History.Execute(Session.Document, new SC2SetArmyNameCommand(selArmy.Id, newArmyName));
                    _undo.Record(new SC2DelegateUndoChannel(TryCommandUndo, TryCommandRedo));
                }
                selArmy.Name = newArmyName;
            }
            var prevPers = selArmy.Personality; var prevPlans = selArmy.Plans;
            var prevFaction = selArmy.Faction; var prevMass = selArmy.Mass; var prevEnergy = selArmy.Energy;
            selArmy.Personality = SC2EnvFieldGui.TextField("Personality", selArmy.Personality, "army_pers");
            selArmy.Plans = SC2EnvFieldGui.TextField("Plans", selArmy.Plans, "army_plans");
            selArmy.Faction = SC2EnvFieldGui.IntField("Faction", selArmy.Faction, "army_faction");
            selArmy.Mass = SC2EnvFieldGui.FloatField("Mass", selArmy.Mass, "army_mass");
            selArmy.Energy = SC2EnvFieldGui.FloatField("Energy", selArmy.Energy, "army_energy");
            if (selArmy.Personality != prevPers || selArmy.Plans != prevPlans || selArmy.Faction != prevFaction ||
                selArmy.Mass != prevMass || selArmy.Energy != prevEnergy) MarkObjectsDirty();

            SC2EnvFieldGui.SectionLabel("Alliances");
            for (var i = 0; i < selArmy.Alliances.Count; i++)
            {
                var al = selArmy.Alliances[i];
                GUILayout.BeginHorizontal();
                var prevOther = al.OtherArmy; var prevStance = al.Stance;
                al.OtherArmy = SC2EnvFieldGui.TextField("With", al.OtherArmy, "ally_other_" + i);
                al.Stance = SC2EnvFieldGui.TextField("Stance", al.Stance, "ally_stance_" + i);
                if (al.OtherArmy != prevOther || al.Stance != prevStance) MarkObjectsDirty();
                if (SC2EnvFieldGui.Button("X", GUILayout.Width(SC2UiScale.Pxf(24f)))) { selArmy.Alliances.RemoveAt(i); MarkObjectsDirty(); GUILayout.EndHorizontal(); break; }
                GUILayout.EndHorizontal();
            }
            if (SC2EnvFieldGui.Button("Add Alliance", GUILayout.Height(SC2UiScale.Pxf(20f))))
            {
                selArmy.Alliances.Add(new SC2ObjAlliance { OtherArmy = "", Stance = "ENEMY" });
                MarkObjectsDirty();
            }
        }
    }

    private void DuplicateMarkerWithSymmetry(SC2ObjMarker src)
    {
        var isArmy = ArmyPositionRe.IsMatch(src.Name);
        var copies = GetUniqueSnappedMarkerSymmetryPositions(src.Position);
        for (var i = 1; i < copies.Count; i++)
        {
            var clone = new SC2ObjMarker
            {
                Id = Data.AllocateObjectId(),
                // Army start positions always get a clean "ARMY_N" name,
                // never "<name>_sym<i>" — see NextArmyName's own comment.
                Name = isArmy ? NextArmyName() : MakeUniqueMarkerName(src.Name + "_sym" + i),
                Type = src.Type,
                Position = copies[i],
                OrientationEuler = src.OrientationEuler,
                OrientationIsQuaternion = src.OrientationIsQuaternion,
                ColorHex = src.ColorHex,
                Prop = src.Prop,
                ExtraFields = new List<SC2LuaField>(src.ExtraFields),
                AnchorToTerrain = src.AnchorToTerrain,
                HeightOffset = src.HeightOffset
            };
            if (HasEquivalentPlacedMarker(clone, src.Id)) continue;
            Data.ObjMarkers.Add(clone);
            if (isArmy) EnsureArmyMetadataLinked(clone);
            MarkObjectsDirty();
        }
    }

    private void DuplicatePropWithSymmetry(SC2ObjProp src)
    {
        var copies = Data.GetSymmetricPositions(src.Position);
        for (var i = 1; i < copies.Count; i++)
        {
            Data.ObjProps.Add(new SC2ObjProp
            {
                Id = Data.AllocateObjectId(),
                BlueprintPath = src.BlueprintPath,
                Position = copies[i],
                Rotation = src.Rotation,
                Scale = src.Scale,
                Flags = src.Flags,
                AnchorToTerrain = src.AnchorToTerrain,
                HeightOffset = src.HeightOffset
            });
            MarkObjectsDirty();
        }
    }

    /// <summary>Ctrl+C — snapshots the current Prop/Marker/Unit selection as
    /// plain field-value copies (never live references) into the clipboard
    /// fields, replacing whatever was copied before. Units that are groups
    /// are skipped — see the clipboard fields' own doc comment for why.</summary>
    private void CopySelectionToClipboard()
    {
        _clipboardProps.Clear();
        _clipboardMarkers.Clear();
        _clipboardUnits.Clear();
        _clipboardPasteCount = 0;

        foreach (var p in Data.ObjProps.Where(x => _selProps.Contains(x.Id)))
            _clipboardProps.Add(new SC2ObjProp
            {
                BlueprintPath = p.BlueprintPath,
                Position = p.Position,
                Rotation = p.Rotation,
                Scale = p.Scale,
                Flags = p.Flags,
                AnchorToTerrain = p.AnchorToTerrain,
                HeightOffset = p.HeightOffset
            });
        foreach (var m in Data.ObjMarkers.Where(x => _selMarkers.Contains(x.Id)))
            _clipboardMarkers.Add(new SC2ObjMarker
            {
                Name = m.Name,
                Type = m.Type,
                Position = m.Position,
                OrientationEuler = m.OrientationEuler,
                OrientationIsQuaternion = m.OrientationIsQuaternion,
                ColorHex = m.ColorHex,
                Prop = m.Prop,
                ExtraFields = new List<SC2LuaField>(m.ExtraFields),
                AnchorToTerrain = m.AnchorToTerrain,
                HeightOffset = m.HeightOffset
            });
        foreach (var u in Data.ObjUnits.Where(x => _selUnits.Contains(x.Id) && !x.IsGroup))
            _clipboardUnits.Add(new SC2ObjUnitNode
            {
                ArmyName = u.ArmyName,
                Name = u.Name,
                ParentIndex = u.ParentIndex,
                IsGroup = false,
                Type = u.Type,
                Position = u.Position,
                OrientationEuler = u.OrientationEuler,
                AnchorToTerrain = u.AnchorToTerrain,
                HeightOffset = u.HeightOffset
            });

        var total = _clipboardProps.Count + _clipboardMarkers.Count + _clipboardUnits.Count;
        _lastActionSummary = total > 0 ? $"Copied {total} object(s) to clipboard." : "Nothing selected to copy.";
        ReportStatus?.Invoke(_lastActionSummary);
    }

    /// <summary>Ctrl+V — pastes the clipboard as brand-new objects (fresh
    /// Ids, unique names) offset from where they were copied, and selects
    /// the pasted set. Each repeated paste offsets further from the
    /// originals (via _clipboardPasteCount) so pasting several times in a
    /// row doesn't stack copies exactly on top of each other or of the
    /// previous paste.</summary>
    private void PasteClipboard()
    {
        var total = _clipboardProps.Count + _clipboardMarkers.Count + _clipboardUnits.Count;
        if (total == 0)
        {
            _lastActionSummary = "Clipboard is empty — copy something first (Ctrl+C).";
            ReportStatus?.Invoke(_lastActionSummary);
            return;
        }

        PushUndo();
        _clipboardPasteCount++;
        var offset = new Vector3(4f * _clipboardPasteCount, 0f, 4f * _clipboardPasteCount);

        _selProps.Clear();
        _selMarkers.Clear();
        _selUnits.Clear();

        foreach (var src in _clipboardProps)
        {
            var pos = SnapXZ(src.Position + offset);
            var clone = new SC2ObjProp
            {
                Id = Data.AllocateObjectId(),
                BlueprintPath = src.BlueprintPath,
                Position = pos,
                Rotation = src.Rotation,
                Scale = src.Scale,
                Flags = src.Flags,
                AnchorToTerrain = src.AnchorToTerrain,
                HeightOffset = src.HeightOffset
            };
            if (clone.AnchorToTerrain) clone.Position = new Vector3(pos.x, SampleHeight(pos.x, pos.z) + clone.HeightOffset, pos.z);
            Data.ObjProps.Add(clone);
            // Ctrl+C/Ctrl+V must also create the
            // symmetric counterpart when symmetry is on. Per-object, not once for the whole pasted
            // batch — LinkPropsWithSymmetry(selected) with
            // selected.Count > 1 links the list's objects TO EACH OTHER
            // (the "Link selected (symmetry)" multi-select use case), which
            // would wrongly treat several independently-pasted props as
            // each other's symmetric slots.
            if (Data.Symmetry != SymmetryMode.None)
                LinkPropsWithSymmetry(new List<SC2ObjProp> { clone });
            _selProps.Add(clone.Id);
        }
        foreach (var src in _clipboardMarkers)
        {
            var isArmy = ArmyPositionRe.IsMatch(src.Name);
            var pos = SnapXZ(src.Position + offset);
            var clone = new SC2ObjMarker
            {
                Id = Data.AllocateObjectId(),
                // Army start positions always get a clean "ARMY_N" name,
                // never a copy of the original name — see NextArmyName's
                // own comment.
                Name = isArmy ? NextArmyName() : MakeUniqueMarkerName(src.Name),
                Type = src.Type,
                Position = pos,
                OrientationEuler = src.OrientationEuler,
                OrientationIsQuaternion = src.OrientationIsQuaternion,
                ColorHex = src.ColorHex,
                Prop = src.Prop,
                ExtraFields = new List<SC2LuaField>(src.ExtraFields),
                AnchorToTerrain = src.AnchorToTerrain,
                HeightOffset = src.HeightOffset
            };
            if (clone.AnchorToTerrain) clone.Position = new Vector3(pos.x, SampleHeight(pos.x, pos.z) + clone.HeightOffset, pos.z);
            if (HasEquivalentPlacedMarker(clone)) continue;
            Data.ObjMarkers.Add(clone);
            if (isArmy) EnsureArmyMetadataLinked(clone);
            // See the clipboardProps loop above for why this is per-object.
            // isArmy carries through automatically: LinkMarkersWithSymmetry
            // reads it from clone.Name (already a fresh NextArmyName() when
            // isArmy is true), so any newly-created symmetric sibling gets
            // its own EnsureArmyMetadataLinked call internally too.
            if (Data.Symmetry != SymmetryMode.None)
                LinkMarkersWithSymmetry(new List<SC2ObjMarker> { clone });
            _selMarkers.Add(clone.Id);
        }
        foreach (var src in _clipboardUnits)
        {
            var pos = SnapXZ(src.Position + offset);
            var clone = new SC2ObjUnitNode
            {
                Id = Data.AllocateObjectId(),
                ArmyName = src.ArmyName,
                Name = MakeUniqueUnitName(src.Name),
                ParentIndex = src.ParentIndex,
                IsGroup = false,
                Type = src.Type,
                Position = pos,
                OrientationEuler = src.OrientationEuler,
                AnchorToTerrain = src.AnchorToTerrain,
                HeightOffset = src.HeightOffset
            };
            if (clone.AnchorToTerrain) clone.Position = new Vector3(pos.x, SampleUnitHeight(pos.x, pos.z, clone.Type) + clone.HeightOffset, pos.z);
            Data.ObjUnits.Add(clone);
            // See the clipboardProps loop above for why this is per-object.
            if (Data.Symmetry != SymmetryMode.None)
                LinkUnitsWithSymmetry(new List<SC2ObjUnitNode> { clone });
            _selUnits.Add(clone.Id);
        }

        _lastActionSummary = $"Pasted {total} object(s).";
        ReportStatus?.Invoke(_lastActionSummary);
        MarkObjectsDirty();
    }

    // ── 2. Units ─────────────────────────────────────────────────────────
    /// <summary>Per-army collapsed state for the top-level "ARMY_1  0-0"
    /// rows below — keyed by army name (there's no stable numeric Id for an
    /// army-as-a-row the way there is for SC2ObjUnitNode; army NAMES are
    /// already the real identity Armies/Units are matched on throughout
    /// this file, e.g. SC2ObjArmy.Name / SC2ObjUnitNode.ArmyName).</summary>
    private readonly HashSet<string> _unitArmyCollapsed = new();

    private static readonly Color ArmyRowColor = new Color(0.93f, 0.62f, 0.25f);
    private static readonly Color GroupRowColor = new Color(0.55f, 0.78f, 0.98f);
    private static readonly Color DeleteButtonColor = new Color(0.55f, 0.16f, 0.16f);
    private static readonly Color AddGroupButtonColor = new Color(0.20f, 0.42f, 0.70f);
    // A muted, low-contrast tone for the recurring "+ Group" row — the
    // brighter AddGroupButtonColor stays reserved for the Groups/Units tab
    // switcher's active-tab highlight, where full contrast is the point;
    // a whole stack of "+ Group" rows in that same bright blue reads as a
    // wall of noise rather than a quiet, repeatable affordance.
    private static readonly Color AddGroupRowColor = new Color(0.15f, 0.19f, 0.26f);
    private static readonly Color TreeGuideLineColor = new Color(0.5f, 0.55f, 0.65f, 0.4f);

    private void DrawUnits(float w)
    {
        if (_selUnits.Count == 1)
        {
            var sel = Data.ObjUnits.FirstOrDefault(u => u.Id == _selUnits.First());
            if (sel != null) { DrawUnitInspector(w, sel); return; }
            _selUnits.Clear();
        }

        // Groups / Units — mirrors FAForeverMapEditor's own left-panel split:
        // "Groups" is a structural tree of group folders only (an army's
        // placed units are never listed inline there, only counted in each
        // folder's badge); "Units" is a flat, searchable list of every
        // placed unit. Kept as two genuinely separate views rather than one
        // combined tree.
        GUILayout.BeginHorizontal();
        var prevBg = GUI.backgroundColor;
        GUI.backgroundColor = _unitsGroupsTabActive ? AddGroupButtonColor : prevBg;
        if (GUILayout.Button("Groups", GUILayout.Height(SC2UiScale.Pxf(22f)), GUILayout.Width(w * 0.5f))) _unitsGroupsTabActive = true;
        GUI.backgroundColor = !_unitsGroupsTabActive ? AddGroupButtonColor : prevBg;
        if (GUILayout.Button("Units", GUILayout.Height(SC2UiScale.Pxf(22f)), GUILayout.Width(w * 0.5f))) _unitsGroupsTabActive = false;
        GUI.backgroundColor = prevBg;
        GUILayout.EndHorizontal();

        if (_hiddenUnitIds.Count > 0)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label($"{_hiddenUnitIds.Count} unit(s) hidden/isolated.");
            if (SC2EnvFieldGui.Button("Show All", GUILayout.Height(SC2UiScale.Pxf(18f)), GUILayout.Width(SC2UiScale.Pxf(90f)))) _hiddenUnitIds.Clear();
            GUILayout.EndHorizontal();
        }

        if (_unitsGroupsTabActive) DrawGroupsTab(w); else DrawUnitsTab(w);
    }

    private void DrawUnitsTab(float w)
    {
        SC2EnvFieldGui.SectionLabel($"Units ({Data.ObjUnits.Count(u => !u.IsGroup)})");

        // New units target whichever group is selected on the Groups tab
        // (_selGroupId persists across the tab switch); if none is
        // currently selected there, fall back to the last unit actually
        // placed/added this session so "Add unit"/"Place in viewport..."
        // stay usable without forcing a trip back to the Groups tab for
        // every single unit.
        var targetGroup = _selGroupId != 0 ? Data.ObjUnits.FirstOrDefault(u => u.Id == _selGroupId && u.IsGroup) : null;
        var lastUnit = _lastActiveUnitId != 0 ? Data.ObjUnits.FirstOrDefault(u => u.Id == _lastActiveUnitId && !u.IsGroup) : null;
        if (targetGroup == null && lastUnit != null && lastUnit.ParentIndex >= 0 && lastUnit.ParentIndex < Data.ObjUnits.Count)
            targetGroup = Data.ObjUnits[lastUnit.ParentIndex];

        if (lastUnit != null)
            SC2EnvFieldGui.WrapLabel($"Last unit: {lastUnit.Name}" + (targetGroup != null ? $"  [{targetGroup.ArmyName}/{targetGroup.Name}]" : ""), w);

        if (targetGroup == null)
        {
            SC2EnvFieldGui.WrapLabel("Select a group on the Groups tab to add units to it.", w);
        }
        else
        {
            SC2EnvFieldGui.WrapLabel($"Target group: {targetGroup.Name}  [{targetGroup.ArmyName}]", w);
            GUILayout.BeginHorizontal();
            if (SC2EnvFieldGui.Button("Add unit", GUILayout.Height(SC2UiScale.Pxf(22f)), GUILayout.Width(w * 0.48f)))
            {
                PushUndo();
                // No default blueprint — must not silently plant a real unit
                // (e.g. "uca0101"); the new leaf starts blank until the user
                // picks one explicitly.
                var parentIdx = Data.ObjUnits.IndexOf(targetGroup);
                var newUnit = new SC2ObjUnitNode { Id = Data.AllocateObjectId(), ArmyName = targetGroup.ArmyName, Name = MakeUniqueUnitName("New Unit"), ParentIndex = parentIdx, Type = "", Position = targetGroup.Position };
                Data.ObjUnits.Add(newUnit);
                _lastActiveUnitId = newUnit.Id;
                MarkObjectsDirty();
            }
            var placingThisGroup = _placementKind == PlacementKind.Unit && _placementTargetGroupId == targetGroup.Id;
            if (SC2EnvFieldGui.Button(placingThisGroup ? "Stop placing" : "Place in viewport...", GUILayout.Height(SC2UiScale.Pxf(22f)), GUILayout.Width(w * 0.48f)))
            {
                if (placingThisGroup) DisarmPlacement();
                else
                {
                    ArmPlacement(PlacementKind.Unit, targetGroup.ArmyName);
                    _placementTargetGroupId = targetGroup.Id; // must be set AFTER ArmPlacement - it resets this field
                }
            }
            GUILayout.EndHorizontal();
        }

        if (SC2EnvFieldGui.Button("Browse unit blueprints", GUILayout.Height(SC2UiScale.Pxf(24f)))) OpenUnitBrowser(null);
        _searchUnits = SC2EnvFieldGui.TextField("Search (name/blueprint)", _searchUnits, "units_search");
        DrawUnitsFlatBrowser(w);
    }

    private void DrawGroupsTab(float w)
    {
        // Real armies (Data.ObjArmies, the same authoritative list the
        // Armies tab itself edits) are the row list — not "whatever army
        // name happens to already own a placed unit". Mirrors
        // FAForeverMapEditor's own Groups tab: ARMY_1..ARMY_N and
        // NEUTRAL_CIVILIAN all show as their own collapsible row with a
        // "0-0" badge even before any group exists under them.
        var shownArmyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var army in Data.ObjArmies)
        {
            shownArmyNames.Add(army.Name);
            DrawArmyGroupsRoot(army.Name, w);
        }

        // Units whose ArmyName doesn't match any real SC2ObjArmy (legacy
        // data, a manual typo, or the army was deleted after units were
        // already assigned to it) still need a way to be found/fixed —
        // shown as their own fallback rows, same treatment.
        var orphanArmyNames = Data.ObjUnits.Select(u => u.ArmyName).Distinct()
            .Where(a => !shownArmyNames.Contains(a)).ToList();
        foreach (var armyName in orphanArmyNames)
            DrawArmyGroupsRoot(armyName, w);

        if (_selGroupId != 0) DrawSelectedGroupInspector(w);
    }

    private (int Groups, int Units) CountArmyRootChildren(string armyName)
    {
        int groups = 0, units = 0;
        for (var i = 0; i < Data.ObjUnits.Count; i++)
        {
            if (!string.Equals(Data.ObjUnits[i].ArmyName, armyName, StringComparison.Ordinal) || Data.ObjUnits[i].ParentIndex >= 0) continue;
            if (Data.ObjUnits[i].IsGroup) groups++; else units++;
        }
        return (groups, units);
    }

    /// <summary>Direct children only (matches FAForeverMapEditor's own
    /// UnitListObject.UpdateValues badge — Source.UnitGroups.Count/
    /// Source.Units.Count — not a recursive total), so the badge tells you
    /// the size of THIS level without needing to expand further.</summary>
    private (int Groups, int Units) CountUnitChildren(int index)
    {
        int groups = 0, units = 0;
        for (var i = 0; i < Data.ObjUnits.Count; i++)
        {
            if (Data.ObjUnits[i].ParentIndex != index) continue;
            if (Data.ObjUnits[i].IsGroup) groups++; else units++;
        }
        return (groups, units);
    }

    /// <summary>A parent's group children only — real _save.lua's Groups
    /// panel never lists individual units inline (only group folders with
    /// count badges), and unlike the old tree, empty groups are NEVER
    /// skipped here — the real FAForeverMapEditor panel this is modeled on
    /// always shows every group, including "0-0" ones.</summary>
    private List<int> GetChildGroupIndices(string armyName, int parentIndex)
    {
        var result = new List<int>();
        for (var i = 0; i < Data.ObjUnits.Count; i++)
        {
            var n = Data.ObjUnits[i];
            if (n.ParentIndex == parentIndex && n.ArmyName == armyName && n.IsGroup) result.Add(i);
        }
        return result;
    }

    /// <summary>One army's whole row + (if expanded) its root-level group
    /// list — the direct analog of FAForeverMapEditor's per-army
    /// UnitListObject row (IsRoot=true). An army is not itself a group, so
    /// this row only ever holds group children plus a "+ Group" row — no
    /// unit-creation/placement/rename affordance lives here anymore (see
    /// DrawSelectedGroupInspector for that, once a real group exists to
    /// hold the unit).</summary>
    private void DrawArmyGroupsRoot(string armyName, float w)
    {
        var collapsed = _unitArmyCollapsed.Contains(armyName);
        var counts = CountArmyRootChildren(armyName);

        GUILayout.BeginHorizontal();
        if (SC2EnvFieldGui.Button(collapsed ? "▸" : "▾", GUILayout.Width(SC2UiScale.Pxf(20f))))
        {
            if (collapsed) _unitArmyCollapsed.Remove(armyName); else _unitArmyCollapsed.Add(armyName);
        }
        var prevContent = GUI.contentColor;
        GUI.contentColor = ArmyRowColor;
        GUILayout.Label($"{armyName}  {counts.Groups}-{counts.Units}", GUILayout.ExpandWidth(true));
        GUI.contentColor = prevContent;
        GUILayout.EndHorizontal();
        var armyRowRect = GUILayoutUtility.GetLastRect();

        if (collapsed) return;

        var lineX = 7f;
        var lineTop = armyRowRect.y + armyRowRect.height * 0.5f;
        var lineBottom = lineTop;
        foreach (var r in GetChildGroupIndices(armyName, -1))
        {
            var childRect = DrawGroupTreeNode(r, 1, w);
            lineBottom = childRect.y + childRect.height * 0.5f;
            DrawTreeElbow(lineX, lineBottom, 14f);
        }
        DrawAddGroupRow(armyName, -1, counts.Groups == 0, 1, w);
        var addRect = GUILayoutUtility.GetLastRect();
        lineBottom = addRect.y + addRect.height * 0.5f;
        DrawTreeElbow(lineX, lineBottom, 14f);
        DrawTreeGuideLine(lineX, lineTop, lineBottom);
        GUILayout.Space(4);
    }

    private static readonly Color HideActiveButtonColor = new Color(0.55f, 0.42f, 0.12f);
    private static readonly Color SmallActionButtonColor = new Color(0.30f, 0.30f, 0.32f);

    /// <summary>One group row: expand arrow (always shown — a group can
    /// always gain subgroups even while currently empty), colored name +
    /// direct-child badge (click selects it for DrawSelectedGroupInspector),
    /// then Select/Hide/Isolate as compact icon buttons matching the "-"
    /// delete button's own size/style, right next to it. No rename/wreckage/
    /// add-unit affordance inline — those live in the selected-group
    /// inspector / Units tab so this row stays as close to the reference
    /// layout as the extra viewport-visibility actions allow.</summary>
    private Rect DrawGroupTreeNode(int index, int depth, float w)
    {
        if (index < 0 || index >= Data.ObjUnits.Count) return default;
        var node = Data.ObjUnits[index];
        var collapsed = _unitCollapsed.Contains(node.Id);

        GUILayout.BeginHorizontal();
        GUILayout.Space(SC2UiScale.Pxf(depth * 14f));
        if (SC2EnvFieldGui.Button(collapsed ? "▸" : "▾", GUILayout.Width(SC2UiScale.Pxf(20f))))
        {
            if (collapsed) _unitCollapsed.Remove(node.Id); else _unitCollapsed.Add(node.Id);
        }

        var counts = CountUnitChildren(index);
        var isSel = _selGroupId == node.Id;
        var prevContent = GUI.contentColor;
        GUI.contentColor = GroupRowColor;
        var icon = node.IsWreckage ? "☠ " : "";
        // Fixed width + truncation instead of ExpandWidth: ExpandWidth only
        // shrinks gracefully down to this button's own minimum content
        // width (driven by the FULL untruncated name) — for a long group
        // name at any real depth, that minimum alone can exceed the whole
        // panel's width no matter how wide ComputeGroupsPanelMinWidth made
        // it, silently pushing the trailing S/H/I/- buttons off the visible
        // edge, leaving only the first trailing button ("S") visible. Reserving fixed
        // space for the indent/arrow/4 trailing buttons up front and giving
        // the label only whatever's left (truncated to fit) guarantees this
        // row can never exceed the panel's actual width, regardless of name
        // length or nesting depth.
        // Scaled through SC2UiScale — this used to stay fixed
        // while the arrow/S/H/I/- buttons it's reserving room FOR (all
        // GUILayout.Width(SC2UiScale.Pxf(...)) now) grew with Scale, so the
        // real row exceeded `w` and pushed the trailing buttons off the
        // panel's right edge.
        var reserved = SC2UiScale.Pxf(depth * 14f + 20f + 4 * 24f + 10f);
        var labelWidth = Mathf.Max(SC2UiScale.Pxf(50f), w - reserved);
        var labelText = (isSel ? "● " : "") + icon + node.Name + $"  {counts.Groups}-{counts.Units}";
        if (GUILayout.Button(TruncateForLabel(labelText, labelWidth), GUILayout.Width(labelWidth)))
            _selGroupId = node.Id;
        GUI.contentColor = prevContent;

        var groupUnitIds = new List<int>();
        GatherUnitsUnderGroup(index, groupUnitIds);
        var isFullyHidden = groupUnitIds.Count > 0 && groupUnitIds.All(_hiddenUnitIds.Contains);

        var prevBg = GUI.backgroundColor;
        GUI.backgroundColor = SmallActionButtonColor;
        if (SC2EnvFieldGui.Button("S", GUILayout.Width(SC2UiScale.Pxf(24f))))
        {
            _selUnits.Clear();
            foreach (var id in groupUnitIds) _selUnits.Add(id);
            _lastActionSummary = $"Selected {groupUnitIds.Count} unit(s) under '{node.Name}'.";
        }
        GUI.backgroundColor = isFullyHidden ? HideActiveButtonColor : SmallActionButtonColor;
        if (SC2EnvFieldGui.Button("H", GUILayout.Width(SC2UiScale.Pxf(24f))))
        {
            if (isFullyHidden) foreach (var id in groupUnitIds) _hiddenUnitIds.Remove(id);
            else foreach (var id in groupUnitIds) _hiddenUnitIds.Add(id);
            _lastActionSummary = (isFullyHidden ? "Shown " : "Hid ") + $"{groupUnitIds.Count} unit(s) under '{node.Name}'.";
        }
        GUI.backgroundColor = SmallActionButtonColor;
        if (SC2EnvFieldGui.Button("I", GUILayout.Width(SC2UiScale.Pxf(24f))))
        {
            var keepSet = new HashSet<int>(groupUnitIds);
            _hiddenUnitIds.Clear();
            foreach (var u in Data.ObjUnits)
                if (!u.IsGroup && !keepSet.Contains(u.Id)) _hiddenUnitIds.Add(u.Id);
            _lastActionSummary = $"Isolated {groupUnitIds.Count} unit(s) under '{node.Name}'.";
        }
        GUI.backgroundColor = DeleteButtonColor;
        if (SC2EnvFieldGui.Button("-", GUILayout.Width(SC2UiScale.Pxf(24f))))
        {
            GUI.backgroundColor = prevBg;
            PushUndo();
            RemoveUnitSubtree(index);
            if (isSel) _selGroupId = 0;
            MarkObjectsDirty();
            GUILayout.EndHorizontal();
            return GUILayoutUtility.GetLastRect();
        }
        GUI.backgroundColor = prevBg;
        GUILayout.EndHorizontal();
        var ownRowRect = GUILayoutUtility.GetLastRect();

        if (!collapsed)
        {
            // Elbow-connector tree guide: one vertical line at this row's
            // indent, from its own row down to each child/"+ Group" row's
            // vertical center, with a short horizontal tick branching into
            // each one — a plain set of parallel vertical lines made a deep
            // chain of single-child groups look like an undifferentiated
            // stack with no cue for which "+ Group" belonged to which level.
            var lineX = SC2UiScale.Pxf(depth * 14f + 7f);
            var lineTop = ownRowRect.y + ownRowRect.height * 0.5f;
            var lineBottom = lineTop;
            foreach (var i in GetChildGroupIndices(node.ArmyName, index))
            {
                var childRect = DrawGroupTreeNode(i, depth + 1, w);
                lineBottom = childRect.y + childRect.height * 0.5f;
                DrawTreeElbow(lineX, lineBottom, SC2UiScale.Pxf((depth + 1) * 14f));
            }
            DrawAddGroupRow(node.ArmyName, index, false, depth + 1, w);
            var addRect = GUILayoutUtility.GetLastRect();
            lineBottom = addRect.y + addRect.height * 0.5f;
            DrawTreeElbow(lineX, lineBottom, SC2UiScale.Pxf((depth + 1) * 14f));
            DrawTreeGuideLine(lineX, lineTop, lineBottom);
        }
        return ownRowRect;
    }

    private static void DrawTreeGuideLine(float x, float yTop, float yBottom)
    {
        if (Event.current == null || Event.current.type != EventType.Repaint || yBottom <= yTop) return;
        var oldColor = GUI.color;
        GUI.color = TreeGuideLineColor;
        GUI.DrawTexture(new Rect(x, yTop, 1f, yBottom - yTop), Texture2D.whiteTexture);
        GUI.color = oldColor;
    }

    /// <summary>Short horizontal tick branching off the vertical guide at
    /// (x, y) out to a child row's own indent start — the piece that turns
    /// a bare vertical line into a real tree-branch connector.</summary>
    private static void DrawTreeElbow(float x, float y, float toX)
    {
        if (Event.current == null || Event.current.type != EventType.Repaint || toX <= x) return;
        var oldColor = GUI.color;
        GUI.color = TreeGuideLineColor;
        GUI.DrawTexture(new Rect(x, y, toX - x, 1f), Texture2D.whiteTexture);
        GUI.color = oldColor;
    }

    /// <summary>The full-width "+ Group" row shown as the last child at
    /// every level (army root or inside any group) — creates a new empty
    /// subgroup there. The very first root-level group created for an army
    /// defaults to "INITIAL" (FAForeverMapEditor's own rule); every other
    /// creation keeps the normal numbered fallback name.</summary>
    private void DrawAddGroupRow(string armyName, int parentIndex, bool isFirstRootGroup, int depth, float w)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Space(SC2UiScale.Pxf(depth * 14f));
        var prevBg = GUI.backgroundColor;
        GUI.backgroundColor = AddGroupRowColor;
        if (GUILayout.Button("+ Group", GUILayout.Height(SC2UiScale.Pxf(16f)), GUILayout.ExpandWidth(true)))
        {
            PushUndo();
            var name = isFirstRootGroup ? MakeUniqueUnitName("INITIAL") : MakeUniqueUnitName("GROUP_1");
            var node = new SC2ObjUnitNode { Id = Data.AllocateObjectId(), ArmyName = armyName, Name = name, ParentIndex = parentIndex, IsGroup = true };
            Data.ObjUnits.Add(node);
            if (parentIndex >= 0) _unitCollapsed.Remove(Data.ObjUnits[parentIndex].Id); // expand the parent so the new subgroup is immediately visible
            MarkObjectsDirty();
        }
        GUI.backgroundColor = prevBg;
        GUILayout.EndHorizontal();
    }

    /// <summary>An army is not itself a group that can hold units — real
    /// _save.lua always wraps an army's root-level content one level
    /// deeper (['Units'] = GROUP {...}). Resolves the group new units for
    /// this army should actually land in: an existing "INITIAL" group if
    /// one exists, else any existing root-level group, else a freshly
    /// created "INITIAL" group.</summary>
    private int ResolveDefaultUnitGroup(string armyName)
    {
        var anyGroupIdx = -1;
        for (var i = 0; i < Data.ObjUnits.Count; i++)
        {
            var n = Data.ObjUnits[i];
            if (n.ArmyName != armyName || n.ParentIndex != -1 || !n.IsGroup) continue;
            if (string.Equals(n.Name, "INITIAL", StringComparison.OrdinalIgnoreCase)) return i;
            if (anyGroupIdx < 0) anyGroupIdx = i;
        }
        if (anyGroupIdx >= 0) return anyGroupIdx;

        var node = new SC2ObjUnitNode { Id = Data.AllocateObjectId(), ArmyName = armyName, Name = MakeUniqueUnitName("INITIAL"), ParentIndex = -1, IsGroup = true };
        Data.ObjUnits.Add(node);
        MarkObjectsDirty();
        return Data.ObjUnits.Count - 1;
    }

    /// <summary>The details strip for whichever group is selected in the
    /// tree above — just its name and Wreckage flag. Select/Hide/Isolate
    /// live inline on the tree row itself (next to "-"), and unit creation/
    /// placement lives on the Units tab (targeting this same selection) —
    /// kept out of here so this panel doesn't grow the same button clutter
    /// it replaced.</summary>
    private void DrawSelectedGroupInspector(float w)
    {
        var index = Data.ObjUnits.FindIndex(u => u.Id == _selGroupId && u.IsGroup);
        if (index < 0) { _selGroupId = 0; return; }
        var node = Data.ObjUnits[index];

        GUILayout.Space(6);
        GUILayout.BeginHorizontal();
        SC2EnvFieldGui.SectionLabel($"Group: {node.Name}");
        if (SC2EnvFieldGui.Button("✕", GUILayout.Width(SC2UiScale.Pxf(24f)))) { _selGroupId = 0; GUILayout.EndHorizontal(); return; }
        GUILayout.EndHorizontal();

        var prevName = node.Name;
        var prevWreckage = node.IsWreckage;
        node.Name = SC2EnvFieldGui.TextField("Name", node.Name, "group_name_" + node.Id);
        node.IsWreckage = SC2EnvFieldGui.Toggle("Wreckage group (dead units' reclaim)", node.IsWreckage);
        if (node.Name != prevName || node.IsWreckage != prevWreckage) MarkObjectsDirty();
    }

    private void DrawUnitsFlatBrowser(float w)
    {
        var visible = Data.ObjUnits.Where(u => !u.IsGroup &&
            (MatchSearch(u.Name, _searchUnits) || MatchSearch(u.Type, _searchUnits) || MatchSearch(u.ArmyName, _searchUnits))).ToList();
        var visibleIds = visible.Select(u => u.Id).ToList();

        DrawBatchBar(w, _selUnits, new List<(string, Action)>
        {
            ("Anchor selected", () => { PushUndo(); foreach (var u in visible.Where(x => _selUnits.Contains(x.Id))) { u.AnchorToTerrain = true; ReprojectUnit(u); } MarkObjectsDirty(); }),
            ("Delete selected", () => { PushUndo(); var ids = new HashSet<int>(_selUnits); Data.ObjUnits.RemoveAll(u => ids.Contains(u.Id)); _selUnits.Clear(); MarkObjectsDirty(); })
        });

        _assignArmyInput = SC2EnvFieldGui.TextField("Assign army to selection", _assignArmyInput, "units_assign_army");
        if (_selUnits.Count > 0 && !string.IsNullOrEmpty(_assignArmyInput) && SC2EnvFieldGui.Button("Apply army assignment", GUILayout.Height(SC2UiScale.Pxf(18f))))
        {
            PushUndo();
            foreach (var u in visible.Where(x => _selUnits.Contains(x.Id))) u.ArmyName = _assignArmyInput;
            MarkObjectsDirty();
        }

        _scrollA = GUILayout.BeginScrollView(_scrollA, GUILayout.Height(SC2UiScale.Pxf(280f)));
        foreach (var u in visible)
        {
            var isSel = _selUnits.Contains(u.Id);
            var blueprint = string.IsNullOrEmpty(u.Type) ? "(no blueprint)" : u.Type;
            var label = $"{(isSel ? "● " : "○ ")}{u.Name}   [{u.ArmyName}] {blueprint}   X:{u.Position.x:0} Y:{u.Position.y:0} Z:{u.Position.z:0}";
            if (GUILayout.Button(TruncateForLabel(label, w - 8), GUILayout.Width(w - 8)))
                HandleRowClick(_selUnits, visibleIds, u.Id, ref _lastClickedUnitIdx);
        }
        GUILayout.EndScrollView();
    }

    private bool _unitGroupPickerOpen;

    /// <summary>Only ever shown for a real leaf unit now (both selection
    /// paths that populate _selUnits — the flat browser and a group row's
    /// "S" button — already filter to !IsGroup), so there's no "Is Group"
    /// toggle here anymore; a leaf can't become a group in this UI. Army is
    /// likewise no longer independently editable — it's always whatever
    /// army the unit's parent GROUP belongs to, changed here only by
    /// picking a different group.</summary>
    private void DrawUnitInspector(float w, SC2ObjUnitNode node)
    {
        if (SC2EnvFieldGui.Button("← Back to list", GUILayout.Height(SC2UiScale.Pxf(20f)))) { _selUnits.Clear(); _unitGroupPickerOpen = false; return; }
        SC2EnvFieldGui.SectionLabel(node.Name);

        var prevName = node.Name;
        var prevType = node.Type;
        var prevParentIndex = node.ParentIndex;
        var prevOrientation = node.OrientationEuler;
        var prevAnchor = node.AnchorToTerrain;
        var prevOffset = node.HeightOffset;
        var prevPosition = node.Position;
        node.Name = SC2EnvFieldGui.TextField("Name", node.Name, "uniti_name");

        SC2EnvFieldGui.SectionLabel("Group");
        var parentGroup = node.ParentIndex >= 0 && node.ParentIndex < Data.ObjUnits.Count ? Data.ObjUnits[node.ParentIndex] : null;
        var currentLabel = parentGroup != null ? $"{parentGroup.ArmyName} / {parentGroup.Name}" : "(no group — select one)";
        if (SC2EnvFieldGui.Button(currentLabel, GUILayout.Height(SC2UiScale.Pxf(22f)))) _unitGroupPickerOpen = !_unitGroupPickerOpen;
        if (_unitGroupPickerOpen)
        {
            _scrollC = GUILayout.BeginScrollView(_scrollC, GUILayout.Height(SC2UiScale.Pxf(140f)));
            for (var i = 0; i < Data.ObjUnits.Count; i++)
            {
                var g = Data.ObjUnits[i];
                if (!g.IsGroup) continue;
                var isCurrent = i == node.ParentIndex;
                if (GUILayout.Button(TruncateForLabel((isCurrent ? "● " : "") + $"{g.ArmyName} / {g.Name}", w - 8), GUILayout.Width(w - 8)))
                {
                    PushUndo();
                    node.ParentIndex = i;
                    node.ArmyName = g.ArmyName;
                    _unitGroupPickerOpen = false;
                }
            }
            GUILayout.EndScrollView();
        }

        node.Type = SC2EnvFieldGui.TextField("Blueprint", node.Type, "uniti_type");
        if (SC2EnvFieldGui.Button("Choose unit blueprint", GUILayout.Height(SC2UiScale.Pxf(20f)))) OpenUnitBrowser(node);
        node.OrientationEuler = SC2EnvFieldGui.Vector3Field("Orientation", node.OrientationEuler, "uniti_rot");
        DrawAnchorBlock(ref node.AnchorToTerrain, ref node.HeightOffset, node.Position.y,
            () => ReprojectUnit(node),
            y => node.Position = new Vector3(node.Position.x, y, node.Position.z));
        node.Position = SnapXZ(SC2EnvFieldGui.Vector3Field("Position", node.Position, "uniti_pos"));

        if (node.Name != prevName || node.Type != prevType || node.ParentIndex != prevParentIndex ||
            node.OrientationEuler != prevOrientation || node.AnchorToTerrain != prevAnchor ||
            node.HeightOffset != prevOffset || node.Position != prevPosition)
            MarkObjectsDirty();

        GUILayout.Space(6);
        if (SC2EnvFieldGui.Button("Focus camera", GUILayout.Height(SC2UiScale.Pxf(20f)))) RequestCameraFocus?.Invoke(node.Position);
        if (SC2EnvFieldGui.Button("Delete", GUILayout.Height(SC2UiScale.Pxf(20f))))
        {
            PushUndo();
            var idx = Data.ObjUnits.IndexOf(node);
            if (idx >= 0) RemoveUnitSubtree(idx);
            _selUnits.Clear();
            MarkObjectsDirty();
        }
    }

    /// <summary>Recursively collects every leaf unit's Id under a group,
    /// including units nested inside its subgroups.
    /// Groups themselves are never added, only real placed units.</summary>
    private void GatherUnitsUnderGroup(int groupIndex, List<int> result)
    {
        for (var i = 0; i < Data.ObjUnits.Count; i++)
        {
            if (Data.ObjUnits[i].ParentIndex != groupIndex) continue;
            if (Data.ObjUnits[i].IsGroup) GatherUnitsUnderGroup(i, result);
            else result.Add(Data.ObjUnits[i].Id);
        }
    }

    private void RemoveUnitSubtree(int index)
    {
        var toRemove = new List<int> { index };
        var frontier = new Queue<int>();
        frontier.Enqueue(index);
        while (frontier.Count > 0)
        {
            var cur = frontier.Dequeue();
            for (var i = 0; i < Data.ObjUnits.Count; i++)
                if (Data.ObjUnits[i].ParentIndex == cur && !toRemove.Contains(i))
                {
                    toRemove.Add(i);
                    frontier.Enqueue(i);
                }
        }
        toRemove.Sort();
        for (var i = toRemove.Count - 1; i >= 0; i--)
        {
            var removedIdx = toRemove[i];
            Data.ObjUnits.RemoveAt(removedIdx);
            for (var j = 0; j < Data.ObjUnits.Count; j++)
                if (Data.ObjUnits[j].ParentIndex > removedIdx) Data.ObjUnits[j].ParentIndex--;
                else if (Data.ObjUnits[j].ParentIndex == removedIdx) Data.ObjUnits[j].ParentIndex = -1;
        }
        // No blanket state-clear needed — _unitCollapsed is keyed by
        // node.Id, not list index, so surviving nodes' collapse state stays
        // correctly attached to them across this index reshuffle. A removed
        // node's leftover Id entry is harmless (never looked up again).
    }

    // ── 3. Props ─────────────────────────────────────────────────────────
    private void DrawProps(float w)
    {
        if (_selProps.Count == 1)
        {
            var item = Data.ObjProps.FirstOrDefault(p => p.Id == _selProps.First());
            if (item != null) { DrawPropInspector(w, item); return; }
            _selProps.Clear();
        }
        DrawPropsBrowser(w);
    }

    /// <summary>Perf caching for DrawPropsBrowser's filtered+sorted list —
    /// this LINQ chain (search filter, system filter,
    /// GroupBy-equivalent sort) used to rebuild from scratch on EVERY OnGUI
    /// event, not just when the search text/filter/prop count actually
    /// changed. Invalidated by the same three inputs that actually affect
    /// the result (_searchProps/_filterProps/Data.ObjProps.Count), plus a
    /// periodic safety net (matches _dataVersion's own reasoning) covering
    /// the one known gap: editing a SINGLE prop's BlueprintPath text field
    /// directly in the Inspector doesn't change the Count and isn't
    /// (currently) preceded by PushUndo, so it wouldn't otherwise be
    /// detected until the safety net's next tick.</summary>
    private List<SC2ObjProp> _cachedVisibleProps;
    private List<int> _cachedVisiblePropIds;
    private string _cachedVisiblePropsSearch;
    private SysFilter _cachedVisiblePropsFilter;
    private int _cachedVisiblePropsSourceCount = -1;
    private int _cachedVisiblePropsTicks;
    private const int ListCacheSafetyNetInterval = 60; // ~1s at 60 Repaint events/sec

    private void RefreshVisiblePropsIfNeeded()
    {
        _cachedVisiblePropsTicks++;
        var dueForSafetyNet = _cachedVisiblePropsTicks >= ListCacheSafetyNetInterval;
        if (_cachedVisibleProps != null && !dueForSafetyNet &&
            _cachedVisiblePropsSearch == _searchProps && _cachedVisiblePropsFilter == _filterProps &&
            _cachedVisiblePropsSourceCount == Data.ObjProps.Count)
            return;

        _cachedVisiblePropsTicks = 0;
        var visible = Data.ObjProps.Where(p => MatchSearch(p.BlueprintPath, _searchProps)).ToList();
        visible = _filterProps switch
        {
            SysFilter.Selected => visible.Where(p => _selProps.Contains(p.Id)).ToList(),
            SysFilter.Invalid => visible.Where(p => string.IsNullOrEmpty(p.BlueprintPath)).ToList(),
            SysFilter.Unanchored => visible.Where(p => !p.AnchorToTerrain).ToList(),
            _ => visible
        };
        // Grouped by blueprint (props scattered via the brush share one
        // blueprint and were previously scattered arbitrarily through the
        // list in creation order) — Id as a stable tie-breaker within a
        // group, not insertion order, so re-filtering doesn't reshuffle it.
        visible = visible.OrderBy(p => p.BlueprintPath ?? "", StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Id).ToList();
        _cachedVisibleProps = visible;
        _cachedVisiblePropIds = visible.Select(p => p.Id).ToList();
        _cachedVisiblePropsSearch = _searchProps;
        _cachedVisiblePropsFilter = _filterProps;
        _cachedVisiblePropsSourceCount = Data.ObjProps.Count;
    }

    private void DrawPropsBrowser(float w)
    {
        SC2EnvFieldGui.SectionLabel($"Props ({Data.ObjProps.Count})");
        if (SC2EnvFieldGui.Button("Browse prop blueprints", GUILayout.Height(SC2UiScale.Pxf(24f)))) OpenPropBrowser(null);
        _searchProps = SC2EnvFieldGui.TextField("Search (blueprint)", _searchProps, "props_search");
        DrawSystemFilterRow(w, ref _filterProps);

        RefreshVisiblePropsIfNeeded();
        var visible = _cachedVisibleProps;
        var visibleIds = _cachedVisiblePropIds;

        // "Select all matching" — the piece needed to hide/erase an entire
        // blueprint in bulk without clicking every individual instance
        // (brush-scattered props can easily number in the hundreds). Type
        // the blueprint into Search above, then this selects every filtered
        // instance so the existing Hide/Delete batch actions below act on
        // the whole group at once.
        GUILayout.BeginHorizontal();
        if (SC2EnvFieldGui.Button($"Select All ({visible.Count})", GUILayout.Height(SC2UiScale.Pxf(20f)), GUILayout.Width(w * 0.48f)))
            foreach (var id in visibleIds) _selProps.Add(id);
        if (SC2EnvFieldGui.Button("Clear Selection", GUILayout.Height(SC2UiScale.Pxf(20f)), GUILayout.Width(w * 0.48f)))
            _selProps.Clear();
        GUILayout.EndHorizontal();

        if (_hiddenPropIds.Count > 0)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label($"{_hiddenPropIds.Count} prop(s) hidden.");
            if (SC2EnvFieldGui.Button("Show All", GUILayout.Height(SC2UiScale.Pxf(18f)), GUILayout.Width(SC2UiScale.Pxf(90f)))) _hiddenPropIds.Clear();
            GUILayout.EndHorizontal();
        }

        DrawPropsByBlueprintPanel(w);

        DrawBatchBar(w, _selProps, new List<(string, Action)>
        {
            ("Anchor selected", () => { PushUndo(); foreach (var p in visible.Where(x => _selProps.Contains(x.Id))) { p.AnchorToTerrain = true; ReprojectProp(p); } MarkObjectsDirty(); }),
            ("Duplicate w/ symmetry", () => { PushUndo(); foreach (var p in visible.Where(x => _selProps.Contains(x.Id)).ToList()) DuplicatePropWithSymmetry(p); }),
            ("Link selected (symmetry)", () => { PushUndo(); LinkPropsWithSymmetry(visible.Where(x => _selProps.Contains(x.Id)).ToList()); }),
            ("Hide selected", () => { foreach (var id in _selProps) _hiddenPropIds.Add(id); }),
            ("Show selected", () => { foreach (var id in _selProps) _hiddenPropIds.Remove(id); }),
            ("Delete selected", () => { PushUndo(); DeletePropsById(new HashSet<int>(_selProps)); }),
            ("Replace selected", () => OpenPropBrowserForBatchReplace(visible.Where(x => _selProps.Contains(x.Id)).ToList())),
            ("Copy JSON to clipboard", () => ExportPropsJson(visible.Where(x => _selProps.Contains(x.Id)).ToList()))
        });

        if (_selProps.Count >= 2)
            DrawPropsMixedEditPanel(w, visible.Where(x => _selProps.Contains(x.Id)).ToList());

        DrawVirtualizedPropRows(visible, visibleIds, w, 300f);

        GUILayout.BeginHorizontal();
        if (SC2EnvFieldGui.Button("Add at camera focus", GUILayout.Height(SC2UiScale.Pxf(24f)), GUILayout.Width(w * 0.48f)))
        {
            PushUndo();
            var newProp = new SC2ObjProp { Id = Data.AllocateObjectId(), Position = GetSpawnPoint() };
            Data.ObjProps.Add(newProp);
            // Unlike "Place in viewport..." (PlacePropAt), this
            // button must also mirror the new prop and link it into a
            // SC2ObjSymmetryGroup when a symmetry mode is active.
            if (Data.Symmetry != SymmetryMode.None)
                LinkPropsWithSymmetry(new List<SC2ObjProp> { newProp });
            MarkObjectsDirty();
        }
        if (SC2EnvFieldGui.Button("Paste from clipboard", GUILayout.Height(SC2UiScale.Pxf(24f)), GUILayout.Width(w * 0.48f)))
            ImportPropsJson();
        GUILayout.EndHorizontal();

        // Single-object click-to-place — distinct from the brush below,
        // which scatters multiple instances per stroke.
        var placingSingleProp = _placementKind == PlacementKind.Prop;
        if (SC2EnvFieldGui.Button(placingSingleProp ? "Stop placing" : "Place in viewport...", GUILayout.Height(SC2UiScale.Pxf(24f))))
        {
            if (placingSingleProp) DisarmPlacement();
            else OpenPropBrowserForPlacement();
        }

        GUILayout.Space(8);
        DrawPropsBrushPanel(w);
    }

    // Scaled through SC2UiScale — these rows draw via plain GUI.Button,
    // which inherits GUI.skin.button's font size (SC2UnifiedSkin, itself
    // scaled), so a fixed row-height const here would fall out of sync
    // with the real (bigger) text at higher UI Scale the same way every
    // other fixed-row-height list this pass has fixed already did.
    private static float PropRowHeight => SC2UiScale.Pxf(20f);

    /// <summary>Virtualized replacement for a GUILayout.Button-per-row scroll
    /// list — the old version built a real GUILayout.
    /// Button (with its own internal style/layout bookkeeping) for all 2871
    /// items every single OnGUI event, even though the 300px-tall scroll
    /// view can only ever show ~15 rows at once. Uses the low-level GUI.*
    /// (not GUILayout.*) API so only the rows actually within the visible
    /// scroll range get a real Button call — the row count no longer
    /// determines the draw cost, only the view's pixel height does. A small
    /// padding of 2 rows above/below the strictly-visible range avoids a
    /// visible pop-in of half-rendered rows while scrolling.</summary>
    private void DrawVirtualizedPropRows(List<SC2ObjProp> visible, List<int> visibleIds, float w, float viewHeight)
    {
        var rowWidth = Mathf.Max(1f, w - 24f); // leaves room for the vertical scrollbar
        var contentHeight = Mathf.Max(viewHeight, visible.Count * PropRowHeight);
        var viewRect = GUILayoutUtility.GetRect(w, viewHeight);
        var contentRect = new Rect(0, 0, rowWidth, contentHeight);
        _scrollA = GUI.BeginScrollView(viewRect, _scrollA, contentRect);

        var first = Mathf.Max(0, Mathf.FloorToInt(_scrollA.y / PropRowHeight) - 2);
        var last = Mathf.Min(visible.Count - 1, first + Mathf.CeilToInt(viewHeight / PropRowHeight) + 4);
        for (var i = first; i <= last; i++)
        {
            var p = visible[i];
            var isSel = _selProps.Contains(p.Id);
            var label = $"{(isSel ? "● " : "○ ")}{ShortBlueprint(p.BlueprintPath)}   X:{p.Position.x:0} Z:{p.Position.z:0}" + (p.AnchorToTerrain ? "" : "  [Y locked]") + (_hiddenPropIds.Contains(p.Id) ? "  [hidden]" : "");
            var rowRect = new Rect(0, i * PropRowHeight, rowWidth, PropRowHeight - 2);
            if (GUI.Button(rowRect, TruncateForLabel(label, rowWidth - 8)))
                HandleRowClick(_selProps, visibleIds, p.Id, ref _lastClickedPropIdx);
        }
        GUI.EndScrollView();
    }

    /// <summary>"Disable/erase props of a specific blueprint" — a direct,
    /// one-click-per-blueprint version of what the Search+"Select All"+
    /// Hide/Delete combo above already makes POSSIBLE but not immediately
    /// discoverable (you have to already know the exact blueprint string to
    /// type into Search). Lists every distinct blueprint actually placed on
    /// the map, with its instance count, and Hide/Show/Delete buttons right
    /// on that row — no need to search or multi-select first. Collapsed by
    /// default (matches the Materials tab's own collapsible-list pattern)
    /// since a heavily-scattered map can have many distinct blueprints.</summary>
    private List<IGrouping<string, SC2ObjProp>> _cachedPropGroups;
    private int _cachedPropGroupsSourceCount = -1;

    private void DrawPropsByBlueprintPanel(float w)
    {
        // This GroupBy+OrderBy must not run while the
        // foldout below is COLLAPSED. The Foldout check runs first —
        // while collapsed, nothing below it executes at all. The label
        // shows no count until first expanded (rather than paying the full
        // grouping cost just to keep a collapsed label's number fresh);
        // once expanded, the result is cached and only rebuilt when the
        // prop count actually changes.
        var foldoutLabel = _cachedPropGroups != null ? $"By blueprint ({_cachedPropGroups.Count})" : "By blueprint";
        if (!SC2EnvFieldGui.Foldout(ref _propsByBlueprintExpanded, foldoutLabel)) return;

        if (_cachedPropGroups == null || _cachedPropGroupsSourceCount != Data.ObjProps.Count)
        {
            _cachedPropGroupsSourceCount = Data.ObjProps.Count;
            _cachedPropGroups = Data.ObjProps
                .Where(p => !string.IsNullOrEmpty(p.BlueprintPath))
                .GroupBy(p => p.BlueprintPath, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        var groups = _cachedPropGroups;

        if (groups.Count == 0)
        {
            GUILayout.Label("No props placed yet.");
            return;
        }

        _scrollPropsByBlueprint = GUILayout.BeginScrollView(_scrollPropsByBlueprint, GUILayout.Height(Mathf.Min(SC2UiScale.Pxf(220f), groups.Count * SC2UiScale.Pxf(22f) + SC2UiScale.Pxf(4f))));
        foreach (var group in groups)
        {
            var blueprint = group.Key;
            var ids = group.Select(p => p.Id).ToList();
            var hiddenCount = ids.Count(id => _hiddenPropIds.Contains(id));

            GUILayout.BeginHorizontal();
            var label = $"{ShortBlueprint(blueprint)} ({ids.Count})" + (hiddenCount > 0 ? $"  [{hiddenCount} hidden]" : "");
            GUILayout.Label(TruncateForLabel(label, w * 0.46f), GUILayout.Width(w * 0.46f));
            if (SC2EnvFieldGui.Button(hiddenCount == ids.Count ? "Show" : "Hide", GUILayout.Width(w * 0.15f)))
            {
                if (hiddenCount == ids.Count) foreach (var id in ids) _hiddenPropIds.Remove(id);
                else foreach (var id in ids) _hiddenPropIds.Add(id);
            }
            if (SC2EnvFieldGui.Button("Select", GUILayout.Width(w * 0.15f)))
            {
                _selProps.Clear();
                foreach (var id in ids) _selProps.Add(id);
            }
            if (SC2EnvFieldGui.Button("Delete", GUILayout.Width(w * 0.15f)))
            {
                PushUndo();
                DeletePropsById(new HashSet<int>(ids));
                GUILayout.EndHorizontal();
                break;
            }
            GUILayout.EndHorizontal();
        }
        GUILayout.EndScrollView();
    }

    // ── Props brush/scatter ──────────────────
    // "pick either a single prop or a prop brush (e.g. for scattering
    // trees), place directly on the terrain" - a materially different
    // gesture from the single-click PlacementKind system above (continuous
    // paint-while-dragging, not one object per click), so it gets its own
    // dedicated state/drag handling rather than being folded into
    // PlacementKind.
    private bool _propsBrushActive;
    private bool _propsBrushErasing;
    private string _propsBrushBlueprint = "";
    // The Browse... picker only hands back a blueprint path string; the real
    // textured-model preview (SC2GameAssetScenePreview.TryShowProp) needs the
    // full SC2GameAsset (LOD/material-set info), so it's kept alongside the
    // path rather than re-resolved from it.
    private SC2GameAsset _propsBrushAsset;
    private float _propsBrushRadius = 8f;
    // F interactive resize, Blender-style —
    // see SC2BrushRadiusGesture's own doc comment.
    private readonly SC2BrushRadiusGesture _propsBrushRadiusGesture = new SC2BrushRadiusGesture();
    private float _propsBrushDensity = 3f; // scatter attempts per paint tick
    private float _propsBrushRotationJitter = 180f; // +/- degrees around Y
    private float _propsBrushScaleMin = 0.8f; // absolute scale multiplier range, not a +/- jitter around 1.0
    private float _propsBrushScaleMax = 1.2f;
    private float _propsBrushMinSpacing = 2f;
    private bool _propsBrushDragging;
    private readonly List<SC2ObjProp> _propsBrushStrokeNewProps = new();

    private void DrawPropsBrushPanel(float w)
    {
        SC2EnvFieldGui.SectionLabel("Brush (scatter)");
        GUILayout.BeginHorizontal();
        var blueprintLabelWidth = w * 0.6f;
        var blueprintLabel = string.IsNullOrEmpty(_propsBrushBlueprint)
            ? "Blueprint: (none)"
            : "Blueprint: " + TruncateForLabel(ShortBlueprint(_propsBrushBlueprint), blueprintLabelWidth - 66f); // ~66px reserved for the "Blueprint: " prefix itself
        GUILayout.Label(blueprintLabel, GUILayout.Width(blueprintLabelWidth));
        if (SC2EnvFieldGui.Button("Browse...", GUILayout.Width(w * 0.35f)))
            _assetBrowser.Open(SC2UnitPropBrowser.AssetKind.Prop, selection =>
            {
                _propsBrushBlueprint = selection.BlueprintPath;
                _propsBrushAsset = selection.Asset;
            });
        GUILayout.EndHorizontal();

        _propsBrushRadius = DrawCompactSliderLocal("Radius (F resize)", _propsBrushRadius, 1f, 60f);
        _propsBrushDensity = DrawCompactSliderLocal("Density", _propsBrushDensity, 0.5f, 20f);
        _propsBrushRotationJitter = DrawCompactSliderLocal("Rotation jitter (deg)", _propsBrushRotationJitter, 0f, 180f);
        _propsBrushScaleMin = DrawCompactSliderLocal("Scale min", _propsBrushScaleMin, 0.05f, 3f);
        _propsBrushScaleMax = DrawCompactSliderLocal("Scale max", _propsBrushScaleMax, 0.05f, 3f);
        if (_propsBrushScaleMin > _propsBrushScaleMax) _propsBrushScaleMax = _propsBrushScaleMin;
        _propsBrushMinSpacing = DrawCompactSliderLocal("Min spacing", _propsBrushMinSpacing, 0.1f, 20f);

        _propsBrushErasing = SC2EnvFieldGui.Toggle("Eraser mode", _propsBrushErasing);

        var canPaint = !string.IsNullOrEmpty(_propsBrushBlueprint) || _propsBrushErasing;
        // See DrawUndoRedoRow's own comment for why this restores the prior
        // value instead of hardcoding true.
        var guiEnabledBeforePaintButton = GUI.enabled;
        GUI.enabled = guiEnabledBeforePaintButton && canPaint;
        if (SC2EnvFieldGui.Button(_propsBrushActive ? "Stop painting" : "Paint in viewport...", GUILayout.Height(SC2UiScale.Pxf(24f))))
            _propsBrushActive = !_propsBrushActive;
        GUI.enabled = guiEnabledBeforePaintButton;
        if (!canPaint)
            SC2EnvFieldGui.WrapLabel("Pick a blueprint first (or turn on Eraser mode).", w);
    }

    private static float DrawCompactSliderLocal(string label, float value, float min, float max)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(label, GUILayout.Width(SC2UiScale.Pxf(140f)));
        var v = GUILayout.HorizontalSlider(value, min, max);
        GUILayout.Label(v.ToString("0.##"), GUILayout.Width(SC2UiScale.Pxf(40f)));
        GUILayout.EndHorizontal();
        return v;
    }

    /// <summary>Continuous paint gesture — called every frame the brush is
    /// active while the mouse is down/dragging in the viewport, from
    /// OnGameViewGUI. Uniform-area disc sampling around the raycast hit
    /// point; each candidate is rejected if it's within MinSpacing of an
    /// existing prop (including ones placed earlier in THIS stroke), so
    /// repeated passes over the same spot don't stack props on top of each
    /// other.</summary>
    private void ScatterPropsAt(Vector3 center)
    {
        var attempts = Mathf.Max(1, Mathf.RoundToInt(_propsBrushDensity));
        for (var i = 0; i < attempts; i++)
        {
            var angle = UnityEngine.Random.value * Mathf.PI * 2f;
            var dist = Mathf.Sqrt(UnityEngine.Random.value) * _propsBrushRadius;
            var x = center.x + Mathf.Cos(angle) * dist;
            var z = center.z + Mathf.Sin(angle) * dist;

            var tooClose = false;
            foreach (var p in Data.ObjProps)
            {
                var dx = p.Position.x - x; var dz = p.Position.z - z;
                if (dx * dx + dz * dz < _propsBrushMinSpacing * _propsBrushMinSpacing) { tooClose = true; break; }
            }
            if (tooClose) continue;

            // Water-excluded ground height (same projection hfield.bdf/
            // Collision2/Costs already use) — a scattered prop always rests
            // on real terrain, submerged or not, and can never end up
            // floating at the water's own rendered surface height. See
            // PlacePropAt's own comment for the fix history behind this.
            var groundHeight = _terrainShell.SampleLiveHeightGrid(x, z);

            var scale = UnityEngine.Random.Range(_propsBrushScaleMin, _propsBrushScaleMax);
            var rotY = UnityEngine.Random.Range(-_propsBrushRotationJitter, _propsBrushRotationJitter);
            var prop = new SC2ObjProp
            {
                Id = Data.AllocateObjectId(),
                BlueprintPath = _propsBrushBlueprint,
                Position = new Vector3(x, groundHeight, z),
                Rotation = Quaternion.Euler(0f, rotY, 0f),
                Scale = Mathf.Max(0.05f, scale),
                AnchorToTerrain = true
            };
            Data.ObjProps.Add(prop);
            _propsBrushStrokeNewProps.Add(prop);
            ShowBrushPropPreview(prop);
        }
    }

    /// <summary>The generic viewport dot (DrawViewportDot) is a stand-in for
    /// every prop regardless of source; only SC2GameAssetScenePreview shows
    /// the actual SCM mesh+textures, and it must be told about each new prop
    /// explicitly (OpenPropBrowser's single-pick flow already does this for
    /// its own prop — brush/mirror-created props need the same call).</summary>
    private void ShowBrushPropPreview(SC2ObjProp prop)
    {
        if (_propsBrushAsset == null) return;
        SC2GameAssetScenePreview.TryShowProp(prop.Id, _propsBrushAsset, _assetBrowser.GameDataRoot, prop.Position, prop.Rotation, prop.Scale, out _);
    }

    /// <summary>Erasing at one point only cleared props under that exact
    /// cursor - the mirrored copies elsewhere on the map (placed via
    /// MirrorBrushStroke, or hand-symmetry-linked) were untouched, so
    /// "erase a symmetric scatter" required manually clicking every mirror
    /// slot too. Mirrors placement's own behavior instead: erase within the
    /// radius at EVERY symmetric slot of the cursor position, not just the
    /// one under the cursor.</summary>
    private void ErasePropsAt(Vector3 center)
    {
        var radiusSq = _propsBrushRadius * _propsBrushRadius;
        var centers = Data.Symmetry == SymmetryMode.None ? new List<Vector3> { center } : Data.GetSymmetricPositions(center);
        var removed = Data.ObjProps.RemoveAll(p => centers.Any(c =>
        {
            var dx = p.Position.x - c.x; var dz = p.Position.z - c.z;
            return dx * dx + dz * dz <= radiusSq;
        }));
        if (removed > 0) _lastActionSummary = $"Erased {removed} prop(s).";
    }

    /// <summary>A full brush stroke mirrors
    /// as a whole across symmetry - applied ONCE at stroke-end to every
    /// prop the stroke placed, not per-instance during painting (which
    /// would recursively re-mirror each mirrored copy too).
    ///
    /// This used to create the mirrored copies as
    /// plain position-mirrored duplicates with no SC2ObjSymmetryGroup link
    /// at all, on the assumption that a decorative scatter isn't expected
    /// to stay draggable-together — but brush-scattered
    /// props should keep a real, editable-together symmetry link too, same as
    /// hand-placed props. Reuses LinkPropsWithSymmetry (the exact same path
    /// PlacePropAt/"Add at camera focus" use) per source prop instead of
    /// hand-rolling a second mirroring implementation.</summary>
    private void MirrorBrushStroke()
    {
        if (Data.Symmetry == SymmetryMode.None || _propsBrushStrokeNewProps.Count == 0) return;
        foreach (var src in _propsBrushStrokeNewProps.ToList())
            LinkPropsWithSymmetry(new List<SC2ObjProp> { src }, ShowBrushPropPreview);
    }

    // ── Mixed-value multi-edit (practical-recommendations #2 from the FAF
    // analysis) — batch actions cover whole-selection operations, but bulk-
    // setting a single field (Scale, Type, Color...) across a selection with
    // DIFFERING current values needs its own small panel: show whether the
    // selection is "mixed" on that field, and apply the typed value to all
    // of them only on an explicit button press (never silently on-change —
    // with a mixed selection there's no single "current" value to compare
    // an edit against, so auto-apply would be ambiguous).
    private string _bulkScaleInput = "", _bulkBlueprintInput = "";

    private void DrawPropsMixedEditPanel(float w, List<SC2ObjProp> selected)
    {
        SC2EnvFieldGui.SectionLabel($"Bulk edit ({selected.Count} selected)");

        var scaleMixed = selected.Select(p => p.Scale).Distinct().Count() > 1;
        GUILayout.Label(scaleMixed ? "Scale: (mixed)" : $"Scale: {selected[0].Scale:0.##}");
        GUILayout.BeginHorizontal();
        _bulkScaleInput = SC2EnvFieldGui.TextField("", _bulkScaleInput, "bulk_scale");
        if (SC2EnvFieldGui.Button($"Apply to {selected.Count}", GUILayout.Width(SC2UiScale.Pxf(90f))) && float.TryParse(_bulkScaleInput, out var scaleVal))
        {
            PushUndo();
            foreach (var p in selected) p.Scale = scaleVal;
            MarkObjectsDirty();
        }
        GUILayout.EndHorizontal();

        var bpMixed = selected.Select(p => p.BlueprintPath).Distinct().Count() > 1;
        GUILayout.Label(bpMixed ? "Blueprint: (mixed)" : $"Blueprint: {ShortBlueprint(selected[0].BlueprintPath)}");
        GUILayout.BeginHorizontal();
        _bulkBlueprintInput = SC2EnvFieldGui.TextField("", _bulkBlueprintInput, "bulk_bp");
        if (SC2EnvFieldGui.Button($"Apply to {selected.Count}", GUILayout.Width(SC2UiScale.Pxf(90f))) && !string.IsNullOrEmpty(_bulkBlueprintInput))
        {
            PushUndo();
            foreach (var p in selected) p.BlueprintPath = _bulkBlueprintInput;
            MarkObjectsDirty();
        }
        GUILayout.EndHorizontal();
        GUILayout.Space(4);
    }

    private void DrawPropInspector(float w, SC2ObjProp p)
    {
        if (SC2EnvFieldGui.Button("← Back to list", GUILayout.Height(SC2UiScale.Pxf(20f)))) { _selProps.Clear(); return; }
        SC2EnvFieldGui.SectionLabel(ShortBlueprint(p.BlueprintPath));

        if (SC2EnvFieldGui.Button($"Select all of this blueprint", GUILayout.Height(SC2UiScale.Pxf(20f))))
            SelectAllPropsWithBlueprint(p.BlueprintPath);

        var prevBlueprint = p.BlueprintPath;
        var prevScale = p.Scale;
        var prevPos = p.Position;
        var prevAnchor = p.AnchorToTerrain;
        var prevOffset = p.HeightOffset;
        p.BlueprintPath = SC2EnvFieldGui.TextField("Blueprint", p.BlueprintPath, "propi_bp");
        if (SC2EnvFieldGui.Button("Choose prop blueprint", GUILayout.Height(SC2UiScale.Pxf(20f)))) OpenPropBrowser(p);
        p.Scale = SC2EnvFieldGui.FloatField("Scale", p.Scale, "propi_scale");

        DrawAnchorBlock(ref p.AnchorToTerrain, ref p.HeightOffset, p.Position.y,
            () => SetPropPosition(p, ReprojectedProp(p)),
            y => SetPropPosition(p, new Vector3(p.Position.x, y, p.Position.z)));
        var editedPos = SC2EnvFieldGui.Vector3Field("Position", p.Position, "propi_pos");
        if (editedPos != p.Position) SetPropPosition(p, editedPos);
        if (p.BlueprintPath != prevBlueprint || p.Scale != prevScale || p.Position != prevPos ||
            p.AnchorToTerrain != prevAnchor || p.HeightOffset != prevOffset) MarkObjectsDirty();

        var symGroup = FindSymmetryGroup(p.Id);
        SC2EnvFieldGui.SectionLabel(symGroup != null ? $"Symmetry: linked ({symGroup.Members.Count} members)" : "Symmetry: not linked");
        if (symGroup == null)
        {
            if (SC2EnvFieldGui.Button("Link with symmetry", GUILayout.Height(SC2UiScale.Pxf(20f)))) { PushUndo(); LinkPropsWithSymmetry(new List<SC2ObjProp> { p }); }
        }
        else if (SC2EnvFieldGui.Button("Unlink symmetry", GUILayout.Height(SC2UiScale.Pxf(20f)))) { PushUndo(); UnlinkSymmetryGroup(p.Id); }

        GUILayout.Space(6);
        if (SC2EnvFieldGui.Button("Duplicate with symmetry", GUILayout.Height(SC2UiScale.Pxf(20f)))) { PushUndo(); DuplicatePropWithSymmetry(p); }
        if (SC2EnvFieldGui.Button("Focus camera", GUILayout.Height(SC2UiScale.Pxf(20f)))) RequestCameraFocus?.Invoke(p.Position);
        if (SC2EnvFieldGui.Button("Delete", GUILayout.Height(SC2UiScale.Pxf(20f)))) { PushUndo(); DeletePropsById(new HashSet<int> { p.Id }); }
    }

    private Vector3 ReprojectedProp(SC2ObjProp p) => new(p.Position.x, SampleHeight(p.Position.x, p.Position.z) + p.HeightOffset, p.Position.z);

    /// <summary>"Select all objects of one kind" (Props) — replaces the
    /// current selection with every prop sharing the given blueprint path,
    /// so a batch action (Hide/Delete/Duplicate w/ symmetry etc.) can then
    /// act on the whole group without hand-picking each instance. Reached
    /// from the single-prop Inspector, which already knows the blueprint of
    /// the one item that's selected.</summary>
    private void SelectAllPropsWithBlueprint(string blueprintPath)
    {
        _selProps.Clear();
        foreach (var other in Data.ObjProps.Where(x => string.Equals(x.BlueprintPath, blueprintPath, StringComparison.OrdinalIgnoreCase)))
            _selProps.Add(other.Id);
        _lastActionSummary = $"Selected {_selProps.Count} prop(s) with blueprint '{ShortBlueprint(blueprintPath)}'.";
        ReportStatus?.Invoke(_lastActionSummary);
    }

    private void OpenUnitBrowser(SC2ObjUnitNode target)
    {
        _assetBrowser.Open(SC2UnitPropBrowser.AssetKind.Unit, selection =>
        {
            var asset = selection.Asset;
            if (target == null)
            {
                // A real drag-and-drop gesture straight out of the browser's
                // modal window isn't possible - GUI.ModalWindow blocks input
                // to everything behind it while open, so a drag can never
                // continue into the viewport. Arming click-to-place for the
                // picked blueprint the moment the picker closes is the
                // achievable equivalent: browse once, then place precisely
                // by clicking, with the same live preview/symmetry machinery
                // every other click-to-place tool already has - previously
                // this instantly created the unit at camera focus instead,
                // guessing an army if none could be resolved (the same
                // silent-fallback pattern fixed for PlaceUnitAt in this same
                // pass, now made consistent here too).
                var army = Data.ObjArmies.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a.Name))?.Name
                           ?? Data.ObjUnits.FirstOrDefault(u => !string.IsNullOrWhiteSpace(u.ArmyName))?.ArmyName;
                if (string.IsNullOrEmpty(army))
                {
                    _lastActionSummary = "Can't place a unit yet — no army exists. Place an army first.";
                    RepaintHost?.Invoke();
                    return;
                }
                var displayName = string.IsNullOrWhiteSpace(asset.DisplayName) ? asset.Id : asset.DisplayName;
                ArmPlacement(PlacementKind.Unit, army);
                _placementUnitType = asset.Id.ToLowerInvariant(); // must be set AFTER ArmPlacement - it resets this field
                _lastActionSummary = $"Click in the viewport to place {displayName} ({army}) — press Esc to stop.";
                RepaintHost?.Invoke();
                return;
            }

            PushUndo();
            target.IsGroup = false;
            target.Type = asset.Id.ToLowerInvariant();
            target.Name = string.IsNullOrWhiteSpace(asset.DisplayName) ? asset.Id : asset.DisplayName;
            if (target.AnchorToTerrain) ReprojectUnit(target);
            _lastActionSummary = "Selected unit " + asset.Id + (selection.IsComplete ? "." : " (some referenced resources are unavailable).");
            string previewError;
            if (!SC2GameAssetScenePreview.TryShowUnit(target.Id, asset, _assetBrowser.GameDataRoot, target.Position, Quaternion.Euler(target.OrientationEuler), out previewError))
                _lastActionSummary += " Preview unavailable: " + previewError;
            MarkObjectsDirty();
            RepaintHost?.Invoke();
        });
    }

    private void OpenPropBrowser(SC2ObjProp target)
    {
        _assetBrowser.Open(SC2UnitPropBrowser.AssetKind.Prop, selection =>
        {
            PushUndo();
            var asset = selection.Asset;
            if (target == null)
            {
                target = new SC2ObjProp { Id = Data.AllocateObjectId(), Position = GetSpawnPoint(), AnchorToTerrain = true };
                Data.ObjProps.Add(target);
            }
            target.BlueprintPath = selection.BlueprintPath;
            if (target.AnchorToTerrain) ReprojectProp(target);
            _lastActionSummary = "Selected prop " + asset.Id + (selection.IsComplete ? "." : " (some referenced resources are unavailable).");
            string previewError;
            if (!SC2GameAssetScenePreview.TryShowProp(target.Id, asset, _assetBrowser.GameDataRoot, target.Position, target.Rotation, target.Scale, out previewError))
                _lastActionSummary += " Preview unavailable: " + previewError;
            MarkObjectsDirty();
            RepaintHost?.Invoke();
        });
    }

    /// <summary>The single-prop "Place in viewport..." button's browse-then-
    /// arm flow — picks a blueprint, then arms click-to-place instead of
    /// instantiating immediately at camera focus (OpenPropBrowser's own
    /// target==null behavior), mirroring OpenUnitBrowser's equivalent
    /// browse-then-place path.</summary>
    private void OpenPropBrowserForPlacement()
    {
        _assetBrowser.Open(SC2UnitPropBrowser.AssetKind.Prop, selection =>
        {
            ArmPlacement(PlacementKind.Prop, "");
            _placementPropBlueprint = selection.BlueprintPath; // must be set AFTER ArmPlacement - it resets these fields
            _placementPropAsset = selection.Asset;
            _lastActionSummary = $"Click in the viewport to place {ShortBlueprint(selection.BlueprintPath)} — press Esc to stop.";
            RepaintHost?.Invoke();
        });
    }

    /// <summary>Batch "Replace selected" — picks one blueprint and swaps it
    /// onto every prop in <paramref name="targets"/>, same effect as running
    /// OpenPropBrowser(target) once per prop but as a single undo step and a
    /// single browser pick instead of one per prop. Position/rotation/scale
    /// are left untouched; only BlueprintPath (and the resulting preview
    /// model) changes.</summary>
    private void OpenPropBrowserForBatchReplace(List<SC2ObjProp> targets)
    {
        if (targets.Count == 0) return;
        _assetBrowser.Open(SC2UnitPropBrowser.AssetKind.Prop, selection =>
        {
            PushUndo();
            var asset = selection.Asset;
            foreach (var target in targets)
            {
                target.BlueprintPath = selection.BlueprintPath;
                if (target.AnchorToTerrain) ReprojectProp(target);
                SC2GameAssetScenePreview.TryShowProp(target.Id, asset, _assetBrowser.GameDataRoot, target.Position, target.Rotation, target.Scale, out _);
            }
            _lastActionSummary = $"Replaced {targets.Count} prop(s) with {ShortBlueprint(selection.BlueprintPath)}" +
                (selection.IsComplete ? "." : " (some referenced resources are unavailable).");
            MarkObjectsDirty();
            RepaintHost?.Invoke();
        });
    }

    /// <summary>SC2GameAssetScenePreview.SyncTransforms only updates the
    /// transform of previews that already exist - without this method,
    /// creating one would only ever happen as a side effect of manually
    /// reopening a SPECIFIC prop/unit's asset-picker panel (OpenPropBrowser/
    /// OpenUnitBrowser above), which would mean a freshly loaded ("Load Terrain")
    /// or imported ("Import Objects from terrain.bdf") map's props/units
    /// show no 3D model at all until each one is clicked open by hand,
    /// even when the game-asset catalog itself is already indexed.
    /// Called every frame alongside
    /// SyncTransforms; cheap when there's nothing to do (HasPropPreview/
    /// HasUnitPreview is a dictionary lookup, FindPropAsset/FindUnitAsset
    /// are O(1) once the catalog's loaded, and returns null harmlessly
    /// before that). EnsureIndexed also means this self-heals even if the
    /// user never manually opened the picker this session, as long as a
    /// game data root is already configured.
    ///
    /// Surfaced via _missingPreviewWarning in the Props/Units tabs so "why
    /// don't my models show up" is self-diagnosing instead of a silent
    /// no-op. Distinguishes two genuinely different failure classes: (a) the
    /// catalog lookup itself found nothing for this BlueprintPath/Type (no
    /// Game data root, still indexing, or the id/path truly isn't in the
    /// index), vs. (b) the catalog DID resolve a real SC2GameAsset but
    /// TryShowProp/TryShowUnit itself failed afterward (no LOD with a
    /// resolved SCM path, the SCM file no longer exists on disk, it parsed
    /// to zero triangles, or an exception while building the mesh/
    /// materials) — a configured,
    /// successfully (re)indexed Game data root where objects still show no
    /// model at all can only be class (b); silently treating it the
    /// same as "root not configured" would keep pointing the mapmaker at
    /// the wrong fix.</summary>
    private string _missingPreviewWarning;

    // Dedup bookkeeping so a persistently-unresolved prop/unit's catalog-miss
    // warning logs once (to a standalone Build & Run's own Player.log), not
    // every single frame this loop retries it - same reasoning as
    // SC2GameAssetScenePreview's own _loggedFailureKeys for the "found in
    // the catalog but failed to load" case.
    private readonly HashSet<int> _loggedUnresolvedPropIds = new();
    private readonly HashSet<int> _loggedUnresolvedUnitIds = new();

    private void SyncMissingAssetPreviews()
    {
        // Catalog indexing starts at map load (kicked
        // off below by EnsureIndexed's StartLoad, a background Task), but
        // without polling it here, models would still only appear after
        // manually opening the Prop/Unit Browser — because nothing else
        // consumes the completed background Task into _result/the lookup dictionaries except
        // SC2UnitPropBrowser.DrawModal (only called while that modal is
        // actually open). FindPropAsset/FindUnitAsset return null until
        // that consume step runs at least once. Poll it directly here too.
        _assetBrowser.PollLoadTask();
        _assetBrowser.EnsureIndexed();
        var hasRoot = !string.IsNullOrWhiteSpace(_assetBrowser.GameDataRoot) && Directory.Exists(_assetBrowser.GameDataRoot);
        var unresolvedProps = 0; // BlueprintPath not found in the catalog at all
        var unresolvedUnits = 0; // Type not found in the catalog at all
        var failedProps = 0; // found in the catalog, but TryShowProp itself failed
        var failedUnits = 0; // found in the catalog, but TryShowUnit itself failed
        string lastLoadError = null;
        foreach (var prop in Data.ObjProps)
        {
            if (string.IsNullOrEmpty(prop.BlueprintPath) || _hiddenPropIds.Contains(prop.Id) || SC2GameAssetScenePreview.HasPropPreview(prop.Id)) continue;
            var asset = _assetBrowser.FindPropAsset(prop.BlueprintPath);
            if (asset == null)
            {
                unresolvedProps++;
                if (_loggedUnresolvedPropIds.Add(prop.Id))
                    Debug.LogWarning($"[SC2 Placement] Prop #{prop.Id} blueprint '{prop.BlueprintPath}' at {prop.Position} not found in the indexed game data (Game data root: '{_assetBrowser.GameDataRoot}').");
                continue;
            }
            _loggedUnresolvedPropIds.Remove(prop.Id);
            if (!SC2GameAssetScenePreview.TryShowProp(prop.Id, asset, _assetBrowser.GameDataRoot, prop.Position, prop.Rotation, prop.Scale, out var err))
            {
                failedProps++;
                lastLoadError ??= err;
            }
        }
        foreach (var unit in Data.ObjUnits)
        {
            if (unit.IsGroup || string.IsNullOrEmpty(unit.Type) || _hiddenUnitIds.Contains(unit.Id) || SC2GameAssetScenePreview.HasUnitPreview(unit.Id)) continue;
            var asset = _assetBrowser.FindUnitAsset(unit.Type);
            if (asset == null)
            {
                unresolvedUnits++;
                if (_loggedUnresolvedUnitIds.Add(unit.Id))
                    Debug.LogWarning($"[SC2 Placement] Unit #{unit.Id} type '{unit.Type}' at {unit.Position} not found in the indexed game data (Game data root: '{_assetBrowser.GameDataRoot}').");
                continue;
            }
            _loggedUnresolvedUnitIds.Remove(unit.Id);
            if (!SC2GameAssetScenePreview.TryShowUnit(unit.Id, asset, _assetBrowser.GameDataRoot, unit.Position, Quaternion.Euler(unit.OrientationEuler), out var err))
            {
                failedUnits++;
                lastLoadError ??= err;
            }
        }

        if (!hasRoot && (unresolvedProps > 0 || unresolvedUnits > 0))
            _missingPreviewWarning = "⚠ No Game data root configured — prop/unit 3D models can't load. Open \"Browse prop/unit blueprints...\", paste the extracted gamedata.scd folder path, and press Index.";
        else if (hasRoot && _assetBrowser.IsIndexing)
            _missingPreviewWarning = "Indexing blueprints... models will appear once this finishes.";
        else if (failedProps > 0 || failedUnits > 0)
            _missingPreviewWarning = $"⚠ {failedProps} prop(s) / {failedUnits} unit(s) resolved to a catalog entry but failed to load a model" + (lastLoadError != null ? $": {lastLoadError}" : ".");
        else if (unresolvedProps > 0 || unresolvedUnits > 0)
            _missingPreviewWarning = $"⚠ {unresolvedProps} prop(s) / {unresolvedUnits} unit(s) reference a blueprint not found in the indexed game data — their models can't show.";
        else
            _missingPreviewWarning = null;

        // Independent of the per-object diagnosis above: even when every
        // mesh/material loaded correctly (no warning from the block above),
        // the actual draw call can still silently render nothing at all if
        // this device/graphics API doesn't support GPU instancing - see
        // SC2GameAssetScenePreview.UsedNonInstancedFallback's own doc
        // comment for why this specific failure mode has no other signal.
        if (SC2GameAssetScenePreview.UsedNonInstancedFallback)
        {
            var note = "ℹ GPU instancing isn't supported on this device — using a slower per-instance draw fallback so models still render.";
            _missingPreviewWarning = string.IsNullOrEmpty(_missingPreviewWarning) ? note : _missingPreviewWarning + " " + note;
        }
    }

    private void ExportPropsJson(List<SC2ObjProp> items)
    {
        var json = JsonUtility.ToJson(new PropsClipboard { Items = items }, true);
        GUIUtility.systemCopyBuffer = json;
        _lastActionSummary = $"Copied {items.Count} prop(s) to clipboard as JSON.";
    }

    private void ImportPropsJson()
    {
        try
        {
            var clip = JsonUtility.FromJson<PropsClipboard>(GUIUtility.systemCopyBuffer);
            if (clip?.Items == null || clip.Items.Count == 0) { _lastActionSummary = "Clipboard has no valid Props JSON."; return; }
            PushUndo();
            foreach (var p in clip.Items)
            {
                p.Id = Data.AllocateObjectId();
                Data.ObjProps.Add(p);
            }
            _lastActionSummary = $"Pasted {clip.Items.Count} prop(s) from clipboard.";
            MarkObjectsDirty();
        }
        catch (Exception ex)
        {
            _lastActionSummary = "Paste failed: " + ex.Message;
        }
    }

    // ── 4. Markers ───────────────────────────────────────────────────────
    private void DrawMarkers(float w)
    {
        if (_selMarkers.Count == 1)
        {
            var item = Data.ObjMarkers.FirstOrDefault(m => m.Id == _selMarkers.First());
            if (item != null) { DrawMarkerInspector(w, item); return; }
            _selMarkers.Clear();
        }
        DrawMarkersBrowser(w);
    }

    private void DrawMarkersBrowser(float w)
    {
        SC2EnvFieldGui.SectionLabel("Markers");
        // Type picker first, on-map list after — pick what you're about to
        // place before scrolling through what's already placed, rather than
        // the other way around.
        DrawMarkerPlacementPicker(w);
        GUILayout.Space(6);

        if (_hiddenMarkerIds.Count > 0)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label($"{_hiddenMarkerIds.Count} marker(s) hidden.");
            if (SC2EnvFieldGui.Button("Show All", GUILayout.Height(SC2UiScale.Pxf(18f)), GUILayout.Width(SC2UiScale.Pxf(90f)))) _hiddenMarkerIds.Clear();
            GUILayout.EndHorizontal();
        }
        DrawMarkersByTypePanel(w);
        GUILayout.Space(6);

        SC2EnvFieldGui.SectionLabel($"On map ({Data.ObjMarkers.Count})");
        _searchMarkers = SC2EnvFieldGui.TextField("Search (name/type)", _searchMarkers, "markers_search");
        DrawSystemFilterRow(w, ref _filterMarkers);

        var visible = Data.ObjMarkers.Where(m => !ArmyPositionRe.IsMatch(m.Name) &&
            (MatchSearch(m.Name, _searchMarkers) || MatchSearch(m.Type, _searchMarkers))).ToList();
        visible = _filterMarkers switch
        {
            SysFilter.Selected => visible.Where(m => _selMarkers.Contains(m.Id)).ToList(),
            SysFilter.Invalid => visible.Where(m => string.IsNullOrEmpty(m.Type) || string.IsNullOrEmpty(m.Name)).ToList(),
            SysFilter.Unanchored => visible.Where(m => !m.AnchorToTerrain).ToList(),
            _ => visible
        };
        var visibleIds = visible.Select(m => m.Id).ToList();

        DrawBatchBar(w, _selMarkers, new List<(string, Action)>
        {
            ("Anchor selected", () => { PushUndo(); foreach (var m in visible.Where(x => _selMarkers.Contains(x.Id))) { m.AnchorToTerrain = true; ReprojectMarker(m); } MarkObjectsDirty(); }),
            ("Duplicate w/ symmetry", () => { PushUndo(); foreach (var m in visible.Where(x => _selMarkers.Contains(x.Id)).ToList()) DuplicateMarkerWithSymmetry(m); }),
            ("Link selected (symmetry)", () => { PushUndo(); LinkMarkersWithSymmetry(visible.Where(x => _selMarkers.Contains(x.Id)).ToList()); }),
            ("Delete selected", () => { PushUndo(); DeleteMarkersById(new HashSet<int>(_selMarkers)); }),
            ("Copy JSON to clipboard", () => ExportMarkersJson(visible.Where(x => _selMarkers.Contains(x.Id)).ToList()))
        });

        if (_selMarkers.Count > 0)
        {
            _renamePrefixMarkers = SC2EnvFieldGui.TextField("Rename selected → prefix_001..", _renamePrefixMarkers, "markers_rename_prefix");
            if (SC2EnvFieldGui.Button("Apply rename sequence", GUILayout.Height(SC2UiScale.Pxf(18f))))
            {
                PushUndo();
                var n = 1;
                foreach (var m in visible.Where(x => _selMarkers.Contains(x.Id)))
                    RenameMarker(m, $"{_renamePrefixMarkers}_{n++:000}");
            }
        }

        _scrollB = GUILayout.BeginScrollView(_scrollB, GUILayout.Height(SC2UiScale.Pxf(280f)));
        foreach (var m in visible)
        {
            var isSel = _selMarkers.Contains(m.Id);
            var label = $"{(isSel ? "● " : "○ ")}{m.Name}   [{m.Type}]" + (m.AnchorToTerrain ? "" : "  [Y locked]") + (_hiddenMarkerIds.Contains(m.Id) ? "  [hidden]" : "");
            if (GUILayout.Button(TruncateForLabel(label, w - 8), GUILayout.Width(w - 8)))
                HandleRowClick(_selMarkers, visibleIds, m.Id, ref _lastClickedMarkerIdx);
        }
        GUILayout.EndScrollView();

        GUILayout.BeginHorizontal();
        if (SC2EnvFieldGui.Button("Add at camera focus", GUILayout.Height(SC2UiScale.Pxf(24f)), GUILayout.Width(w * 0.48f)))
        {
            var marker = BuildMarkerPlacementCandidate(GetSpawnPoint(), "Blank Marker");
            if (HasEquivalentPlacedMarker(marker))
                _lastActionSummary = "Marker not added: an equivalent marker already occupies that grid cell.";
            else
            {
                PushUndo();
                marker.Id = Data.AllocateObjectId();
                marker.Name = MakeUniqueMarkerName($"Marker_{Data.ObjMarkers.Count + 1}");
                Data.ObjMarkers.Add(marker);
                MarkObjectsDirty();
            }
        }
        if (SC2EnvFieldGui.Button("Paste from clipboard", GUILayout.Height(SC2UiScale.Pxf(24f)), GUILayout.Width(w * 0.48f)))
            ImportMarkersJson();
        GUILayout.EndHorizontal();
    }

    /// <summary>"By type" — select all/isolate/hide every marker of one
    /// type at once, previously missing (SelectAllMarkersOfType
    /// already existed but only as a single-marker inspector button, no way
    /// to reach it without first clicking one marker of that type on the
    /// map). Mirrors DrawPropsByBlueprintPanel's shape but with the Units
    /// tree's S/H/I compact-button convention (DrawGroupTreeNode) instead of
    /// Select/Hide/Delete, since "isolate" is
    /// the established pattern for it elsewhere in this file. Army
    /// start-position markers are excluded from grouping, same as the "On
    /// map" list above (ArmyPositionRe) — they all share Type="Blank
    /// Marker" with any other blank marker, and hiding/isolating that type
    /// would unexpectedly also affect army start positions.</summary>
    private void DrawMarkersByTypePanel(float w)
    {
        var groups = Data.ObjMarkers
            .Where(m => !ArmyPositionRe.IsMatch(m.Name) && !string.IsNullOrEmpty(m.Type))
            .GroupBy(m => m.Type, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (!SC2EnvFieldGui.Foldout(ref _markersByTypeExpanded, $"By type ({groups.Count})")) return;

        if (groups.Count == 0)
        {
            GUILayout.Label("No markers placed yet.");
            return;
        }

        _scrollMarkersByType = GUILayout.BeginScrollView(_scrollMarkersByType, GUILayout.Height(Mathf.Min(SC2UiScale.Pxf(220f), groups.Count * SC2UiScale.Pxf(26f) + SC2UiScale.Pxf(4f))));
        foreach (var group in groups)
        {
            var type = group.Key;
            var ids = group.Select(m => m.Id).ToList();
            var hiddenCount = ids.Count(id => _hiddenMarkerIds.Contains(id));
            var isFullyHidden = hiddenCount == ids.Count;

            GUILayout.BeginHorizontal();
            // This used to compute reserved space for the 3
            // S/H/I buttons in raw, unscaled pixels while the buttons
            // themselves are drawn at SC2UiScale.Pxf(24f) — at a high UI
            // scale setting the buttons grew but this estimate didn't,
            // so labelWidth came out too wide and the row overflowed/
            // overlapped. Wrapped in
            // Pxf(...) to match DrawGroupTreeNode's own identical
            // S/H/I/- row, which already did this correctly.
            var reserved = SC2UiScale.Pxf(3 * 26f + 10f);
            var labelWidth = Mathf.Max(50f, w - reserved);
            var label = $"{type} ({ids.Count})" + (hiddenCount > 0 ? $"  [{hiddenCount} hidden]" : "");
            GUILayout.Label(TruncateForLabel(label, labelWidth), GUILayout.Width(labelWidth));

            var prevBg = GUI.backgroundColor;
            GUI.backgroundColor = SmallActionButtonColor;
            if (SC2EnvFieldGui.Button("S", GUILayout.Width(SC2UiScale.Pxf(24f))))
                SelectAllMarkersOfType(type);
            GUI.backgroundColor = isFullyHidden ? HideActiveButtonColor : SmallActionButtonColor;
            if (SC2EnvFieldGui.Button("H", GUILayout.Width(SC2UiScale.Pxf(24f))))
            {
                if (isFullyHidden) foreach (var id in ids) _hiddenMarkerIds.Remove(id);
                else foreach (var id in ids) _hiddenMarkerIds.Add(id);
                _lastActionSummary = (isFullyHidden ? "Shown " : "Hid ") + $"{ids.Count} marker(s) of type '{type}'.";
            }
            GUI.backgroundColor = SmallActionButtonColor;
            if (SC2EnvFieldGui.Button("I", GUILayout.Width(SC2UiScale.Pxf(24f))))
            {
                var keepSet = new HashSet<int>(ids);
                _hiddenMarkerIds.Clear();
                foreach (var m in Data.ObjMarkers)
                    if (!ArmyPositionRe.IsMatch(m.Name) && !keepSet.Contains(m.Id)) _hiddenMarkerIds.Add(m.Id);
                _lastActionSummary = $"Isolated {ids.Count} marker(s) of type '{type}'.";
            }
            GUI.backgroundColor = prevBg;
            GUILayout.EndHorizontal();
        }
        GUILayout.EndScrollView();
    }

    // ── Marker category grid (per user reference: FAForever Map Editor's own
    // marker picker) — a compact 2-row icon grid replaces the old flat
    // scrolling list of 30 full-width template buttons. Categories with more
    // than one real template (Path/Defense/Expand/Rally/Other) collapse
    // behind one tile and expand a "Type" sub-list on click, exactly like
    // that reference groups its Path Node variants; single-template
    // categories (Blank/Mass/Hydro/Camera/Combat/Prot. Exp.) arm placement
    // immediately. This shell is deliberately GUILayout/GUI-only (never
    // EditorGUILayout) so it keeps working in a Build & Run player — the
    // reference's native dropdown is reproduced as a toggle + button list
    // instead of EditorGUILayout.Popup for that reason.
    private sealed class MarkerCategory
    {
        public string Key = "";
        public string Label = "";
        public string IconResource = "";
        public string[] TemplateNames = Array.Empty<string>();
        public bool IsPreset;
    }

    /// <summary>Expanded from 11 broad groups to one tile per
    /// covered marker type, using the real per-type icons
    /// (Resources/EditorIcons/
    /// Markers/&lt;key&gt;.png — see SC2MarkerTemplates.Template.IconKey). A
    /// single-TemplateNames tile still arms placement directly on click
    /// (DrawMarkerCategoryTile's existing behavior); "Preset" and the
    /// grouped "Other" tile (for the 4 types with no supplied asset) are
    /// unchanged from before.</summary>
    private static readonly MarkerCategory[] MarkerCategories =
    {
        new() { Key = "Blank", Label = "Blank", IconResource = "EditorIcons/Markers/blank_marker", TemplateNames = new[] { "Blank Marker" } },
        new() { Key = "Mass", Label = "Mass", IconResource = "EditorIcons/Markers/mass", TemplateNames = new[] { "Mass" } },
        new() { Key = "Camera", Label = "Camera", IconResource = "EditorIcons/Markers/camera_info", TemplateNames = new[] { "Camera Info" } },
        new() { Key = "Preset", Label = "Preset", IconResource = "EditorIcons/marker_spawn", IsPreset = true },
        new() { Key = "Combat", Label = "Combat", IconResource = "EditorIcons/Markers/combat_zone", TemplateNames = new[] { "Combat Zone" } },
        new() { Key = "Defense", Label = "Defense", IconResource = "EditorIcons/Markers/defensive_point", TemplateNames = new[] { "Defensive Point" } },
        new() { Key = "NavalDefense", Label = "Nav. Def.", IconResource = "EditorIcons/Markers/naval_defensive_point", TemplateNames = new[] { "Naval Defensive Point" } },
        new() { Key = "Expand", Label = "Expand", IconResource = "EditorIcons/Markers/expansion_area", TemplateNames = new[] { "Expansion Area" } },
        new() { Key = "ExpandLarge", Label = "Lg. Expand", IconResource = "EditorIcons/Markers/large_expansion_area", TemplateNames = new[] { "Large Expansion Area" } },
        new() { Key = "NavalArea", Label = "Naval Area", IconResource = "EditorIcons/Markers/naval_area", TemplateNames = new[] { "Naval Area" } },
        new() { Key = "ProtExp", Label = "Prot. Exp.", IconResource = "EditorIcons/Markers/protected_experimental_construction", TemplateNames = new[] { "Protected Experimental Construction" } },
        new() { Key = "Rally", Label = "Rally", IconResource = "EditorIcons/Markers/rally_point", TemplateNames = new[] { "Rally Point" } },
        new() { Key = "NavalRally", Label = "Nav. Rally", IconResource = "EditorIcons/Markers/naval_rally_point", TemplateNames = new[] { "Naval Rally Point" } },
        new() { Key = "Island", Label = "Island", IconResource = "EditorIcons/Markers/island", TemplateNames = new[] { "Island" } },
        new() { Key = "NavalExclude", Label = "Nav. Excl.", IconResource = "EditorIcons/Markers/naval_exclude", TemplateNames = new[] { "Naval Exclude" } },
        new() { Key = "NavalLink", Label = "Nav. Link", IconResource = "EditorIcons/Markers/naval_link", TemplateNames = new[] { "Naval Link" } },
        new() { Key = "Effect", Label = "Effect", IconResource = "EditorIcons/Markers/effect", TemplateNames = new[] { "Effect" } },
        new() { Key = "Weather", Label = "Weather", IconResource = "EditorIcons/Markers/weather_generator", TemplateNames = new[] { "Weather Generator" } },
        new() { Key = "Attack", Label = "Attack", IconResource = "EditorIcons/Markers/attack_marker", TemplateNames = new[] { "Attack Marker" } },
        new() { Key = "Transport", Label = "Transport", IconResource = "EditorIcons/Markers/transport_marker", TemplateNames = new[] { "Transport Marker" } },
        new() { Key = "Sound", Label = "Sound", IconResource = "EditorIcons/Markers/sound", TemplateNames = new[] { "Sound" } },
        new() { Key = "PathLand", Label = "Land Path", IconResource = "EditorIcons/Markers/land_path_node", TemplateNames = new[] { "Land Path Node" } },
        new() { Key = "PathWater", Label = "Water Path", IconResource = "EditorIcons/Markers/water_path_node", TemplateNames = new[] { "Water Path Node" } },
        new() { Key = "PathAir", Label = "Air Path", IconResource = "EditorIcons/Markers/air_path_node", TemplateNames = new[] { "Air Path Node" } },
        new() { Key = "PathAmphib", Label = "Amphib. Path", IconResource = "EditorIcons/Markers/amphibious_path_node", TemplateNames = new[] { "Amphibious Path Node" } },
        new() { Key = "PathAuto", Label = "Auto Path", IconResource = "EditorIcons/Markers/auto_path_node", TemplateNames = new[] { "Auto Path Node" } },
        new() { Key = "PathDefault", Label = "Def. Path", IconResource = "EditorIcons/Markers/default_path_node", TemplateNames = new[] { "Default Path Node" } },
        new() { Key = "Other", Label = "Other", IconResource = "EditorIcons/marker_other", TemplateNames = new[] { "Hydrocarbon", "Weather Definition", "Base Marker", "gpnav Playable Island" } },
    };

    private static readonly Dictionary<string, Texture2D> _markerIconCache = new();
    private static Texture2D LoadMarkerIcon(string resourcePath)
    {
        if (_markerIconCache.TryGetValue(resourcePath, out var cached)) return cached;
        var tex = Resources.Load<Texture2D>(resourcePath);
        _markerIconCache[resourcePath] = tex;
        return tex;
    }

    private static GUIStyle _markerCategoryLabelStyle;
    private static GUIStyle MarkerCategoryLabelStyle => _markerCategoryLabelStyle ??= new GUIStyle(GUI.skin.label)
    {
        alignment = TextAnchor.MiddleCenter, fontSize = 9, wordWrap = false
    };

    private string _markerCategoryOpen = "";

    /// <summary>Pick a type on the left panel, then
    /// place directly on the map by clicking; symmetry siblings are created
    /// automatically at placement time. Click-to-place stays armed —
    /// repeated clicks keep placing the same type until
    /// the user picks a different type or clicks "Stop placing".</summary>
    private void DrawMarkerPlacementPicker(float w)
    {
        var placingMarker = _placementKind == PlacementKind.Marker;
        var placingPreset = _placementKind == PlacementKind.MarkerPreset;
        if (placingMarker || placingPreset)
        {
            var label = placingPreset ? _placementPreset?.Name ?? _placementSubtype : _placementSubtype;
            if (SC2EnvFieldGui.Button($"Placing: {label} — click viewport, or Stop placing", GUILayout.Height(SC2UiScale.Pxf(24f)), GUILayout.Width(w)))
                DisarmPlacement();
            return;
        }

        SC2EnvFieldGui.SectionLabel("Place in viewport");
        const int columns = 4;
        var cellSize = Mathf.Floor((w - 8) / columns);
        var rows = Mathf.CeilToInt(MarkerCategories.Length / (float)columns);
        for (var row = 0; row < rows; row++)
        {
            GUILayout.BeginHorizontal();
            for (var col = 0; col < columns; col++)
            {
                var index = row * columns + col;
                if (index >= MarkerCategories.Length) { GUILayout.FlexibleSpace(); continue; }
                DrawMarkerCategoryTile(MarkerCategories[index], cellSize);
            }
            GUILayout.EndHorizontal();
        }

        var open = Array.Find(MarkerCategories, c => c.Key == _markerCategoryOpen);
        if (open == null) return;

        if (open.IsPreset)
        {
            foreach (var preset in SC2MarkerPresets.All)
                if (SC2EnvFieldGui.Button(preset.Name, GUILayout.Height(SC2UiScale.Pxf(20f))))
                {
                    ArmPlacement(PlacementKind.MarkerPreset, preset.Name);
                    _placementPreset = preset; // must be set AFTER ArmPlacement - it resets this field
                    _markerCategoryOpen = "";
                }
            return;
        }

        if (open.TemplateNames.Length == 1) return; // single-template categories arm on tile click, no sub-list

        GUILayout.Label("Type:");
        foreach (var name in open.TemplateNames)
            if (SC2EnvFieldGui.Button(name, GUILayout.Height(SC2UiScale.Pxf(18f)), GUILayout.Width(w - 24)))
            {
                ArmPlacement(PlacementKind.Marker, name);
                _markerCategoryOpen = "";
            }
    }

    private static GUIStyle _markerTileButtonStyle;
    private static GUIStyle MarkerTileButtonStyle => _markerTileButtonStyle ??= new GUIStyle(GUI.skin.button) { padding = new RectOffset(0, 0, 0, 0) };

    /// <summary>The icon should fill the tile
    /// button's full clickable area instead of sitting shrunk inside the
    /// button style's default content padding (GUILayout.Button(GUIContent)
    /// insets the image by the style's padding, leaving a visible dark
    /// border around every icon). Draws an empty-content button for the
    /// click/visual-state handling, then paints the icon directly over the
    /// button's own rect with only a tiny 2px inset.</summary>
    private void DrawMarkerCategoryTile(MarkerCategory cat, float size)
    {
        var isOpen = _markerCategoryOpen == cat.Key;
        var oldColor = GUI.backgroundColor;
        if (isOpen) GUI.backgroundColor = new Color(0.35f, 0.55f, 0.95f);

        GUILayout.BeginVertical(GUILayout.Width(size));
        var icon = LoadMarkerIcon(cat.IconResource);
        var clicked = GUILayout.Button(GUIContent.none, MarkerTileButtonStyle, GUILayout.Width(size - 4), GUILayout.Height(size - 4));
        var rect = GUILayoutUtility.GetLastRect();
        if (Event.current.type == EventType.Repaint)
        {
            if (icon != null)
            {
                const float pad = 2f;
                GUI.DrawTexture(new Rect(rect.x + pad, rect.y + pad, rect.width - pad * 2, rect.height - pad * 2), icon, ScaleMode.ScaleToFit);
            }
            else
            {
                GUI.Label(rect, cat.Label.Substring(0, Mathf.Min(2, cat.Label.Length)), MarkerCategoryLabelStyle);
            }
        }
        if (clicked)
        {
            if (!cat.IsPreset && cat.TemplateNames.Length == 1)
            {
                ArmPlacement(PlacementKind.Marker, cat.TemplateNames[0]);
                _markerCategoryOpen = "";
            }
            else
            {
                _markerCategoryOpen = isOpen ? "" : cat.Key;
            }
        }
        GUILayout.Label(cat.Label, MarkerCategoryLabelStyle, GUILayout.Width(size));
        GUILayout.EndVertical();

        GUI.backgroundColor = oldColor;
    }

    private void PlaceMarkerPreset(SC2MarkerPresets.Preset preset, Vector3 anchor)
    {
        var placed = 0;
        var skipped = 0;
        foreach (var entry in preset.Entries)
        {
            var marker = BuildMarkerPlacementCandidate(anchor + entry.Offset, entry.TemplateName);
            if (HasEquivalentPlacedMarker(marker))
            {
                skipped++;
                continue;
            }
            marker.Id = Data.AllocateObjectId();
            marker.Name = MakeUniqueMarkerName($"{entry.TemplateName}_{Data.ObjMarkers.Count + 1}");
            Data.ObjMarkers.Add(marker);
            placed++;
            // Marker presets must link with symmetry too, matching every
            // other placement function (PlaceMarkerAt/PlacePropAt/
            // PlaceUnitAt/PlaceArmyAt). Called per-entry, one marker at a time, exactly
            // like PlaceMarkerAt — NOT once with the whole preset's markers
            // together, since LinkMarkersWithSymmetry(selected) with
            // selected.Count > 1 treats the list as objects that should be
            // linked TO EACH OTHER (the "Link selected (symmetry)"
            // multi-select use case), which would wrongly treat a preset's
            // own distinct corner markers as each other's symmetric slots
            // instead of each getting its own independent mirrored sibling.
            if (Data.Symmetry != SymmetryMode.None)
                LinkMarkersWithSymmetry(new List<SC2ObjMarker> { marker });
        }
        _lastActionSummary = $"Placed preset '{preset.Name}' ({placed} marker(s), {skipped} duplicate(s) skipped).";
        if (placed > 0) MarkObjectsDirty();
    }

    /// <summary>Preview for PlacementKind.MarkerPreset — mirrors
    /// DrawPlacementPreview's single-type preview (translucent 2D glyph +
    /// real 3D ghost model per landing spot), but built per-entry since a
    /// preset places several markers, potentially of different types, each
    /// at its own fixed offset from the cursor rather than one shared
    /// symmetry-mirrored point. Uses the exact same SnapXZ(anchor +
    /// entry.Offset) computation PlaceMarkerPreset itself uses, so the
    /// preview never drifts from where a click would actually place.
    ///
    /// Each entry's ghost is ALSO mirrored across the active symmetry mode
    /// (there used to be no preview for symmetric objects) — matching
    /// PlaceMarkerPreset, which links each entry with symmetry
    /// individually, not DrawPlacementPreview's single shared
    /// GetSymmetricPositions(hit) call, since a preset already has several
    /// independent anchor points to begin with.</summary>
    private void DrawMarkerPresetPreview(Camera camera, Rect viewportRect, Vector3 anchor)
    {
        if (_placementPreset == null) { SC2MarkerScenePreview.ClearGhosts(); return; }

        var ghostEntries = new List<(string key, Vector3 position, Vector3 orientationEuler)>();
        foreach (var entry in _placementPreset.Entries)
        {
            var tpl = SC2MarkerTemplates.Find(entry.TemplateName);
            var basePos = SnapXZ(anchor + entry.Offset);
            var color = tpl != null ? SC2MarkerTemplates.HexToColor(tpl.ColorHex) : new Color(.8f, .8f, .8f, 1f);
            color.a = 0.5f;

            var positions = Data.Symmetry == SymmetryMode.None
                ? new List<Vector3> { basePos }
                : GetUniqueSnappedMarkerSymmetryPositions(basePos);
            var probe = BuildMarkerPlacementCandidate(basePos, entry.TemplateName);
            foreach (var rawPos in positions)
            {
                var pos = SnapXZ(rawPos);
                probe.Position = pos;
                if (HasEquivalentPlacedMarker(probe)) continue;
                if (TryWorldToGui(camera, viewportRect, pos, out var gui))
                    DrawViewportGlyph(gui, color, ViewportObjectKind.Marker);
                if (!string.IsNullOrEmpty(tpl?.IconKey))
                    ghostEntries.Add((tpl.IconKey, pos, Vector3.zero));
            }
        }
        SC2MarkerScenePreview.SyncGhostsMulti(ghostEntries);
    }

    /// <summary>"Select all objects of one kind" (Markers) — same purpose
    /// as SelectAllPropsWithBlueprint, grouped by marker Type instead of
    /// blueprint path.</summary>
    private void SelectAllMarkersOfType(string type)
    {
        _selMarkers.Clear();
        foreach (var other in Data.ObjMarkers.Where(x => string.Equals(x.Type, type, StringComparison.OrdinalIgnoreCase)))
            _selMarkers.Add(other.Id);
        _lastActionSummary = $"Selected {_selMarkers.Count} marker(s) of type '{type}'.";
        ReportStatus?.Invoke(_lastActionSummary);
    }

    private void DrawMarkerInspector(float w, SC2ObjMarker m)
    {
        if (SC2EnvFieldGui.Button("← Back to list", GUILayout.Height(SC2UiScale.Pxf(20f)))) { _selMarkers.Clear(); return; }
        SC2EnvFieldGui.SectionLabel(m.Name);

        if (!string.IsNullOrEmpty(m.Type) && SC2EnvFieldGui.Button($"Select all of type '{m.Type}'", GUILayout.Height(SC2UiScale.Pxf(20f))))
            SelectAllMarkersOfType(m.Type);

        SC2EnvFieldGui.SectionLabel("Template");
        var tpl = SC2MarkerTemplates.Find(m.Type);
        GUILayout.BeginHorizontal();
        GUILayout.Label(TruncateForLabel(tpl != null ? tpl.Name : "(custom / no template match)", w * 0.6f), GUILayout.Width(w * 0.6f));
        if (SC2EnvFieldGui.Button("Pick...", GUILayout.Width(SC2UiScale.Pxf(60f)))) _markerTemplatePicking = !_markerTemplatePicking;
        GUILayout.EndHorizontal();
        if (_markerTemplatePicking)
        {
            _scrollC = GUILayout.BeginScrollView(_scrollC, GUILayout.Height(SC2UiScale.Pxf(140f)));
            foreach (var t in SC2MarkerTemplates.All)
                if (SC2EnvFieldGui.Button(t.Name, GUILayout.Height(SC2UiScale.Pxf(18f))))
                {
                    var probe = new SC2ObjMarker { Name = m.Name, Type = t.Name, Prop = t.Prop, Position = m.Position };
                    if (HasEquivalentPlacedMarker(probe, m.Id))
                        _lastActionSummary = "Template not applied: it would duplicate another marker at this position.";
                    else
                    {
                        PushUndo();
                        ApplyMarkerTemplate(m, t);
                        MarkObjectsDirty();
                    }
                    _markerTemplatePicking = false;
                }
            GUILayout.EndScrollView();
        }

        var editedMarkerName = SC2EnvFieldGui.TextField("Name", m.Name, "mki_name");
        if (!string.Equals(m.Name, editedMarkerName, StringComparison.Ordinal)) RenameMarker(m, editedMarkerName);
        // Every generated army start-position name is already the plain
        // "ARMY_N" form (NextArmyName) — a manual rename that leaves it
        // still matching ArmyPositionRe (so it keeps exporting as a start
        // position) but not the strict clean form only makes sense on a
        // campaign map, per the real vanilla corpus.
        if (ArmyPositionRe.IsMatch(m.Name) && !ArmyCleanNameRe.IsMatch(m.Name) && !IsCampaignMap())
            SC2TerrainShell.DrawUnifiedNote($"⚠ '{m.Name}' isn't the plain \"ARMY_N\" form real skirmish/multiplayer start positions use — this is only valid on a campaign map (Scenario tab > Type).");
        var editedMarkerType = SC2EnvFieldGui.TextField("Type (advanced — prefer template above)", m.Type, "mki_type");
        if (!string.Equals(m.Type, editedMarkerType, StringComparison.Ordinal))
            { if (TrySetMarkerPlacementKind(m, editedMarkerType, m.Prop)) MarkObjectsDirty(); }

        var prevAnchor = m.AnchorToTerrain;
        var prevOffset = m.HeightOffset;
        DrawAnchorBlock(ref m.AnchorToTerrain, ref m.HeightOffset, m.Position.y,
            () => { SetMarkerPosition(m, ReprojectedMarker(m)); MarkObjectsDirty(); },
            y => { SetMarkerPosition(m, new Vector3(m.Position.x, y, m.Position.z)); MarkObjectsDirty(); });
        if (m.AnchorToTerrain != prevAnchor || m.HeightOffset != prevOffset) MarkObjectsDirty();
        var editedMarkerPos = SC2EnvFieldGui.Vector3Field("Position", m.Position, "mki_pos");
        if (editedMarkerPos != m.Position) { SetMarkerPosition(m, editedMarkerPos); MarkObjectsDirty(); }
        var prevOrientation = m.OrientationEuler;
        m.OrientationEuler = SC2EnvFieldGui.Vector3Field("Orientation", m.OrientationEuler, "mki_rot");
        if (m.OrientationEuler != prevOrientation) MarkObjectsDirty();

        var col = SC2MarkerTemplates.HexToColor(m.ColorHex);
        var newCol = SC2EnvFieldGui.ColorField("Color", col, "mki_col");
        if (newCol != col)
        {
            var c32 = (Color32)newCol;
            m.ColorHex = $"{c32.a:x2}{c32.r:x2}{c32.g:x2}{c32.b:x2}";
            MarkObjectsDirty();
        }
        var editedMarkerProp = SC2EnvFieldGui.TextField("Prop", m.Prop, "mki_prop");
        if (!string.Equals(m.Prop, editedMarkerProp, StringComparison.Ordinal))
            { if (TrySetMarkerPlacementKind(m, m.Type, editedMarkerProp)) MarkObjectsDirty(); }

        SC2EnvFieldGui.SectionLabel("Export destination");
        var isArmyPos = ArmyPositionRe.IsMatch(m.Name);
        GUILayout.Label($"mapobjs.bdf: {(isArmyPos ? "no (start-position marker)" : "yes")}");
        GUILayout.Label("_save.lua Markers: yes");
        GUILayout.Label($"_scenario.lua StartPositions: {(isArmyPos ? "yes" : "no")}");
        if (string.Equals(m.Type, "Auto Path Node", StringComparison.Ordinal))
            SC2EnvFieldGui.WrapLabel($"Resolves at export to: {SC2ObjectsExport.ResolveEffectiveType(Data, m, Session?.Document?.Environment?.WorkingHeader)}", w);

        if (SC2EnvFieldGui.Foldout(ref _markerAdvancedExpanded, $"Advanced / raw fields ({m.ExtraFields.Count})"))
        {
            for (var i = 0; i < m.ExtraFields.Count; i++)
            {
                var f = m.ExtraFields[i];
                GUILayout.BeginHorizontal();
                var prevKey = f.Key;
                var prevVal = f.RawValue;
                f.Key = SC2EnvFieldGui.TextField("Key", f.Key, "mki_ek_" + i);
                f.RawValue = SC2EnvFieldGui.TextField("Value", f.RawValue, "mki_ev_" + i);
                m.ExtraFields[i] = f;
                if (f.Key != prevKey || f.RawValue != prevVal) MarkObjectsDirty();
                if (SC2EnvFieldGui.Button("X", GUILayout.Width(SC2UiScale.Pxf(24f)))) { m.ExtraFields.RemoveAt(i); MarkObjectsDirty(); GUILayout.EndHorizontal(); break; }
                GUILayout.EndHorizontal();
            }
            if (SC2EnvFieldGui.Button("Add raw field", GUILayout.Height(SC2UiScale.Pxf(18f))))
            {
                m.ExtraFields.Add(new SC2LuaField { Key = "key", RawValue = "STRING( '' )" });
                MarkObjectsDirty();
            }
        }

        var markerSymGroup = FindSymmetryGroup(m.Id);
        SC2EnvFieldGui.SectionLabel(markerSymGroup != null ? $"Symmetry: linked ({markerSymGroup.Members.Count} members)" : "Symmetry: not linked");
        if (markerSymGroup == null)
        {
            if (SC2EnvFieldGui.Button("Link with symmetry", GUILayout.Height(SC2UiScale.Pxf(20f)))) { PushUndo(); LinkMarkersWithSymmetry(new List<SC2ObjMarker> { m }); }
        }
        else if (SC2EnvFieldGui.Button("Unlink symmetry", GUILayout.Height(SC2UiScale.Pxf(20f)))) { PushUndo(); UnlinkSymmetryGroup(m.Id); }

        GUILayout.Space(6);
        if (SC2EnvFieldGui.Button("Duplicate with symmetry", GUILayout.Height(SC2UiScale.Pxf(20f)))) { PushUndo(); DuplicateMarkerWithSymmetry(m); }
        if (SC2EnvFieldGui.Button("Focus camera", GUILayout.Height(SC2UiScale.Pxf(20f)))) RequestCameraFocus?.Invoke(m.Position);
        if (SC2EnvFieldGui.Button("Delete", GUILayout.Height(SC2UiScale.Pxf(20f)))) { PushUndo(); DeleteMarkersById(new HashSet<int> { m.Id }); }
    }

    private Vector3 ReprojectedMarker(SC2ObjMarker m) => new(m.Position.x, SampleHeight(m.Position.x, m.Position.z) + m.HeightOffset, m.Position.z);

    private bool TrySetMarkerPlacementKind(SC2ObjMarker marker, string type, string prop)
    {
        var probe = new SC2ObjMarker
        {
            Name = marker.Name,
            Type = type ?? "",
            Prop = prop ?? "",
            Position = marker.Position
        };
        if (HasEquivalentPlacedMarker(probe, marker.Id))
        {
            _lastActionSummary = "Marker fields not changed: they would duplicate another marker at this position.";
            return false;
        }

        PushUndo();
        marker.Type = probe.Type;
        marker.Prop = probe.Prop;
        return true;
    }

    private void ApplyMarkerTemplate(SC2ObjMarker m, SC2MarkerTemplates.Template t)
    {
        m.Type = t.Name;
        m.Prop = t.Prop;
        m.ColorHex = t.ColorHex;
        var extras = new List<SC2LuaField>();
        if (t.Resource)
        {
            extras.Add(new SC2LuaField { Key = "resource", RawValue = "BOOLEAN( true )" });
            extras.Add(new SC2LuaField { Key = "amount", RawValue = $"FLOAT( {t.Amount:0.##} )" });
            extras.Add(new SC2LuaField { Key = "size", RawValue = $"FLOAT( {t.Size:0.##} )" });
        }
        if (t.Hint) extras.Add(new SC2LuaField { Key = "hint", RawValue = "BOOLEAN( true )" });
        if (!string.IsNullOrEmpty(t.Graph)) extras.Add(new SC2LuaField { Key = "graph", RawValue = $"STRING( '{t.Graph}' )" });
        m.ExtraFields = extras;
    }

    /// <summary>Deleting a marker/prop that's linked into a symmetry group
    /// (via "Link with symmetry", "Duplicate w/ symmetry," or auto-linked
    /// click-to-place) must not leave its symmetric siblings behind with the
    /// group still referencing the now-deleted object's id. Every deletion path
    /// (hover Del-key, "Delete selected," the per-object Delete button)
    /// funnels through DeleteMarkersById/DeletePropsById, so expanding the
    /// id set here once fixes all of them. Groups touched by the expansion
    /// are removed outright (their members are all being deleted together,
    /// so there's nothing left to keep the group linked for) — mirrors
    /// UnlinkSymmetryGroup's own "remove the group, not just a member" shape.</summary>
    private HashSet<int> ExpandWithSymmetricSiblings(IEnumerable<int> ids)
    {
        var result = new HashSet<int>(ids);
        var groupsToRemove = Data.ObjSymmetryGroups
            .Where(g => g.Members.Any(m => result.Contains(m.ObjectId)))
            .ToList();
        foreach (var group in groupsToRemove)
        {
            foreach (var member in group.Members) result.Add(member.ObjectId);
            Data.ObjSymmetryGroups.Remove(group);
        }
        return result;
    }

    private void DeleteMarkersById(HashSet<int> markerIds)
    {
        if (markerIds == null || markerIds.Count == 0) return;
        markerIds = ExpandWithSymmetricSiblings(markerIds);
        var removedNames = new HashSet<string>(Data.ObjMarkers.Where(m => markerIds.Contains(m.Id)).Select(m => m.Name), StringComparer.OrdinalIgnoreCase);
        Data.ObjMarkers.RemoveAll(m => markerIds.Contains(m.Id));
        foreach (var graph in Data.ObjGraphs) SC2ObjectGraphRefs.RemoveEdgesReferencing(graph, markerIds);
        foreach (var chain in Data.ObjChains) chain.MarkerNames.RemoveAll(name => removedNames.Contains(name));
        _selMarkers.ExceptWith(markerIds);
        _lastActionSummary = $"Deleted {removedNames.Count} marker(s), including their graph/chain references and symmetric siblings.";
        MarkObjectsDirty();
    }

    private void DeletePropsById(HashSet<int> propIds)
    {
        if (propIds == null || propIds.Count == 0) return;
        propIds = ExpandWithSymmetricSiblings(propIds);
        var removed = Data.ObjProps.RemoveAll(p => propIds.Contains(p.Id));
        _selProps.ExceptWith(propIds);
        _lastActionSummary = $"Deleted {removed} prop(s), including symmetric siblings.";
        MarkObjectsDirty();
    }

    /// <summary>Same symmetric-sibling expansion as DeleteMarkersById/
    /// DeletePropsById, for units. Removes by id (not pre-computed index —
    /// RemoveUnitSubtree shifts indices as it goes) via the same subtree
    /// removal a single unit's own "Delete" button already uses.</summary>
    private void DeleteUnitsById(HashSet<int> unitIds)
    {
        if (unitIds == null || unitIds.Count == 0) return;
        unitIds = ExpandWithSymmetricSiblings(unitIds);
        var removed = 0;
        foreach (var id in unitIds.ToList())
        {
            var idx = Data.ObjUnits.FindIndex(u => u.Id == id);
            if (idx < 0) continue;
            RemoveUnitSubtree(idx);
            removed++;
        }
        _selUnits.ExceptWith(unitIds);
        _lastActionSummary = $"Deleted {removed} unit(s), including symmetric siblings.";
        MarkObjectsDirty();
    }

    private void RenameMarker(SC2ObjMarker marker, string newName)
    {
        if (marker == null || string.Equals(marker.Name, newName, StringComparison.Ordinal)) return;
        if (!string.IsNullOrEmpty(newName) && Data.ObjMarkers.Any(other => other.Id != marker.Id &&
            string.Equals(other.Name, newName, StringComparison.OrdinalIgnoreCase)))
        {
            _lastActionSummary = $"Marker not renamed: '{newName}' already exists.";
            return;
        }
        var previousName = marker.Name;
        marker.Name = newName;
        RenameMarkerReferences(previousName, newName);
        MarkObjectsDirty();
    }

    private void RenameMarkerReferences(string previousName, string newName)
    {
        if (string.IsNullOrEmpty(previousName) || string.Equals(previousName, newName, StringComparison.OrdinalIgnoreCase)) return;
        foreach (var chain in Data.ObjChains)
            for (var i = 0; i < chain.MarkerNames.Count; i++)
                if (string.Equals(chain.MarkerNames[i], previousName, StringComparison.OrdinalIgnoreCase))
                    chain.MarkerNames[i] = newName;
    }

    private void ExportMarkersJson(List<SC2ObjMarker> items)
    {
        var json = JsonUtility.ToJson(new MarkersClipboard { Items = items }, true);
        GUIUtility.systemCopyBuffer = json;
        _lastActionSummary = $"Copied {items.Count} marker(s) to clipboard as JSON.";
    }

    private void ImportMarkersJson()
    {
        try
        {
            var clip = JsonUtility.FromJson<MarkersClipboard>(GUIUtility.systemCopyBuffer);
            if (clip?.Items == null || clip.Items.Count == 0) { _lastActionSummary = "Clipboard has no valid Markers JSON."; return; }
            var accepted = new List<SC2ObjMarker>();
            var names = new HashSet<string>(Data.ObjMarkers.Where(m => !string.IsNullOrEmpty(m.Name)).Select(m => m.Name), StringComparer.OrdinalIgnoreCase);
            foreach (var marker in clip.Items)
            {
                marker.Position = SnapXZ(marker.Position);
                var duplicateName = !string.IsNullOrEmpty(marker.Name) && names.Contains(marker.Name);
                var duplicatePlacement = Data.ObjMarkers.Any(existing => SC2MarkerDeduplication.SamePlacement(existing, marker)) ||
                                         accepted.Any(existing => SC2MarkerDeduplication.SamePlacement(existing, marker));
                if (duplicateName || duplicatePlacement) continue;
                accepted.Add(marker);
                if (!string.IsNullOrEmpty(marker.Name)) names.Add(marker.Name);
            }
            if (accepted.Count > 0)
            {
                PushUndo();
                foreach (var marker in accepted)
                {
                    marker.Id = Data.AllocateObjectId();
                    Data.ObjMarkers.Add(marker);
                }
                MarkObjectsDirty();
            }
            _lastActionSummary = $"Pasted {accepted.Count} marker(s); skipped {clip.Items.Count - accepted.Count} duplicate(s).";
        }
        catch (Exception ex)
        {
            _lastActionSummary = "Paste failed: " + ex.Message;
        }
    }

    // ── 5. Paths (graphs) ───────────────────────────────────────────────
    private static readonly string[] GraphKindsLandAir = { "Land", "Air" };
    private static readonly string[] GraphKindsAll = { "Land", "Air", "Naval", "Amphibious" };

    /// <summary>Land+Air always; +Naval+Amphibious only when the map has
    /// playable water — same Data.PlayableWater signal already used for
    /// Costs' 3-vs-5 block profile, reused here rather than a second,
    /// possibly-inconsistent detection.</summary>
    private static string[] RequiredGraphKinds(bool playableWater) => playableWater ? GraphKindsAll : GraphKindsLandAir;

    /// <summary>Silently creates whichever required graph(s) don't exist yet
    /// (no undo entry — this is self-healing structure, not a user edit,
    /// same treatment as Data.EnsureObjectIds()). Never removes anything —
    /// a graph whose kind falls outside the current required set (stale
    /// duplicates, or Naval/Amphibious left over after playable water is
    /// turned back off) stays as real data the user can review/delete
    /// manually rather than being silently destroyed.</summary>
    private void EnsureRequiredGraphs(string[] required)
    {
        foreach (var kind in required)
        {
            if (Data.ObjGraphs.Any(g => string.Equals(g.GraphKind, kind, StringComparison.OrdinalIgnoreCase))) continue;
            Data.ObjGraphs.Add(new SC2ObjGraph { Id = Data.AllocateObjectId(), Name = kind, GraphKind = kind });
        }
    }

    private void DrawPaths(float w)
    {
        SC2EnvFieldGui.SectionLabel("Path Graphs (multiplayer AI navigation)");
        var required = RequiredGraphKinds(Data.PlayableWater);
        SC2EnvFieldGui.WrapLabel(Data.PlayableWater
            ? "4 graphs (map has playable water). Switch to 2 via Costs → \"3 Blocks (Land)\"."
            : "2 graphs (no playable water). Switch to 4 via Costs → \"5 Blocks (+ Water)\".", w);
        EnsureRequiredGraphs(required);

        var hasExtra = false;
        foreach (var g in Data.ObjGraphs)
        {
            var isRequired = required.Contains(g.GraphKind, StringComparer.OrdinalIgnoreCase);
            hasExtra |= !isRequired;
            GUILayout.BeginHorizontal();
            var rowWidth = isRequired ? w - 8f : w * 0.7f;
            var sel = GUILayout.Toggle(_selectedGraphId == g.Id, TruncateForLabel($"{g.Name} [{g.GraphKind}]", rowWidth), GUILayout.Width(rowWidth));
            if (sel) _selectedGraphId = g.Id;
            // The required set (exactly 2 or 4, one per kind) is structural,
            // not a user-managed list — no delete for those rows. Only
            // extras outside the current required set can be removed.
            if (!isRequired && SC2EnvFieldGui.Button("X", GUILayout.Width(SC2UiScale.Pxf(24f))))
            {
                PushUndo();
                Data.ObjGraphs.Remove(g);
                if (_selectedGraphId == g.Id) _selectedGraphId = 0;
                MarkObjectsDirty();
                GUILayout.EndHorizontal();
                break;
            }
            GUILayout.EndHorizontal();
        }
        if (hasExtra)
            SC2EnvFieldGui.WrapLabel("⚠ Graph(s) above aren't part of the required set (Land+Air, +Naval+Amphibious if the map has playable water) — remove with X.", w);

        var g2 = Data.ObjGraphs.FirstOrDefault(x => x.Id == _selectedGraphId);
        if (g2 == null) return;

        GUILayout.Space(6);
        SC2EnvFieldGui.SectionLabel($"{g2.Name} [{g2.GraphKind}]");

        SC2EnvFieldGui.SectionLabel($"Edges ({SC2ObjectGraphRefs.Count(g2)})");
        var edges = SC2ObjectGraphRefs.Enumerate(g2).ToList();
        for (var i = 0; i < edges.Count; i++)
        {
            GUILayout.BeginHorizontal();
            var (a, b) = edges[i];
            var nameA = SC2ObjectGraphRefs.TryGetMarker(Data, a, out var markerA) ? markerA.Name : "?(missing)";
            var nameB = SC2ObjectGraphRefs.TryGetMarker(Data, b, out var markerB) ? markerB.Name : "?(missing)";
            GUILayout.Label(TruncateForLabel($"{nameA} — {nameB}", w * 0.7f), GUILayout.Width(w * 0.7f));
            if (SC2EnvFieldGui.Button("X", GUILayout.Width(SC2UiScale.Pxf(24f))))
            {
                PushUndo();
                SC2ObjectGraphRefs.RemoveAt(g2, i);
                MarkObjectsDirty();
                GUILayout.EndHorizontal();
                break;
            }
            GUILayout.EndHorizontal();
        }

        var dup = CountDuplicateEdges(g2);
        var dangling = CountDanglingEdges(g2);
        if (dup > 0 || dangling > 0)
            SC2EnvFieldGui.WrapLabel($"⚠ {dup} duplicate edge(s), {dangling} dangling reference(s).", w);

        // Lets one graph's edges be reused as the
        // starting point for another — the real workflow is building Land
        // first (accounting for every real obstacle), then copying it onto
        // Air and connecting whatever was only blocked for ground units.
        // Edges reference marker Ids directly (SC2ObjectGraphRefs), and
        // markers are shared across graphs, so "copy" is just adding the
        // source graph's (a,b) pairs into this one — no new nodes needed,
        // duplicates already present here are skipped.
        var otherGraphs = Data.ObjGraphs.Where(x => x.Id != g2.Id).ToList();
        if (otherGraphs.Count > 0)
        {
            GUILayout.Space(6);
            SC2EnvFieldGui.SectionLabel("Copy edges from another graph");
            SC2EnvFieldGui.WrapLabel("Adds every edge from the chosen graph into this one (build Land accounting for obstacles, copy it onto Air, then connect whatever was only blocked for ground units).", w);
            GUILayout.BeginHorizontal();
            foreach (var other in otherGraphs)
            {
                if (SC2EnvFieldGui.Button($"From {other.GraphKind}", GUILayout.Height(SC2UiScale.Pxf(22f)), GUILayout.Width(w / otherGraphs.Count - 4f)))
                {
                    PushUndo();
                    var existingPairs = new HashSet<(int, int)>();
                    foreach (var (ea, eb) in SC2ObjectGraphRefs.Enumerate(g2))
                        existingPairs.Add(ea < eb ? (ea, eb) : (eb, ea));
                    var copied = 0;
                    foreach (var (a, b) in SC2ObjectGraphRefs.Enumerate(other))
                    {
                        var key = a < b ? (a, b) : (b, a);
                        if (!existingPairs.Add(key)) continue;
                        SC2ObjectGraphRefs.AddEdge(g2, a, b);
                        copied++;
                    }
                    _lastActionSummary = $"Copied {copied} new edge(s) from {other.GraphKind} into {g2.GraphKind}.";
                    if (copied > 0) MarkObjectsDirty();
                }
            }
            GUILayout.EndHorizontal();
        }

        if (_selMarkers.Count >= 2)
        {
            GUILayout.Space(6);
            SC2EnvFieldGui.SectionLabel($"Selected markers ({_selMarkers.Count}) — from the Markers tab");
            SC2EnvFieldGui.WrapLabel("Connect links every selected pair exactly once (2 selected → 1 edge, 3 → a full triangle, etc.) — the same \"select the group you mean to join\" model as FAF's map editor.", w);
            GUILayout.BeginHorizontal();
            if (SC2EnvFieldGui.Button("Connect selected (mesh)", GUILayout.Height(SC2UiScale.Pxf(22f)), GUILayout.Width(w * 0.48f)))
            {
                PushUndo();
                ConnectSelectedMarkersMesh(g2);
            }
            if (SC2EnvFieldGui.Button("Disconnect selected", GUILayout.Height(SC2UiScale.Pxf(22f)), GUILayout.Width(w * 0.48f)))
            {
                PushUndo();
                DisconnectSelectedMarkers(g2);
            }
            GUILayout.EndHorizontal();
        }

        GUILayout.Space(6);
        SC2EnvFieldGui.SectionLabel("Create edge — pick two nodes explicitly");
        var nodes = Data.ObjMarkers.Where(m => m.Type != null && m.Type.Contains("Path Node")).ToList();

        GUILayout.BeginHorizontal();
        var labelA = SC2ObjectGraphRefs.TryGetMarker(Data, _pathNodeA, out var pathMarkerA) ? pathMarkerA.Name : "(none)";
        if (SC2EnvFieldGui.Button(TruncateForLabel((_pathPickMode == 1 ? "● " : "") + $"Node A: {labelA}", w * 0.48f), GUILayout.Width(w * 0.48f)))
            _pathPickMode = _pathPickMode == 1 ? 0 : 1;
        var labelB = SC2ObjectGraphRefs.TryGetMarker(Data, _pathNodeB, out var pathMarkerB) ? pathMarkerB.Name : "(none)";
        if (SC2EnvFieldGui.Button(TruncateForLabel((_pathPickMode == 2 ? "● " : "") + $"Node B: {labelB}", w * 0.48f), GUILayout.Width(w * 0.48f)))
            _pathPickMode = _pathPickMode == 2 ? 0 : 2;
        GUILayout.EndHorizontal();

        if (_pathPickMode != 0)
        {
            GUILayout.Label($"Pick Node {(_pathPickMode == 1 ? "A" : "B")} from the list below:");
            _scrollC = GUILayout.BeginScrollView(_scrollC, GUILayout.Height(SC2UiScale.Pxf(140f)));
            foreach (var marker in nodes)
                if (SC2EnvFieldGui.Button(marker.Name, GUILayout.Height(SC2UiScale.Pxf(18f))))
                {
                    if (_pathPickMode == 1) _pathNodeA = marker.Id; else _pathNodeB = marker.Id;
                    _pathPickMode = 0;
                }
            GUILayout.EndScrollView();
        }

        if (SC2EnvFieldGui.Button("Create Edge", GUILayout.Height(SC2UiScale.Pxf(22f))) && _pathNodeA != 0 && _pathNodeB != 0 && _pathNodeA != _pathNodeB)
        {
            PushUndo();
            var existingEdges = new HashSet<(int, int)>();
            foreach (var (ea, eb) in SC2ObjectGraphRefs.Enumerate(g2))
                existingEdges.Add(ea < eb ? (ea, eb) : (eb, ea));
            var addedCount = AddEdgeWithSymmetry(g2, _pathNodeA, _pathNodeB, existingEdges);
            _lastActionSummary = addedCount > 1
                ? $"Connected 2 nodes plus {addedCount - 1} symmetric edge(s)."
                : "Connected 2 nodes.";
            if (addedCount > 0) MarkObjectsDirty();
        }
    }

    /// <summary>Mesh-connects every pair in the current marker selection
    /// (2 selected → 1 edge; N selected → all N*(N-1)/2 pairs), skipping
    /// pairs that already have an edge. This is deliberately the FAF map
    /// editor's model ("select the group you mean to join") rather than a
    /// chain — for 2 nodes the two are identical; for 3+, the user is
    /// asking for the whole group to be mutually connected.</summary>
    private void ConnectSelectedMarkersMesh(SC2ObjGraph g)
    {
        var ids = _selMarkers.ToList();
        var existing = new HashSet<(int, int)>();
        foreach (var (a, b) in SC2ObjectGraphRefs.Enumerate(g))
            existing.Add(a < b ? (a, b) : (b, a));

        var added = 0;
        for (var i = 0; i < ids.Count; i++)
            for (var j = i + 1; j < ids.Count; j++)
                added += AddEdgeWithSymmetry(g, ids[i], ids[j], existing);
        _lastActionSummary = $"Connected {added} new edge(s) among {ids.Count} selected marker(s), including symmetric siblings.";
        if (added > 0) MarkObjectsDirty();
    }

    /// <summary>Adds edge idA-idB, plus a mirrored edge between their
    /// symmetric siblings if BOTH endpoints belong to a SC2ObjSymmetryGroup
    /// — connecting two path nodes that were placed with symmetry also
    /// connects their mirrored counterparts, so a symmetric map's path
    /// graph doesn't need every edge drawn twice by hand. Skips edges that
    /// already exist (via the shared existing-pairs set, which the caller
    /// may pre-seed and reuse across multiple calls in one mesh-connect
    /// pass). Returns how many edges were actually added.
    ///
    /// Audit history: the original version matched siblings by raw
    /// SlotIndex equality ("group A's member at slot s" paired with "group
    /// B's member at slot s" for every slot s shared by both groups") —
    /// correct only when idA and idB both happened to be their group's
    /// canonical (slot 0), and silently wrong otherwise: connecting a
    /// non-canonical sibling (idA at slot 1) to an unrelated marker's
    /// canonical (idB at slot 0) also added an edge between idA's OWN
    /// canonical (never selected at all) and idB. A first fix restricted
    /// mirroring to canonical-canonical only, but that's too narrow — per
    /// direct correction, mirroring must work regardless of whether the
    /// user selects an original or one of its symmetric copies.
    ///
    /// Fixed generally via ComposeSlots: rather than matching by raw slot
    /// number, this preserves the RELATIVE symmetry transform between idA's
    /// own slot and idB's own slot, and reapplies that exact same relative
    /// transform to every other member of both groups — reduces to the old
    /// canonical-canonical behavior when both slots are 0, and generalizes
    /// correctly to any other starting slot combination.</summary>
    private int AddEdgeWithSymmetry(SC2ObjGraph g, int idA, int idB, HashSet<(int, int)> existing)
    {
        var added = 0;
        void TryAdd(int a, int b)
        {
            if (a == b) return;
            var key = a < b ? (a, b) : (b, a);
            if (!existing.Add(key)) return;
            SC2ObjectGraphRefs.AddEdge(g, a, b);
            added++;
        }

        TryAdd(idA, idB);

        var groupA = Data.ObjSymmetryGroups.FirstOrDefault(grp => grp.Members.Any(m => m.ObjectId == idA));
        var groupB = Data.ObjSymmetryGroups.FirstOrDefault(grp => grp.Members.Any(m => m.ObjectId == idB));
        if (groupA == null || groupB == null) return added;

        var slotA = groupA.Members.First(m => m.ObjectId == idA).SlotIndex;
        var slotB = groupB.Members.First(m => m.ObjectId == idB).SlotIndex;
        // Reference position list purely to name every slot geometrically —
        // an arbitrary, non-degenerate offset from SymmetryCenter (never
        // the center itself, which is a fixed point under every mode and
        // would collapse every slot onto the same position).
        var referenceSlots = Data.GetSymmetricPositions(Data.SymmetryCenter + new Vector3(37.13f, 0f, 53.71f));
        for (var extraSlot = 1; extraSlot < referenceSlots.Count; extraSlot++) // extraSlot=0 is identity — the literal edge above already covers it
        {
            var targetSlotA = ComposeSlots(slotA, extraSlot, referenceSlots);
            var targetSlotB = ComposeSlots(slotB, extraSlot, referenceSlots);
            var siblingA = groupA.Members.FirstOrDefault(m => m.SlotIndex == targetSlotA);
            var siblingB = groupB.Members.FirstOrDefault(m => m.SlotIndex == targetSlotB);
            if (siblingA != null && siblingB != null)
                TryAdd(siblingA.ObjectId, siblingB.ObjectId);
        }
        return added;
    }

    /// <summary>Computes the slot index that results from applying slot
    /// `extraSlot`'s symmetry transform on top of slot `baseSlot`'s
    /// transform, both relative to the same shared canonical — i.e. slot
    /// "baseSlot ∘ extraSlot" composed. Derived geometrically instead of via
    /// hand-written per-mode formulas (X/Y/XY are self-inverse reflections
    /// — composing is XOR-like; Radial is rotation — composing is modular
    /// addition): apply extraSlot's transform to the ALREADY-baseSlot-
    /// transformed reference position via a second real GetSymmetricPositions
    /// call, then match the result back to referenceSlots by nearest
    /// distance. Correct for every symmetry mode this project supports
    /// without needing mode-specific arithmetic duplicated here.</summary>
    private int ComposeSlots(int baseSlot, int extraSlot, List<Vector3> referenceSlots)
    {
        if (baseSlot < 0 || baseSlot >= referenceSlots.Count) return baseSlot;
        var composedFromBase = Data.GetSymmetricPositions(referenceSlots[baseSlot]);
        if (extraSlot < 0 || extraSlot >= composedFromBase.Count) return baseSlot;
        var target = composedFromBase[extraSlot];
        var bestSlot = baseSlot;
        var bestDistSq = float.MaxValue;
        for (var i = 0; i < referenceSlots.Count; i++)
        {
            var d = (referenceSlots[i] - target).sqrMagnitude;
            if (d < bestDistSq) { bestDistSq = d; bestSlot = i; }
        }
        return bestSlot;
    }

    private void DisconnectSelectedMarkers(SC2ObjGraph g)
    {
        var ids = new HashSet<int>(_selMarkers);
        var edges = SC2ObjectGraphRefs.Enumerate(g).ToList();
        var removed = 0;
        for (var i = edges.Count - 1; i >= 0; i--)
        {
            var (a, b) = edges[i];
            if (ids.Contains(a) && ids.Contains(b)) { SC2ObjectGraphRefs.RemoveAt(g, i); removed++; }
        }
        _lastActionSummary = $"Disconnected {removed} edge(s) among selected markers.";
        if (removed > 0) MarkObjectsDirty();
    }

    private int CountDuplicateEdges(SC2ObjGraph g)
    {
        var seen = new HashSet<(int, int)>();
        var dup = 0;
        foreach (var (a, b) in SC2ObjectGraphRefs.Enumerate(g))
        {
            var key = a < b ? (a, b) : (b, a);
            if (!seen.Add(key)) dup++;
        }
        return dup;
    }

    private int CountDanglingEdges(SC2ObjGraph g)
    {
        var dangling = 0;
        foreach (var (a, b) in SC2ObjectGraphRefs.Enumerate(g))
        {
            if (!SC2ObjectGraphRefs.TryGetMarker(Data, a, out _) || !SC2ObjectGraphRefs.TryGetMarker(Data, b, out _)) dangling++;
        }
        return dangling;
    }

    // ── 6. Zones ─────────────────────────────────────────────────────────
    private void DrawZones(float w)
    {
        SC2EnvFieldGui.SectionLabel("Areas (playable area bounds / trigger zones)");
        _searchZones = SC2EnvFieldGui.TextField("Search", _searchZones, "zones_search");
        var visible = Data.ObjZones.Where(z => MatchSearch(z.Name, _searchZones)).ToList();

        foreach (var z in visible)
        {
            GUILayout.BeginHorizontal();
            var sel = GUILayout.Toggle(_selectedZoneId == z.Id, TruncateForLabel($"{z.Name} [{z.Role}]", w * 0.7f), GUILayout.Width(w * 0.7f));
            if (sel) _selectedZoneId = z.Id; else if (_selectedZoneId == z.Id) _selectedZoneId = 0;
            if (SC2EnvFieldGui.Button("X", GUILayout.Width(SC2UiScale.Pxf(24f))))
            {
                PushUndo();
                Data.ObjZones.Remove(z);
                _selectedZoneId = 0;
                MarkObjectsDirty();
                GUILayout.EndHorizontal();
                break;
            }
            GUILayout.EndHorizontal();
        }
        if (SC2EnvFieldGui.Button("Add Area", GUILayout.Height(SC2UiScale.Pxf(22f))))
        {
            PushUndo();
            var zoneName = $"AREA_{Data.ObjZones.Count + 1}";
            // Auto-guess (was a separate "Guess role from name" button the
            // user had to click every time) — the heuristic is cheap and
            // name-only, so there's no reason not to run it immediately;
            // the Role field right below stays freely editable either way.
            var nz = new SC2ObjZone { Id = Data.AllocateObjectId(), Name = zoneName, Role = SC2ObjZone.GuessRole(zoneName) };
            Data.ObjZones.Add(nz);
            _selectedZoneId = nz.Id;
            // A zone created here only exists on the legacy list until the
            // next full terrain reload (EnsureObjectsDocumentSynced only
            // re-syncs when Data itself changes) — without this, the very
            // first rect edit below would call SC2SetZoneRectCommand.Do(),
            // which looks the zone up by Id in Session.Document.Objects.Zones
            // and throws "Zone #N not found" since it was never added there.
            // That exception can also visually corrupt the rest
            // of that frame's UI (a caught exception mid-Layout desyncs
            // IMGUI's Layout/Repaint pass counts), which reads as the top
            // panel getting covered rather than a separate bug.
            SyncZoneToDocument(nz);
            MarkObjectsDirty();
        }

        var selZone = Data.ObjZones.FirstOrDefault(x => x.Id == _selectedZoneId);
        if (selZone != null)
        {
            GUILayout.Space(6);
            var prevZoneName = selZone.Name;
            selZone.Name = SC2EnvFieldGui.TextField("Name", selZone.Name, "zone_name");
            if (selZone.Name != prevZoneName) MarkObjectsDirty();
            SC2EnvFieldGui.WrapLabel(Data.Symmetry == SymmetryMode.None
                ? "Edges edit independently (symmetry mode: None)."
                : $"Moving an edge live-moves its symmetric edge outward/inward by the same amount, keeping the rect centered (symmetry mode: {Data.Symmetry}).", w);

            // Edge-based editing (not corner/point-based): the rect is
            // expressed as 4 independent scalar edges (Left/Right/Bottom/
            // Top) rather than 2 corner Vector2 fields. A corner conflates
            // one X edge and one Z edge into a single point, so dragging
            // "Corner 1" always perturbed both axes together even when the
            // user only meant to move one edge — per the user's own report,
            // edges (faces) must move independently, points must not.
            var left = Mathf.Min(selZone.X1, selZone.X2);
            var right = Mathf.Max(selZone.X1, selZone.X2);
            var bottom = Mathf.Min(selZone.Z1, selZone.Z2);
            var top = Mathf.Max(selZone.Z1, selZone.Z2);

            var newLeft = SC2EnvFieldGui.FloatField("Left (X)", left, "zone_left");
            var newRight = SC2EnvFieldGui.FloatField("Right (X)", right, "zone_right");
            var newBottom = SC2EnvFieldGui.FloatField("Bottom (Z)", bottom, "zone_bottom");
            var newTop = SC2EnvFieldGui.FloatField("Top (Z)", top, "zone_top");

            // Tolerance well above the text field's own "0.###" display
            // rounding (max ~0.0005 round-trip drift): comparing with exact
            // (or near-exact, e.g. Vector2's ~1e-5) equality against that
            // rounding noise was the root cause of a runaway feedback loop —
            // each frame's harmless display-rounding drift on one edge kept
            // registering as a "real" edit, mirroring a tiny nudge onto the
            // opposite edge, which then looked like ITS OWN "real" edit on
            // the next frame, forever — recording a fresh undo step every
            // single frame the panel was open (observed: undo counter in
            // the thousands after one Mirror click). A tolerance this size
            // still catches every real, intentional edit.
            const float edgeChangeEpsilon = 0.01f;
            var leftChanged = Mathf.Abs(newLeft - left) > edgeChangeEpsilon;
            var rightChanged = Mathf.Abs(newRight - right) > edgeChangeEpsilon;
            var bottomChanged = Mathf.Abs(newBottom - bottom) > edgeChangeEpsilon;
            var topChanged = Mathf.Abs(newTop - top) > edgeChangeEpsilon;

            // Proportional/symmetric resize: moving one edge OUT (away from
            // the rect's own center) by delta moves its opposite edge OUT by
            // the same delta too (e.g. push Bottom down 100 -> Top also
            // moves up 100), keeping the rect centered rather than sliding
            // it. Gated on the Mirror X/Z toggles right below (per-Area,
            // explicit, always visible) rather than the global Data.Symmetry
            // setting — Mirror X/Z are themselves the activation switch for
            // this behavior now, not a one-shot reflect action.
            if (leftChanged && !rightChanged && _areaMirrorX)
                newRight = right - (newLeft - left);
            else if (rightChanged && !leftChanged && _areaMirrorX)
                newLeft = left - (newRight - right);

            if (bottomChanged && !topChanged && _areaMirrorZ)
                newTop = top - (newBottom - bottom);
            else if (topChanged && !bottomChanged && _areaMirrorZ)
                newBottom = bottom - (newTop - top);

            if (leftChanged || rightChanged || bottomChanged || topChanged)
                ApplyZoneRect(selZone, newLeft, newBottom, newRight, newTop);

            GUILayout.Space(4);
            SC2EnvFieldGui.WrapLabel("Mirror X/Z: while active, dragging an edge here or on the map moves its opposite edge outward/inward by the same amount instead of moving alone.", w);
            GUILayout.BeginHorizontal();
            _areaMirrorX = GUILayout.Toggle(_areaMirrorX, "Mirror X", "Button", GUILayout.Height(SC2UiScale.Pxf(20f)));
            _areaMirrorZ = GUILayout.Toggle(_areaMirrorZ, "Mirror Z", "Button", GUILayout.Height(SC2UiScale.Pxf(20f)));
            GUILayout.EndHorizontal();
        }
    }

    /// <summary>Single entry point for "this zone's rect changed" — used by
    /// both direct corner edits and the Mirror buttons, so the document-
    /// command + legacy-field-mirroring pattern only has to be written once.</summary>
    private void ApplyZoneRect(SC2ObjZone z, float x1, float z1, float x2, float z2)
    {
        if (Session?.Document != null)
        {
            Session.History.Execute(Session.Document, new SC2SetZoneRectCommand(z.Id, x1, z1, x2, z2));
            _undo.Record(new SC2DelegateUndoChannel(TryCommandUndo, TryCommandRedo));
        }
        z.X1 = x1; z.Z1 = z1; z.X2 = x2; z.Z2 = z2;
    }

    /// <summary>Appends a matching zone to Session.Document.Objects.Zones if
    /// one with this Id doesn't already exist — the create-time counterpart
    /// to ApplyZoneRect's ongoing per-edit sync (which only updates a zone
    /// already present in both places). See the "Add Area" button's own
    /// comment for why this is needed at all.</summary>
    private void SyncZoneToDocument(SC2ObjZone z)
    {
        if (Session?.Document == null) return;
        var zones = Session.Document.Objects.Zones;
        if (zones.Any(x => x.Id == z.Id)) return;
        zones.Add(new SC2ObjZone { Id = z.Id, Name = z.Name, Role = z.Role, X1 = z.X1, Z1 = z.Z1, X2 = z.X2, Z2 = z.Z2 });
    }

    // ── 7. Chains ────────────────────────────────────────────────────────
    private void DrawChains(float w)
    {
        SC2EnvFieldGui.SectionLabel("Chains (scripted patrol/attack routes)");
        _searchChains = SC2EnvFieldGui.TextField("Search", _searchChains, "chains_search");
        var visible = Data.ObjChains.Where(c => MatchSearch(c.Name, _searchChains)).ToList();

        foreach (var c in visible)
        {
            GUILayout.BeginHorizontal();
            var sel = GUILayout.Toggle(_selectedChainId == c.Id, TruncateForLabel(c.Name, w * 0.7f), GUILayout.Width(w * 0.7f));
            if (sel) _selectedChainId = c.Id; else if (_selectedChainId == c.Id) _selectedChainId = 0;
            if (SC2EnvFieldGui.Button("X", GUILayout.Width(SC2UiScale.Pxf(24f))))
            {
                PushUndo();
                Data.ObjChains.Remove(c);
                _selectedChainId = 0;
                MarkObjectsDirty();
                GUILayout.EndHorizontal();
                break;
            }
            GUILayout.EndHorizontal();
        }
        if (SC2EnvFieldGui.Button("Add Chain", GUILayout.Height(SC2UiScale.Pxf(22f))))
        {
            PushUndo();
            var nc = new SC2ObjChain { Id = Data.AllocateObjectId(), Name = $"Chain_{Data.ObjChains.Count + 1}" };
            Data.ObjChains.Add(nc);
            _selectedChainId = nc.Id;
            MarkObjectsDirty();
        }

        var selChain = Data.ObjChains.FirstOrDefault(x => x.Id == _selectedChainId);
        if (selChain != null)
        {
            GUILayout.Space(6);
            var newChainName = SC2EnvFieldGui.TextField("Name", selChain.Name, "chain_name");
            if (newChainName != selChain.Name)
            {
                if (Session?.Document != null)
                {
                    Session.History.Execute(Session.Document, new SC2SetChainNameCommand(selChain.Id, newChainName));
                    _undo.Record(new SC2DelegateUndoChannel(TryCommandUndo, TryCommandRedo));
                }
                selChain.Name = newChainName;
            }
            SC2EnvFieldGui.SectionLabel($"Waypoints ({selChain.MarkerNames.Count})");
            for (var i = 0; i < selChain.MarkerNames.Count; i++)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label($"{i + 1}.", GUILayout.Width(SC2UiScale.Pxf(24f)));
                var prevWp = selChain.MarkerNames[i];
                selChain.MarkerNames[i] = SC2EnvFieldGui.TextField("", selChain.MarkerNames[i], "chain_wp_" + i);
                if (selChain.MarkerNames[i] != prevWp) MarkObjectsDirty();
                if (SC2EnvFieldGui.Button("↑", GUILayout.Width(SC2UiScale.Pxf(24f))) && i > 0)
                {
                    (selChain.MarkerNames[i - 1], selChain.MarkerNames[i]) = (selChain.MarkerNames[i], selChain.MarkerNames[i - 1]);
                    MarkObjectsDirty();
                }
                if (SC2EnvFieldGui.Button("↓", GUILayout.Width(SC2UiScale.Pxf(24f))) && i < selChain.MarkerNames.Count - 1)
                {
                    (selChain.MarkerNames[i + 1], selChain.MarkerNames[i]) = (selChain.MarkerNames[i], selChain.MarkerNames[i + 1]);
                    MarkObjectsDirty();
                }
                if (SC2EnvFieldGui.Button("X", GUILayout.Width(SC2UiScale.Pxf(24f))))
                {
                    selChain.MarkerNames.RemoveAt(i);
                    MarkObjectsDirty();
                    GUILayout.EndHorizontal();
                    break;
                }
                GUILayout.EndHorizontal();
            }
            if (SC2EnvFieldGui.Button("Add Waypoint", GUILayout.Height(SC2UiScale.Pxf(20f))))
            {
                selChain.MarkerNames.Add(Data.ObjMarkers.Count > 0 ? Data.ObjMarkers[0].Name : "");
                MarkObjectsDirty();
            }

            // Selecting 2+ objects and clicking
            // Connect joins ALL of them with each other — the same select-
            // then-Connect gesture Path graphs already use
            // (ConnectSelectedMarkersMesh). A Chain has no graph/edge
            // structure of its own (MarkerNames is just an ordered list),
            // so "connect" here means "this chain's membership IS exactly
            // the current marker selection" — the closest faithful
            // equivalent of all-to-all for a flat list, replacing manual
            // one-at-a-time text entry.
            if (_selMarkers.Count >= 2)
            {
                GUILayout.Space(6);
                SC2EnvFieldGui.SectionLabel($"Selected markers ({_selMarkers.Count}) — from the Markers tab");
                SC2EnvFieldGui.WrapLabel("Connect replaces this chain's waypoints with exactly the selected markers.", w);
                if (SC2EnvFieldGui.Button("Connect selected → this chain", GUILayout.Height(SC2UiScale.Pxf(22f)), GUILayout.Width(w)))
                {
                    PushUndo();
                    ConnectSelectedMarkersToChain(selChain);
                }
            }
        }
    }

    private void ConnectSelectedMarkersToChain(SC2ObjChain chain)
    {
        var names = Data.ObjMarkers.Where(m => _selMarkers.Contains(m.Id)).Select(m => m.Name).ToList();
        chain.MarkerNames = names;
        _lastActionSummary = $"Connected {names.Count} selected marker(s) into chain '{chain.Name}'.";
        MarkObjectsDirty();
    }

    // ── Viewport integration ─────────────────────────────────────────────
    // Clickable dots for Props/Markers in the active subtab, reusing the
    // same GUI-space projection/raycast plumbing SC2CostsShell already uses
    // for terrain painting (SC2TerrainShell.DrawGameViewBrush is the public
    // entry point; WorldToGui below replicates its private WorldToGameGui
    // formula just for hit-testing distances, since that helper isn't
    // exposed).
    //
    // Capping the drawn set to a fixed count (e.g. 400) for perf causes real
    // regressions: taking the first N in LIST order means props placed after
    // the cutoff never get a dot, anywhere; taking the N nearest the
    // camera's own 3D position BEFORE checking on-screen-ness builds
    // a fixed-radius bubble around the camera — clearly visible props just
    // stop getting dots once more than N are on-screen at once (an
    // entirely ordinary situation when zoomed out to see a large map, not a
    // rare edge case). Every on-screen object
    // should always get a dot, full stop — no count-based fallback cap at
    // all. The on-screen test itself (TryWorldToGui: one matrix transform +
    // rect containment per candidate) is cheap; if drawing thousands of
    // simultaneously-visible dots' GUI.DrawTexture calls ever turns out to
    // be the actual bottleneck, that's a distinct, separately-diagnosable
    // performance report, not something to pre-emptively solve by hiding
    // objects the user asked to see.
    private static List<T> VisibleForViewport<T>(IEnumerable<T> items, Camera camera, Rect viewportRect, Func<T, Vector3> position)
    {
        var onscreen = new List<T>();
        foreach (var item in items)
            if (TryWorldToGui(camera, viewportRect, position(item), out _))
                onscreen.Add(item);
        return onscreen;
    }
    // GroupHandle is a synthetic pick target (not a real Prop/Marker/Unit
    // Id) at a multi-selection's centroid — see DrawGroupHandleGlyph.
    private enum ViewportObjectKind { None, Prop, Marker, Unit, Zone, GroupHandle }
    private struct ViewportPick { public ViewportObjectKind Kind; public int Id; public float Distance; }

    // ── Drag-to-move state (practical-recommendations: "moving markers
    // should happen through the map view itself, not the side panel") ──
    // Active on a plain click-drag of a picked object (Select tool — moving
    // is unconditional default behavior, no separate "Move tool"/hotkey);
    // drags the WHOLE current selection together by one shared delta, and
    // routes each item's position through Set*Position so live symmetry
    // links keep following during the drag, not just after releasing.
    private bool _dragActive;
    private readonly List<(ViewportObjectKind kind, int id, Vector3 originalPos)> _dragItems = new();
    private Vector3 _dragStartWorld;

    // ── Props rotate/scale gesture (R / S) — R rotates,
    // S scales the current Props selection, no other function bound to
    // either key, and no tool-switch/hold needed (matches the drag-to-move
    // behavior above — press once, move the mouse, click or Enter to
    // confirm, Esc to cancel). Each prop rotates/scales around ITS OWN
    // position (not the selection's centroid as a rigid group) — the same
    // delta (rotation angle / scale ratio), computed once from the shared
    // gesture anchor, is applied to every selected prop's own Rotation/
    // Scale field independently, so a multi-select doesn't also displace
    // anyone's position.
    private enum PropsGestureKind { None, Rotate, Scale }
    private PropsGestureKind _propsGestureKind = PropsGestureKind.None;
    private Vector2 _gestureAnchorScreen;
    private Vector2 _gestureStartMouse;
    private float _gestureStartDistance;
    private readonly List<(SC2ObjProp prop, Quaternion originalRotation, float originalScale)> _gestureItems = new();

    // ── Copy/paste (Ctrl+C / Ctrl+V) — copies the current Prop/Marker/Unit
    // selection as plain field-value snapshots (not live references, so
    // later edits to the originals don't bleed into the clipboard), pastes
    // as brand-new objects with fresh Ids at an offset from the originals.
    // Groups are excluded from Unit copies (only leaf units, IsGroup=false)
    // to avoid remapping ParentIndex hierarchies — matches the existing
    // symmetry-duplicate functions' own scope (DuplicatePropWithSymmetry/
    // DuplicateMarkerWithSymmetry never touch Units' group structure either).
    private readonly List<SC2ObjProp> _clipboardProps = new();
    private readonly List<SC2ObjMarker> _clipboardMarkers = new();
    private readonly List<SC2ObjUnitNode> _clipboardUnits = new();
    private int _clipboardPasteCount;

    // ── Box/marquee select — Select-tool click-drag on empty space selects
    // every object of the CURRENTLY OPEN Objects sub-tab's kind (Props/
    // Units/Markers, per _lastSubTab) whose viewport position falls inside
    // the dragged rectangle, so a whole cluster can then be moved together
    // via the existing multi-select drag (which already propagates through
    // symmetry — see HandleViewportDrag/CommitDragPositions).
    private bool _boxSelectActive;
    private Vector2 _boxSelectStartScreen;

    // ── Click-to-place: the tool does not
    // disarm after one placement — repeated clicks keep placing more of the
    // same type until the user explicitly switches tools ────────────────
    // Shared by Markers/Units/Armies/Props (Props ALSO has its own separate
    // brush/scatter mechanism — SC2ObjectsShell.PropsBrush, a materially
    // different click-and-drag multi-instance gesture — this PlacementKind
    // path is for a single, individually-chosen prop instead).
    private enum PlacementKind { None, Marker, Unit, Army, Prop, MarkerPreset }
    private PlacementKind _placementKind = PlacementKind.None;
    private string _placementSubtype = "";
    /// <summary>Set alongside PlacementKind.MarkerPreset (after ArmPlacement,
    /// which doesn't know about this field and so can't reset it) — the
    /// preset whose Entries get placed, offset from the click point, on the
    /// next viewport click.</summary>
    private SC2MarkerPresets.Preset _placementPreset;
    /// <summary>Blueprint id to use for the next PlacementKind.Unit click,
    /// set by OpenUnitBrowser's browse-then-place flow — empty means the
    /// generic "Place in viewport..." button's own placeholder blueprint
    /// (PlaceUnitAt's own default).</summary>
    private string _placementUnitType = "";
    /// <summary>Real, catalog-resolvable unit blueprint id ("Drone") used
    /// only when the user places via the generic "Place in viewport..."
    /// button without ever picking a real blueprint through "Browse unit
    /// blueprints" — see PlaceUnitAt's own comment for why this replaced
    /// the old "uca0101" placeholder, which never existed in real game
    /// data.</summary>
    private const string DefaultPlaceholderUnitType = "sca3601";
    /// <summary>Group Id a placed unit should land in, set by the selected-
    /// group inspector's "Place in viewport..." AFTER calling ArmPlacement
    /// (which resets this field) — 0 means "resolve the army's default
    /// group" (ResolveDefaultUnitGroup), same as any other placement path.</summary>
    private int _placementTargetGroupId;
    /// <summary>Blueprint path/asset for the next PlacementKind.Prop click,
    /// set by OpenPropBrowserForPlacement's browse-then-place flow AFTER
    /// calling ArmPlacement (which resets both fields).</summary>
    private string _placementPropBlueprint = "";
    private SC2GameAsset _placementPropAsset;

    private void ArmPlacement(PlacementKind kind, string subtype)
    {
        _placementKind = kind;
        _placementSubtype = subtype ?? "";
        // Generic-tool arming (the per-army "Place in viewport..." button)
        // should always fall back to PlaceUnitAt's own placeholder blueprint,
        // not a leftover blueprint choice from a previous browse-then-place
        // session — OpenUnitBrowser sets this explicitly AFTER calling
        // ArmPlacement, so its own assignment survives this reset.
        _placementUnitType = "";
        _placementTargetGroupId = 0;
        _placementPropBlueprint = "";
        _placementPropAsset = null;
        _placementPreset = null;
        _lastActionSummary = $"Click in the viewport to place {kind}(s) — press Esc to stop.";
    }

    private void DisarmPlacement()
    {
        _placementKind = PlacementKind.None;
        _placementSubtype = "";
        _placementUnitType = "";
        _placementTargetGroupId = 0;
        _placementPropBlueprint = "";
        _placementPropAsset = null;
        _placementPreset = null;
        _placementRotationY = 0f;
        _placementRotating = false;
    }

    /// <summary>Cancels every "armed" viewport tool this tab can have active
    /// at once — click-to-place (Markers/Units/Army/Props/MarkerPreset),
    /// the Props paint brush (incl. its eraser variant), and Paths' Node A/B
    /// picking. Called whenever the user leaves the space a tool was armed
    /// in: switching Objects sub-tab (DrawEmbeddedGUI, below) or leaving the
    /// Objects main tab entirely (SC2GameViewEditorUI.HandleObjectsGameViewInput)
    /// — same cancellation Esc already performs, just triggered by
    /// navigation instead of a keypress.</summary>
    public void CancelActiveTools()
    {
        DisarmPlacement();
        _propsBrushActive = false;
        _propsBrushErasing = false;
        _propsBrushDragging = false;
        _pathPickMode = 0;
    }

    /// <summary>Consumes the click if a placement tool is armed and the
    /// cursor is over a raycastable surface; returns false (does nothing)
    /// otherwise so the caller falls through to normal select/drag handling
    /// unchanged. Reuses the same imported-terrain-first raycast order as
    /// TryGetDragPoint (the Objects-side height-query fix), so
    /// placed objects land correctly on a Blender-imported map too.</summary>
    private bool TryHandlePlacementClick(Camera camera, Rect viewportRect, Vector2 mousePosition, bool allowInput)
    {
        if (_placementKind == PlacementKind.None) return false;
        var e = Event.current;
        if (e == null || !allowInput || e.type != EventType.MouseDown || e.button != 0) return false;
        if (!viewportRect.Contains(mousePosition)) return false;

        var ray = SC2TerrainShell.GameGuiPointToRay(camera, viewportRect, mousePosition);
        if (!TryRaycastImportedTerrain(ray.origin, ray.direction, out var hit) &&
            !SC2TerrainShell.RaycastTerrainHeight(_terrainShell.Runtime, ray, out hit))
        {
            _lastActionSummary = "Placement: nothing under the cursor to place onto.";
            e.Use();
            return true;
        }

        if (_placementKind == PlacementKind.Marker && HasEquivalentPlacedMarker(BuildMarkerPlacementCandidate(hit, _placementSubtype)))
        {
            _lastActionSummary = "Marker not placed: an equivalent marker already occupies that grid cell.";
            SC2MarkerScenePreview.ClearGhosts();
            e.Use();
            return true;
        }
        if (_placementKind == PlacementKind.Army && HasArmyAt(hit))
        {
            _lastActionSummary = "Army not placed: a start-position marker already occupies that grid cell.";
            SC2MarkerScenePreview.ClearGhosts();
            e.Use();
            return true;
        }

        PushUndo();
        switch (_placementKind)
        {
            case PlacementKind.Marker: PlaceMarkerAt(hit, _placementSubtype); break;
            case PlacementKind.Unit: PlaceUnitAt(hit, _placementSubtype); break;
            case PlacementKind.Army: PlaceArmyAt(hit); break;
            case PlacementKind.Prop: PlacePropAt(hit); break;
            case PlacementKind.MarkerPreset: if (_placementPreset != null) PlaceMarkerPreset(_placementPreset, hit); break;
        }
        e.Use();
        RepaintHost?.Invoke();
        return true;
    }

    /// <summary>The brush previously had no viewport indicator at all — the
    /// radius/density controls existed in the side panel, but nothing showed
    /// where the brush actually was or how big it was until you'd already
    /// clicked. Reuses SC2TerrainShell.DrawGameViewBrush (the same ring
    /// Terrain/Costs painting already draws) with the SAME raycast order as
    /// the actual paint tick, so the ring always matches exactly where a
    /// click would land.</summary>
    /// <summary>Holding Ctrl temporarily acts as the Props brush's eraser,
    /// alongside the same Ctrl-held-erase behavior for
    /// Costs/TerrainTypes/RNM-blend painting (see SC2CostsShell.IsErasingHeld
    /// and SC2PaintMode.HandleRnm). The "Eraser mode" checkbox itself
    /// (_propsBrushErasing) never changes — this only affects what a
    /// click/drag actually does while Ctrl is held, exactly like a modifier
    /// key in any other paint tool.</summary>
    private bool EffectivePropsErasing() => _propsBrushErasing || SC2Hotkeys.ModifierHeld("Objects.PropsBrushEraseModifier", Event.current);

    private void DrawPropsBrushCursor(Camera camera, Rect viewportRect, Vector2 mousePosition)
    {
        if (!_propsBrushActive) return;
        var ray = SC2TerrainShell.GameGuiPointToRay(camera, viewportRect, mousePosition);
        if (!TryRaycastImportedTerrain(ray.origin, ray.direction, out var hit) &&
            !SC2TerrainShell.RaycastTerrainHeight(_terrainShell.Runtime, ray, out hit))
            return;

        var erasingPreview = EffectivePropsErasing();
        var color = erasingPreview ? new Color(1f, .25f, .2f, .9f) : new Color(.3f, .95f, .5f, .9f);
        // Eraser mirrors across symmetry (see ErasePropsAt) - show every
        // slot's ring, not just the one under the cursor, so the preview
        // matches what actually gets removed. Scatter mode only mirrors
        // once at stroke-end (MirrorBrushStroke), so its cursor stays
        // single-ring to match what a single paint tick actually places.
        if (erasingPreview && Data.Symmetry != SymmetryMode.None)
        {
            foreach (var slot in Data.GetSymmetricPositions(hit))
                SC2TerrainShell.DrawGameViewBrush(camera, viewportRect, slot, _propsBrushRadius, color, Color.clear);
        }
        else
        {
            SC2TerrainShell.DrawGameViewBrush(camera, viewportRect, hit, _propsBrushRadius, color, Color.clear);
        }
    }

    /// <summary>F interactive brush-radius resize, Blender-style — must be checked BEFORE DrawPropsBrushCursor/
    /// TryHandlePropsBrush each frame the brush is armed, so an active (or
    /// just-started) resize owns the click and draws its own preview circle
    /// instead of a paint tick or the normal cursor firing underneath it.
    /// Same raycast order as the actual paint tick (PaintPropsBrushTick) so
    /// the resize anchor always lands exactly where a click would have.</summary>
    private bool HandlePropsBrushRadiusResize(Camera camera, Rect viewportRect, Vector2 mousePosition, bool allowInput)
    {
        if (!_propsBrushActive) return false;
        var ray = SC2TerrainShell.GameGuiPointToRay(camera, viewportRect, mousePosition);
        var hasHit = TryRaycastImportedTerrain(ray.origin, ray.direction, out var hit) ||
            SC2TerrainShell.RaycastTerrainHeight(_terrainShell.Runtime, ray, out hit);
        return _propsBrushRadiusGesture.HandleInput(camera, viewportRect, mousePosition, hasHit, hit, allowInput, ref _propsBrushRadius, 1f, 60f);
    }

    /// <summary>Shows what will actually be placed BEFORE the click, for
    /// Marker/Unit/Army click-to-place: a translucent version of the same
    /// screen-space glyph DrawViewportDot draws for real objects, at the
    /// snapped (SnapXZ) landing spot - and at every symmetry slot too, since
    /// placement auto-creates symmetric siblings (PlaceMarkerAt/PlaceUnitAt/
    /// PlaceArmyAt all call LinkMarkersWithSymmetry/LinkUnitsWithSymmetry
    /// when a symmetry mode is active), so the preview shows the whole set
    /// that will land, not just the one under the cursor.
    ///
    /// For Marker/Army specifically, ALSO shows the real 3D marker gizmo
    /// model (SC2MarkerScenePreview.SyncGhosts) at the same spot(s), flat-
    /// tinted a fixed cyan so it never reads as one of the 27 markers' own
    /// real colors: the flat 2D dot
    /// alone no longer reads as a preview once real placed markers render
    /// as full 3D models, and sharing the
    /// same per-type color the real marker would get (just at half alpha)
    /// would make the preview hard to distinguish from an already-placed
    /// marker.
    /// Every exit path (including the early returns above) must clear the
    /// ghosts, not just skip drawing them - they're real GameObjects that
    /// persist across frames, unlike the 2D glyph's per-frame draw calls.</summary>
    private void DrawPlacementPreview(Camera camera, Rect viewportRect, Vector2 mousePosition)
    {
        if (_placementKind == PlacementKind.None) { SC2MarkerScenePreview.ClearGhosts(); SC2GameAssetScenePreview.ClearPropGhosts(); return; }
        var ray = SC2TerrainShell.GameGuiPointToRay(camera, viewportRect, mousePosition);
        if (!TryRaycastImportedTerrain(ray.origin, ray.direction, out var hit) &&
            !SC2TerrainShell.RaycastTerrainHeight(_terrainShell.Runtime, ray, out hit))
        {
            SC2MarkerScenePreview.ClearGhosts();
            SC2GameAssetScenePreview.ClearPropGhosts();
            return;
        }

        if (_placementKind == PlacementKind.MarkerPreset)
        {
            SC2GameAssetScenePreview.ClearPropGhosts();
            DrawMarkerPresetPreview(camera, viewportRect, hit);
            return;
        }

        Color color;
        ViewportObjectKind kind;
        string ghostIconKey = null;
        switch (_placementKind)
        {
            case PlacementKind.Marker:
                var tpl = SC2MarkerTemplates.Find(_placementSubtype);
                color = tpl != null ? SC2MarkerTemplates.HexToColor(tpl.ColorHex) : new Color(.8f, .8f, .8f, 1f);
                kind = ViewportObjectKind.Marker;
                ghostIconKey = tpl?.IconKey;
                break;
            case PlacementKind.Unit:
                color = new Color(.86f, .45f, 1f, 1f);
                kind = ViewportObjectKind.Unit;
                break;
            case PlacementKind.Army:
                color = new Color(1f, .82f, .2f, 1f);
                kind = ViewportObjectKind.Marker;
                ghostIconKey = SC2MarkerTemplates.ArmyIconKey;
                break;
            case PlacementKind.Prop:
                color = new Color(.25f, .86f, 1f, 1f);
                kind = ViewportObjectKind.Prop;
                break;
            default: SC2MarkerScenePreview.ClearGhosts(); SC2GameAssetScenePreview.ClearPropGhosts(); return;
        }
        color.a = 0.5f; // translucent - distinguishes "about to place" from real placed objects

        var positions = Data.Symmetry == SymmetryMode.None
            ? new List<Vector3> { SnapXZ(hit) }
            : GetUniqueSnappedMarkerSymmetryPositions(hit);
        if (_placementKind == PlacementKind.Marker)
        {
            var probe = BuildMarkerPlacementCandidate(hit, _placementSubtype);
            positions = positions.Where(position =>
            {
                probe.Position = position;
                return !HasEquivalentPlacedMarker(probe);
            }).ToList();
        }
        else if (_placementKind == PlacementKind.Army)
        {
            positions = positions.Where(position => !HasArmyAt(position)).ToList();
        }
        foreach (var pos in positions)
            if (TryWorldToGui(camera, viewportRect, pos, out var gui))
                DrawViewportGlyph(gui, color, kind);

        if (!string.IsNullOrEmpty(ghostIconKey) && positions.Count > 0)
            SC2MarkerScenePreview.SyncGhosts(ghostIconKey, positions, new Vector3(0f, _placementRotationY, 0f));
        else
            SC2MarkerScenePreview.ClearGhosts();

        // Single-prop placement should also show the real 3D model preview,
        // not just the flat 2D dot above, matching Marker/Army placement —
        // see SC2GameAssetScenePreview.SyncPropGhosts' own
        // doc comment.
        if (_placementKind == PlacementKind.Prop && _placementPropAsset != null && positions.Count > 0)
        {
            var rotation = Quaternion.Euler(0f, _placementRotationY, 0f);
            var placements = positions.Select(p => (p, rotation, 1f)).ToList();
            SC2GameAssetScenePreview.SyncPropGhosts(_placementPropAsset, _assetBrowser.GameDataRoot, placements);
            // Footprint (e.g. `Footprint = { SizeX =
            // 14, SizeZ = 14 }`) is how many terrain cells a building
            // actually occupies. Draw the real occupied-area rectangle at
            // each landing spot (including symmetric siblings), not just
            // the flat 2D dot above, so overlapping footprints are
            // visible before the click commits.
            if (_placementPropAsset.FootprintSizeX > 0f && _placementPropAsset.FootprintSizeZ > 0f)
                foreach (var pos in positions)
                    DrawFootprintOverlay(camera, viewportRect, pos, _placementPropAsset.FootprintSizeX, _placementPropAsset.FootprintSizeZ, _placementRotationY, color);
        }
        else
        {
            SC2GameAssetScenePreview.ClearPropGhosts();
        }

        // The Footprint overlay must also cover buildings, because real
        // buildings in this game's data
        // are UNIT blueprints (structures), not Prop blueprints — the block
        // above only checks _placementPropAsset. Units track only a
        // plain blueprint-id string (_placementUnitType) during placement,
        // not a resolved SC2GameAsset the way Props do, so resolve it here.
        if (_placementKind == PlacementKind.Unit && positions.Count > 0)
        {
            var unitType = string.IsNullOrEmpty(_placementUnitType) ? DefaultPlaceholderUnitType : _placementUnitType;
            var unitAsset = _assetBrowser.FindUnitAsset(unitType);
            if (unitAsset != null && unitAsset.FootprintSizeX > 0f && unitAsset.FootprintSizeZ > 0f)
                foreach (var pos in positions)
                    DrawFootprintOverlay(camera, viewportRect, pos, unitAsset.FootprintSizeX, unitAsset.FootprintSizeZ, _placementRotationY, color);
        }
    }

    /// <summary>Draws a rotated rectangle in world space (terrain-following
    /// at each corner, same convention as DrawZoneOverlays) representing a
    /// building/prop's real Footprint (SizeX/SizeZ, in terrain cells —
    /// converted via Data.CellSize). center is the object's own placement
    /// point (footprint is centered on it, matching the real game's own
    /// convention).</summary>
    // Footprint.SizeX/SizeZ are in
    // real WORLD units (matching the grid's own base level, see
    // SC2TerrainShell.PushHeightGridOverlay's doc comment), not
    // Data.CellSize's terrain-heightfield sampling resolution (2 world
    // units by default); multiplying by CellSize would double each
    // dimension, quadrupling the drawn rectangle's area relative to the
    // real footprint.
    private const float FootprintWorldUnitsPerCell = 1f;
    private static readonly Color PlacedFootprintColor = new Color(1f, .65f, .15f, .7f);
    // The footprint is a solid red fill over the
    // whole occupied area, not just a wireframe outline.
    private static readonly Color FootprintFillColor = new Color(1f, .12f, .12f, .38f);

    private void DrawFootprintOverlay(Camera camera, Rect viewportRect, Vector3 center, float sizeXCells, float sizeZCells, float rotationYDegrees, Color color)
    {
        var halfX = sizeXCells * FootprintWorldUnitsPerCell * 0.5f;
        var halfZ = sizeZCells * FootprintWorldUnitsPerCell * 0.5f;
        var rot = Quaternion.Euler(0f, rotationYDegrees, 0f);
        Vector3 Corner(float x, float z)
        {
            var flat = center + rot * new Vector3(x, 0f, z);
            return new Vector3(flat.x, SampleHeight(flat.x, flat.z) + .3f, flat.z);
        }
        var c0 = Corner(-halfX, -halfZ);
        var c1 = Corner(halfX, -halfZ);
        var c2 = Corner(halfX, halfZ);
        var c3 = Corner(-halfX, halfZ);
        DrawOverlayLine(camera, viewportRect, c0, c1, color, 2f);
        DrawOverlayLine(camera, viewportRect, c1, c2, color, 2f);
        DrawOverlayLine(camera, viewportRect, c2, c3, color, 2f);
        DrawOverlayLine(camera, viewportRect, c3, c0, color, 2f);

        // A 2D screen-space GL quad (GL.LoadPixelMatrix,
        // projected via the same SC2TopViewMirror.WorldToGui the outline
        // above uses) drifts/"rotates with
        // the camera", because GL calls issued from OnGUI bypass
        // Unity's IMGUI matrix stack entirely (GUI.matrix, DPI scaling via
        // SC2UiScale, and whatever screen-space adjustment
        // SC2TopViewMirror's own "Active" mirrored-render mode applies all
        // only ever affect GUI.* calls, never raw GL.* calls) — so a
        // GL.LoadPixelMatrix() quad would be positioned against a
        // DIFFERENT pixel-coordinate mapping than the one WorldToGui's
        // numbers actually describe, causing exactly this kind of visible
        // drift as the camera/viewport changed. Real 3D scene geometry,
        // rendered by the same camera as the terrain/buildings themselves,
        // has none of this risk — mirrors the existing SC2GameAssetScenePreview/
        // SC2MarkerScenePreview ghost-preview pattern instead of a 2D overlay.
        SyncFootprintFillQuad(center, rot, halfX * 2f, halfZ * 2f, SampleHeight(center.x, center.z) + .2f);
    }

    // ── Real 3D scene-mesh footprint fill (see DrawFootprintOverlay's own
    // comment for why this replaced an earlier GL-based 2D overlay) ──────
    // Pooled, reused across frames — one quad GameObject per footprint
    // drawn this frame, indexed by draw order. _footprintFillUsedThisFrame
    // tracks how many are actually needed; anything beyond that from a
    // previous frame (fewer footprints visible now, e.g. camera panned
    // away or placement cancelled) gets deactivated at frame end.
    private static Mesh _footprintFillMesh;
    private static Material _footprintFillMaterial;
    private readonly List<GameObject> _footprintFillPool = new();
    private int _footprintFillUsedThisFrame;
    private int _footprintFillFrameToken = -1;

    private static Mesh FootprintFillMesh
    {
        get
        {
            if (_footprintFillMesh != null) return _footprintFillMesh;
            _footprintFillMesh = new Mesh { name = "SC2FootprintFillQuad", hideFlags = HideFlags.HideAndDontSave };
            _footprintFillMesh.SetVertices(new List<Vector3>
            {
                new(-0.5f, 0f, -0.5f), new(0.5f, 0f, -0.5f), new(0.5f, 0f, 0.5f), new(-0.5f, 0f, 0.5f)
            });
            _footprintFillMesh.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0);
            _footprintFillMesh.RecalculateNormals();
            _footprintFillMesh.RecalculateBounds();
            return _footprintFillMesh;
        }
    }

    private static Material FootprintFillMaterial
    {
        get
        {
            if (_footprintFillMaterial != null) return _footprintFillMaterial;
            // Sprites/Default: a real Built-in-RP shader (this project's
            // rendering pipeline throughout), always available with no
            // import step, supports a plain translucent vertex/material
            // color out of the box — exactly what a flat colored fill
            // quad needs, no custom shader required.
            var shader = Shader.Find("Sprites/Default");
            _footprintFillMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave, color = FootprintFillColor };
            // Decals (Resources/SC2Decal.shader, queue
            // "Transparent+20" = 3020, plus its own depth Offset -2,-2 to
            // win the terrain z-fighting race) would otherwise render on top
            // of the footprint fill instead of the other way around —
            // Sprites/Default's own default queue is plain "Transparent" (3000),
            // lower than decals'. Push this comfortably above 3020 so the
            // footprint always wins, regardless of what else is placed on
            // the ground under it.
            _footprintFillMaterial.renderQueue = 3040;
            return _footprintFillMaterial;
        }
    }

    /// <summary>Call once per footprint that needs to be drawn this frame
    /// (from DrawFootprintOverlay, both the live placement preview and the
    /// per-placed-object loop). Reuses/creates the Nth pooled quad in draw
    /// order; ReconcileFootprintFillPool (called once per Repaint from
    /// OnGameViewGUI) deactivates whatever's left over.</summary>
    private void SyncFootprintFillQuad(Vector3 center, Quaternion rotation, float sizeX, float sizeZ, float groundY)
    {
        if (Event.current == null || Event.current.type != EventType.Repaint) return;
        var index = _footprintFillUsedThisFrame++;
        GameObject go;
        if (index < _footprintFillPool.Count)
        {
            go = _footprintFillPool[index];
        }
        else
        {
            go = new GameObject("SC2FootprintFill") { hideFlags = HideFlags.HideAndDontSave };
            go.AddComponent<MeshFilter>().sharedMesh = FootprintFillMesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = FootprintFillMaterial;
            _footprintFillPool.Add(go);
        }
        go.SetActive(true);
        go.transform.SetPositionAndRotation(new Vector3(center.x, groundY, center.z), rotation);
        go.transform.localScale = new Vector3(sizeX, 1f, sizeZ);
    }

    /// <summary>Deactivates any pooled footprint-fill quad not claimed this
    /// Repaint — must run exactly once per rendered frame, after every
    /// DrawFootprintOverlay call for that frame has already run (placement
    /// preview + every placed prop/unit's own footprint), or it would hide
    /// quads that simply hadn't been synced yet this same frame.</summary>
    private void ReconcileFootprintFillPool()
    {
        if (Event.current == null || Event.current.type != EventType.Repaint) return;
        if (_footprintFillFrameToken == Time.frameCount) return; // already reconciled this frame
        _footprintFillFrameToken = Time.frameCount;
        for (var i = _footprintFillUsedThisFrame; i < _footprintFillPool.Count; i++)
            if (_footprintFillPool[i] != null) _footprintFillPool[i].SetActive(false);
        _footprintFillUsedThisFrame = 0;
    }

    // ── Live rotation while a placement tool is armed (hold E, drag mouse
    // X) — mirrors FAForeverMapEditor's PlacementManager.Update() E-hold
    // gesture. Y-axis only, matching that reference (no per-blueprint pitch/
    // roll use case here). Sticky across repeated placements within one
    // armed session (this tool never auto-disarms after a click — see
    // ArmPlacement's own doc comment) so placing several objects in a row
    // keeps the last-set facing; only resets to 0 when the tool is disarmed,
    // so the next arm starts fresh. ────────────────────────────────────────
    private float _placementRotationY;
    private bool _placementRotating;
    private float _placementRotationStartMouseX;
    private float _placementRotationStartY;

    private void TickPlacementRotation(bool allowInput)
    {
        if (_placementKind == PlacementKind.None || !allowInput) { _placementRotating = false; return; }

        if (SC2Hotkeys.GetKeyDown("Objects.LiveRotateWhilePlacing"))
        {
            _placementRotating = true;
            _placementRotationStartMouseX = Input.mousePosition.x;
            _placementRotationStartY = _placementRotationY;
        }
        else if (SC2Hotkeys.GetKeyUp("Objects.LiveRotateWhilePlacing"))
        {
            _placementRotating = false;
        }
        else if (_placementRotating && SC2Hotkeys.GetKey("Objects.LiveRotateWhilePlacing"))
        {
            _placementRotationY = _placementRotationStartY - (Input.mousePosition.x - _placementRotationStartMouseX) * 0.5f;
        }
    }

    /// <summary>Continuous paint gesture for the Props brush — mirrors
    /// TryHandlePlacementClick's structure but handles MouseDown/MouseDrag/
    /// MouseUp as one stroke instead of a single click, so density
    /// accumulates the longer the user holds/drags over an area.</summary>
    private bool TryHandlePropsBrush(Camera camera, Rect viewportRect, Vector2 mousePosition, bool allowInput)
    {
        if (!_propsBrushActive) return false;
        var e = Event.current;
        if (e == null || !allowInput) return false;

        if (!_propsBrushDragging && e.type == EventType.MouseDown && e.button == 0)
        {
            if (!viewportRect.Contains(mousePosition)) return false;
            _propsBrushDragging = true;
            _propsBrushStrokeNewProps.Clear();
            // Props add/remove has no ISC2DocumentCommand support yet (only
            // position/name/rect edits do — see SC2SetPropPositionCommand),
            // so this must record the legacy undo checkpoint unconditionally,
            // the same as every other prop add/delete site in this file
            // (PlacePropAt, DeletePropsById, DuplicatePropWithSymmetry).
            // Skipping it whenever Session.Document exists left the whole
            // brush stroke with no registered undo channel at all.
            PushUndo();
            PaintPropsBrushTick(camera, viewportRect, mousePosition);
            e.Use();
            return true;
        }

        if (_propsBrushDragging && e.type == EventType.MouseDrag && e.button == 0)
        {
            PaintPropsBrushTick(camera, viewportRect, mousePosition);
            e.Use();
            return true;
        }

        if (_propsBrushDragging && (e.type == EventType.MouseUp || e.type == EventType.Ignore))
        {
            _propsBrushDragging = false;
            if (!EffectivePropsErasing()) MirrorBrushStroke();
            _propsBrushStrokeNewProps.Clear();
            // Same class of fix as
            // SC2GeometryMode/SC2PaintMode's MouseUp handlers: a single call
            // per completed stroke rather than per scatter/erase tick
            // (ScatterPropsAt/ErasePropsAt run many times per stroke).
            MarkObjectsDirty();
            RepaintHost?.Invoke();
            e.Use();
            return true;
        }

        return false;
    }

    private void PaintPropsBrushTick(Camera camera, Rect viewportRect, Vector2 mousePosition)
    {
        var ray = SC2TerrainShell.GameGuiPointToRay(camera, viewportRect, mousePosition);
        if (!TryRaycastImportedTerrain(ray.origin, ray.direction, out var hit) &&
            !SC2TerrainShell.RaycastTerrainHeight(_terrainShell.Runtime, ray, out hit))
            return;

        if (EffectivePropsErasing()) ErasePropsAt(hit);
        else ScatterPropsAt(hit);
        RepaintHost?.Invoke();
    }

    /// <summary>Single-prop counterpart to PlaceMarkerAt/PlaceUnitAt — a
    /// normal click-to-place for ONE prop, distinct from the Props brush's
    /// continuous multi-instance scatter gesture. Refuses (rather than
    /// guessing a blueprint) if the user somehow reaches this without
    /// OpenPropBrowserForPlacement having set one first.
    ///
    /// SampleHeight's raw raycast is not used for the resting height,
    /// because — on an imported map — it hits the decorative
    /// water surface itself (topmost geometry over any submerged area), so a
    /// prop could end up floating at the water's own rendered height. An
    /// explicit reject (e.g. height-vs-WaterElevation, or a
    /// raycast-hit-classification reject) is not viable either:
    /// real submerged ground (a lake/river bed, decorative underwater rocks,
    /// etc.) must stay placeable — and since the SAME click point can mean
    /// either "the water surface" or "the seabed under it" (a single top-
    /// down raycast can't tell those apart; water is always the topmost hit
    /// either way), no reject can distinguish the two. The only correct fix
    /// is to never let the water surface determine the resting height in the
    /// first place: SC2TerrainShell.SampleLiveHeightGrid (the same water-
    /// EXCLUDED height projection hfield.bdf/Collision2/Costs already use)
    /// always resolves to the real ground/seabed, so a prop can never float
    /// on water — dry land is unaffected, submerged ground works normally,
    /// and no explicit reject/block is needed at all.</summary>
    private void PlacePropAt(Vector3 position)
    {
        if (string.IsNullOrEmpty(_placementPropBlueprint))
        {
            _lastActionSummary = "Prop not placed — no blueprint chosen.";
            return;
        }

        position = SnapXZ(position);
        var groundHeight = _terrainShell.SampleLiveHeightGrid(position.x, position.z);
        var prop = new SC2ObjProp
        {
            Id = Data.AllocateObjectId(),
            BlueprintPath = _placementPropBlueprint,
            Position = new Vector3(position.x, groundHeight, position.z),
            Rotation = Quaternion.Euler(0f, _placementRotationY, 0f),
            Scale = 1f,
            AnchorToTerrain = true
        };
        Data.ObjProps.Add(prop);
        if (_placementPropAsset != null)
            SC2GameAssetScenePreview.TryShowProp(prop.Id, _placementPropAsset, _assetBrowser.GameDataRoot, prop.Position, prop.Rotation, prop.Scale, out _);
        if (Data.Symmetry != SymmetryMode.None)
            LinkPropsWithSymmetry(new List<SC2ObjProp> { prop });
        _lastActionSummary = $"Placed prop '{ShortBlueprint(prop.BlueprintPath)}'.";
        MarkObjectsDirty();
    }

    private void PlaceMarkerAt(Vector3 position, string markerType)
    {
        var marker = BuildMarkerPlacementCandidate(position, markerType);
        if (HasEquivalentPlacedMarker(marker))
        {
            _lastActionSummary = "Marker not placed: an equivalent marker already occupies that grid cell.";
            return;
        }
        var baseName = string.IsNullOrEmpty(markerType) ? "Marker" : markerType;
        marker.Id = Data.AllocateObjectId();
        marker.Name = MakeUniqueMarkerName($"{baseName}_{Data.ObjMarkers.Count + 1}");
        Data.ObjMarkers.Add(marker);
        if (Data.Symmetry != SymmetryMode.None)
            LinkMarkersWithSymmetry(new List<SC2ObjMarker> { marker });
        _lastActionSummary = $"Placed marker '{marker.Name}'.";
        MarkObjectsDirty();
    }

    /// <summary>Previously silently fell back to "ARMY_1" when armyName was
    /// empty, guessing which army a unit belongs to instead of refusing —
    /// exactly the class of silently-wrong-data bug this whole session kept
    /// finding on the Armies tab. The one real call site (DrawUnits'
    /// "Place in viewport..." button, armed per-army-section) always passes
    /// a real army name already, so this should never actually fire in
    /// practice — it's a defensive guard, not a normal path.</summary>
    private void PlaceUnitAt(Vector3 position, string armyName)
    {
        if (string.IsNullOrEmpty(armyName))
        {
            _lastActionSummary = "Unit not placed — no army selected.";
            return;
        }

        position = SnapXZ(position);
        // "uca0101" (this fallback's old value) doesn't exist in any real
        // SC2 unit blueprint corpus — checked directly against a real
        // gamedata.scd Units/ tree, which only ever has "ucaXXXX" ids for
        // 0103+ (Cybran); "0101"/"0102" were never real. Every unit placed
        // via the generic "Place in viewport..." (i.e. without first
        // picking a real blueprint through "Browse unit blueprints") got a
        // Type that could never resolve to a catalog asset, so
        // SyncMissingAssetPreviews's FindUnitAsset lookup silently failed
        // and no 3D model ever appeared. DefaultPlaceholderUnitType is a
        // real, confirmed-resolvable id instead (already used successfully
        // elsewhere this session for GPU-instancing verification).
        var type = string.IsNullOrEmpty(_placementUnitType) ? DefaultPlaceholderUnitType : _placementUnitType;
        // An army is not itself a group — placed units land in whichever
        // group the selected-group inspector armed placement for
        // (_placementTargetGroupId), else a resolved default group.
        int parentIdx;
        var targetIdx = _placementTargetGroupId != 0
            ? Data.ObjUnits.FindIndex(u => u.Id == _placementTargetGroupId && u.IsGroup && u.ArmyName == armyName)
            : -1;
        parentIdx = targetIdx >= 0 ? targetIdx : ResolveDefaultUnitGroup(armyName);
        var unit = new SC2ObjUnitNode
        {
            Id = Data.AllocateObjectId(), ArmyName = armyName, Name = MakeUniqueUnitName("Unit_" + (Data.ObjUnits.Count + 1)),
            ParentIndex = parentIdx, Type = type, Position = position, OrientationEuler = new Vector3(0f, _placementRotationY, 0f)
        };
        Data.ObjUnits.Add(unit);
        _lastActiveUnitId = unit.Id;
        if (Data.Symmetry != SymmetryMode.None)
            LinkUnitsWithSymmetry(new List<SC2ObjUnitNode> { unit });
        _lastActionSummary = $"Placed unit '{unit.Name}' ({armyName}).";
        MarkObjectsDirty();
    }

    /// <summary>Placing an Army is ONE action —
    /// creates the ARMY_* start-position marker AND its SC2ObjArmy metadata
    /// entry together, linked via SC2ObjArmy.MarkerId, instead of the two
    /// separate manual steps ("Add Start Position" + "Add Army") the old UI
    /// required. Symmetry-generated sibling markers get their own linked
    /// metadata entry too, so every army the symmetry group creates is
    /// immediately complete, not just the one the user clicked.</summary>
    private void PlaceArmyAt(Vector3 position)
    {
        position = SnapXZ(position);
        if (HasArmyAt(position))
        {
            _lastActionSummary = "Army not placed: a start-position marker already occupies that grid cell.";
            return;
        }
        var name = NextArmyName();
        var marker = new SC2ObjMarker { Id = Data.AllocateObjectId(), Name = name, Type = "Blank Marker", Position = position, OrientationEuler = new Vector3(0f, _placementRotationY, 0f) };
        Data.ObjMarkers.Add(marker);
        EnsureArmyMetadataLinked(marker);

        if (Data.Symmetry != SymmetryMode.None)
        {
            LinkMarkersWithSymmetry(new List<SC2ObjMarker> { marker });
            var group = FindSymmetryGroup(marker.Id);
            if (group != null)
                foreach (var mem in group.Members)
                {
                    var sibling = Data.ObjMarkers.FirstOrDefault(x => x.Id == mem.ObjectId);
                    if (sibling != null && ArmyPositionRe.IsMatch(sibling.Name))
                        EnsureArmyMetadataLinked(sibling);
                }
        }
        _lastActionSummary = $"Placed army '{name}' (marker + metadata linked).";
        MarkObjectsDirty();
    }

    private void EnsureArmyMetadataLinked(SC2ObjMarker m)
    {
        if (Data.ObjArmies.Any(a => a.MarkerId == m.Id)) return;
        // Guard by name too, not just MarkerId — _save.lua's Armies table is
        // a real Lua table keyed by name, so two SC2ObjArmy entries sharing
        // a name is always wrong data, never two legitimately-separate
        // armies. Link the existing unlinked entry instead of creating a
        // second one under the same name (this is what previously let a
        // duplicate-named army get created — see DrawArmies' own desync
        // detection for the matching cleanup UI on already-existing data).
        var existingByName = Data.ObjArmies.FirstOrDefault(a => string.Equals(a.Name, m.Name, StringComparison.OrdinalIgnoreCase));
        if (existingByName != null)
        {
            if (existingByName.MarkerId == 0) existingByName.MarkerId = m.Id;
            return;
        }
        Data.ObjArmies.Add(new SC2ObjArmy { Id = Data.AllocateObjectId(), Name = m.Name, MarkerId = m.Id });
    }

    /// <summary>The other direction of the same desync DrawArmies' own
    /// detection UI fixes: deleting an Army from the bottom list ("X"
    /// button) must not only remove Data.ObjArmies, leaving its Start
    /// Position marker behind in the top list forever and the two lists
    /// out of sync. Matches the marker by
    /// MarkerId when linked (armies created/renamed through this session's
    /// linking path), falling back to matching by Name for armies imported
    /// straight from a real map's _save.lua (PopulateArmiesFromSaveLuaTree
    /// never sets MarkerId at all) — same ARMY_N name convention the rest
    /// of this file already relies on. Deliberately does NOT expand to
    /// symmetric siblings the way DeleteMarkersById does — each ARMY_N is
    /// its own army, so deleting one shouldn't silently delete a different
    /// army's marker just because they're symmetry-linked.</summary>
    private void RemoveArmyAndLinkedMarker(SC2ObjArmy army)
    {
        Data.ObjArmies.Remove(army);
        MarkObjectsDirty();

        var marker = army.MarkerId != 0
            ? Data.ObjMarkers.FirstOrDefault(m => m.Id == army.MarkerId)
            : Data.ObjMarkers.FirstOrDefault(m => ArmyPositionRe.IsMatch(m.Name) && string.Equals(m.Name, army.Name, StringComparison.OrdinalIgnoreCase));
        if (marker == null) return;

        Data.ObjMarkers.Remove(marker);
        var markerIds = new HashSet<int> { marker.Id };
        foreach (var graph in Data.ObjGraphs) SC2ObjectGraphRefs.RemoveEdgesReferencing(graph, markerIds);
        foreach (var chain in Data.ObjChains) chain.MarkerNames.RemoveAll(name => string.Equals(name, marker.Name, StringComparison.OrdinalIgnoreCase));
        _selMarkers.Remove(marker.Id);
    }

    public void OnGameViewGUI(Camera camera, Rect viewportRect, Vector2 mousePosition, bool allowInput,
        string activeTool, bool showProps, bool showUnits, bool showMarkers,
        bool showGraphLand, bool showGraphAir, bool showGraphNaval, bool showGraphAmphibious,
        bool showChains, bool showAreas, bool objectsTabActive)
    {
        if (!IsReady || camera == null) return;
        Data.EnsureObjectIds();
        EnsureObjectsDocumentSynced();
        // Unlike the 3D prop/unit preview MODELS (which
        // should stay visible cross-tab — see EnsureAssetsIndexed/
        // SyncMissingAssetPreviews' own comments), the small 2D prop pick-
        // dot/icon should only show while the Props sub-tab is actually
        // open, not on every tab just because Visibility > Props defaults
        // on. Scoped to props only — Units/Markers keep
        // their existing cross-tab icon behavior unchanged.
        var showPropsIcon = showProps && objectsTabActive && _lastSubTab == TabProps;
        // Real 3D prop/unit preview models (SC2GameAssetScenePreview) —
        // moved here from DrawEmbeddedGUI (see that method's own comment)
        // for the same reason markers were: this method already runs
        // cross-tab whenever any Objects overlay flag is on (true by
        // default), not just while the Objects tab itself is open. Keeps
        // the exact same "at most once per rendered frame, and only when
        // something could actually have changed" gating DrawEmbeddedGUI
        // used to do directly — this method is called once per OnGUI
        // event (Layout/Repaint/MouseMove/...), same multiple-times-per-
        // frame concern the original perf fix (_dataVersion's own doc
        // comment) was written against.
        if (Event.current == null || Event.current.type == EventType.Repaint)
        {
            _previewSyncSafetyNetTicks++;
            var dueForSafetyNet = _previewSyncSafetyNetTicks >= PreviewSyncSafetyNetInterval;
            if (dueForSafetyNet || _dragActive || _propsGestureKind != PropsGestureKind.None || _dataVersion != _lastSyncedDataVersion)
            {
                SC2GameAssetScenePreview.SyncTransforms(Data, _hiddenUnitIds, _hiddenPropIds);
                SyncMissingAssetPreviews();
                _lastSyncedDataVersion = _dataVersion;
                if (dueForSafetyNet) _previewSyncSafetyNetTicks = 0;
            }
        }
        // Real 3D marker gizmo models (SC2MarkerScenePreview), same "gate
        // the actual model, not just the flat 2D dot" fix as Props/Units'
        // own HideProps/HideUnits — toggling Markers off
        // in Visibility used to hide the dot but leave the model on screen. Moved
        // here (this method already runs cross-tab whenever any Objects
        // overlay flag is on — see HandleObjectsGameViewInput) from
        // DrawEmbeddedGUI (which only ran while the Objects tab itself was
        // open), so markers now also stay in sync on other tabs the same
        // way Props/Units now do too.
        SC2MarkerScenePreview.Sync(Data, _hiddenMarkerIds, showMarkers);
        // GPU-instanced viewport pick-dots (SC2ObjectMarkerRenderer,
        // user-specced) — same cross-tab reasoning as the 3D marker sync
        // directly above, dirty-gated internally so this is cheap to call
        // unconditionally here regardless of which overlay flags are on.
        // Props specifically uses showPropsIcon (Props-sub-tab-only), not
        // showProps — see that variable's own comment.
        if (!_legacyImguiMarkers) SyncMarkerRendererIfNeeded(showPropsIcon, showUnits, showMarkers);

        if ((_placementKind != PlacementKind.None || _propsBrushActive) && allowInput && Event.current != null &&
            Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Escape)
        {
            CancelActiveTools();
            Event.current.Use();
            RepaintHost?.Invoke();
            return;
        }

        // Ctrl+R hotkey for the Paths tab's own "Connect selected (mesh)"
        // button (DrawPaths). Checked (with its own
        // Ctrl requirement) before the plain-R Props-rotate gesture below,
        // so the two never conflict regardless of order. HandleObjectsGame
        // ViewInput (which calls this method) already runs earlier in the
        // same OnGUI frame than SC2GameViewEditorUI.HandleHotkeys(), so
        // consuming it here is enough — no change needed on that side.
        // Same allowInput gate as the Escape/Delete handlers in this method
        // (mouse over the viewport, Objects tab active), and the same
        // "2+ selected markers, a graph is selected" precondition the
        // button itself already requires.
        if (allowInput && _lastSubTab == TabPaths && Event.current != null &&
            Event.current.type == EventType.KeyDown && SC2Hotkeys.Matches("Objects.ConnectMarkers", Event.current) &&
            _selMarkers.Count >= 2)
        {
            var connectGraph = Data.ObjGraphs.FirstOrDefault(x => x.Id == _selectedGraphId);
            if (connectGraph != null)
            {
                PushUndo();
                ConnectSelectedMarkersMesh(connectGraph);
                Event.current.Use();
                RepaintHost?.Invoke();
                return;
            }
        }

        // Objects.RotateGesture/ScaleGesture (default R/S) — interactive
        // rotate/scale gesture for the current Props selection, no
        // tool-switch/hold needed (mirrors the drag-to-move gesture's own
        // "press, move mouse, click/Enter to confirm, Esc to cancel" shape).
        // SC2Hotkeys.Matches requires an exact modifier match, so this
        // naturally doesn't also fire for the Ctrl+R Paths handler above
        // without needing an explicit exclusion here. Requires an existing
        // Props selection so a stray press with nothing selected does
        // nothing. Takes over the frame entirely while active, same as the
        // brush-radius-resize gesture below.
        if (_propsGestureKind == PropsGestureKind.None && allowInput && !_dragActive && !_boxSelectActive && _selProps.Count > 0 &&
            Event.current != null && Event.current.type == EventType.KeyDown &&
            (SC2Hotkeys.Matches("Objects.RotateGesture", Event.current) || SC2Hotkeys.Matches("Objects.ScaleGesture", Event.current)) &&
            TryComputeGesturePivot(out var gesturePivot) && TryWorldToGui(camera, viewportRect, gesturePivot, out var gestureAnchorScreen))
        {
            _propsGestureKind = SC2Hotkeys.Matches("Objects.RotateGesture", Event.current) ? PropsGestureKind.Rotate : PropsGestureKind.Scale;
            _gestureAnchorScreen = gestureAnchorScreen;
            _gestureStartMouse = mousePosition;
            _gestureStartDistance = Mathf.Max(1f, Vector2.Distance(gestureAnchorScreen, mousePosition));
            _gestureItems.Clear();
            foreach (var id in _selProps)
            {
                var p = Data.ObjProps.FirstOrDefault(x => x.Id == id);
                if (p != null) _gestureItems.Add((p, p.Rotation, p.Scale));
            }
            PushUndo();
            Event.current.Use();
            RepaintHost?.Invoke();
            return;
        }
        if (_propsGestureKind != PropsGestureKind.None)
        {
            TickPropsGesture(mousePosition);
            return;
        }

        TickPlacementRotation(allowInput);

        // F brush-radius resize takes over the frame entirely while
        // active (or just starting) — checked before the brush's own cursor
        // draw/click handling below, same reasoning as the Escape/Ctrl+R
        // handlers above.
        if (HandlePropsBrushRadiusResize(camera, viewportRect, mousePosition, allowInput))
            return;

        // Drawn every frame the brush is armed (not just while painting) so
        // the user can see the radius/where it'll land before clicking -
        // DrawGameViewBrush itself no-ops outside Repaint events, so this is
        // safe to call unconditionally alongside the click/drag handling
        // below rather than only on a subset of event types.
        DrawPropsBrushCursor(camera, viewportRect, mousePosition);
        // Same reasoning as the brush cursor above - shown every frame a
        // Marker/Unit/Army placement tool is armed, so the user sees what
        // will land (including auto-created symmetry siblings) before
        // clicking, not just after.
        DrawPlacementPreview(camera, viewportRect, mousePosition);
        // Must run after every DrawFootprintOverlay call this frame has
        // already happened (the props/units pick-dot loop above, plus the
        // placement preview just above this line) — deactivates any pooled
        // footprint-fill quad nothing claimed this Repaint.
        ReconcileFootprintFillPool();
        // Drawn every frame a box-select drag is in progress, same
        // unconditional-call/no-op-outside-Repaint pattern as the two calls
        // above.
        DrawBoxSelectOverlay(mousePosition);

        if (TryHandlePropsBrush(camera, viewportRect, mousePosition, allowInput))
            return;

        if (TryHandlePlacementClick(camera, viewportRect, mousePosition, allowInput))
            return;

        var pick = new ViewportPick { Distance = 15f };
        var canSelect = allowInput && string.Equals(activeTool, "Select", StringComparison.OrdinalIgnoreCase);
        // Moving is unconditional default behavior now (per direct
        // instruction — G removed entirely, no separate "Move tool" to
        // switch into): click-drag on an already-selected Prop/Marker/Unit
        // just works whenever the Select tool is active, which is both the
        // default and, with the Move/Rotate/Scale tool-switch hotkeys gone,
        // the only real interaction tool left. canMove and canSelect are
        // therefore identical now — kept as two separate locals only for
        // readability at each call site below, not because they can differ.
        var canMove = canSelect;
        var canPick = canSelect;

        if (showAreas) DrawZoneOverlays(camera, viewportRect, mousePosition, canPick, ref pick);
        if (showGraphLand || showGraphAir || showGraphNaval || showGraphAmphibious || showChains)
        {
            // Isolated in its own try/catch, not just relying on the outer
            // HandleObjectsGameViewInput wrapper — an exception ANYWHERE
            // earlier in this one large method (which also handles Del/
            // Ctrl+C+V/box-select/drag, all further down) would otherwise
            // silently disable ALL of those for the rest of that frame, not
            // just break the edge-line overlay itself. Without this
            // isolation, an earlier exception (e.g. a malformed/dangling
            // edge) would abort the method before reaching the MouseDown
            // handling further down, on every subsequent frame — box-select's
            // marquee rectangle would never appear at all after creating a
            // graph edge.
            try
            {
                // Per-kind visibility (Visibility menu shows 2 or 4 separate
                // Path toggles now, one per real graph kind, instead of one
                // blanket "Graphs" checkbox) — DrawGraphOverlays itself
                // decides per-graph which of these 4 flags applies.
                DrawGraphOverlays(camera, viewportRect, showGraphLand, showGraphAir, showGraphNaval, showGraphAmphibious);
                if (showChains) DrawChainOverlays(camera, viewportRect);
            }
            catch (Exception ex)
            {
                SC2Log.LogException(ex);
            }
        }
        if (showProps)
            foreach (var prop in VisibleForViewport(Data.ObjProps, camera, viewportRect, p => p.Position))
            {
                if (string.IsNullOrEmpty(prop.BlueprintPath)) DrawInvalidRing(camera, viewportRect, prop.Position);
                // Clicking/box-selecting
                // on Markers (or any non-Props sub-tab) still picked up and
                // selected Props underneath the cursor, even though their
                // own pick-dot/icon is already correctly hidden outside the
                // Props sub-tab (showPropsIcon, above). That hide only ever
                // covered the RENDERED dot; this loop's actual click-picking
                // still ran unconditionally off the broader showProps flag.
                // canPick is now also gated on showPropsIcon so a Prop can't
                // be newly selected while its own dot isn't even shown —
                // an ALREADY-selected Prop (from before switching tabs)
                // still keeps working exactly as before (see
                // TryGetSelectionCentroid's own group-handle carve-out,
                // unchanged), and the footprint overlay below stays on its
                // own showProps gate, unaffected by this.
                DrawViewportDot(camera, viewportRect, mousePosition, canPick && showPropsIcon, prop.Position, prop.Id, ViewportObjectKind.Prop,
                    _selProps.Contains(prop.Id) ? new Color(1f, .9f, .12f, .95f) : new Color(.25f, .86f, 1f, .75f), ref pick);
                // Footprint should show for ALREADY-
                // PLACED buildings too, not just the live placement preview
                // (which DrawPlacementPreview already covers). Gated on the
                // same showProps flag as this prop's own pick-dot, so
                // hiding Props also hides their footprints.
                var propAsset = _assetBrowser.FindPropAsset(prop.BlueprintPath);
                if (propAsset != null && propAsset.FootprintSizeX > 0f && propAsset.FootprintSizeZ > 0f)
                    DrawFootprintOverlay(camera, viewportRect, prop.Position, propAsset.FootprintSizeX, propAsset.FootprintSizeZ, prop.Rotation.eulerAngles.y, PlacedFootprintColor);
            }
        if (showUnits)
            foreach (var unit in VisibleForViewport(Data.ObjUnits.Where(u => !u.IsGroup && !_hiddenUnitIds.Contains(u.Id)), camera, viewportRect, u => u.Position))
            {
                DrawViewportDot(camera, viewportRect, mousePosition, canPick, unit.Position, unit.Id, ViewportObjectKind.Unit,
                    _selUnits.Contains(unit.Id) ? new Color(1f, .9f, .12f, .95f) : new Color(.86f, .45f, 1f, .78f), ref pick);
                // Same as Props above — real buildings in this game's data
                // are UNIT blueprints (structures), so this is where most
                // Footprint-bearing objects actually live.
                var unitAsset = _assetBrowser.FindUnitAsset(unit.Type);
                if (unitAsset != null && unitAsset.FootprintSizeX > 0f && unitAsset.FootprintSizeZ > 0f)
                    DrawFootprintOverlay(camera, viewportRect, unit.Position, unitAsset.FootprintSizeX, unitAsset.FootprintSizeZ, unit.OrientationEuler.y, PlacedFootprintColor);
            }
        if (showMarkers)
            foreach (var marker in VisibleForViewport(Data.ObjMarkers, camera, viewportRect, m => m.Position))
            {
                if (_hiddenMarkerIds.Contains(marker.Id)) continue;
                var template = SC2MarkerTemplates.Find(marker.Type);
                var invalid = string.IsNullOrEmpty(marker.Type) || string.IsNullOrEmpty(marker.Name);
                if (invalid) DrawInvalidRing(camera, viewportRect, marker.Position);
                var color = template != null ? SC2MarkerTemplates.HexToColor(marker.ColorHex) : new Color(.35f, .9f, .95f, .75f);
                if (_selMarkers.Contains(marker.Id)) color = new Color(1f, .9f, .12f, .98f);
                DrawViewportDot(camera, viewportRect, mousePosition, canPick, marker.Position, marker.Id, ViewportObjectKind.Marker, color, ref pick);
            }
        else if (_selMarkers.Count > 0)
            // Selected markers stay visible/pickable even with the Markers
            // overlay off — a common Paths/Chains workflow is to hide marker
            // clutter and keep only the Graphs edge-line overlay on, but a
            // box-select (or "Connect selected") there still updates
            // _selMarkers correctly; with nothing drawn to show it, that
            // would otherwise look exactly like box-select silently not
            // working on those tabs. Only the currently-selected
            // markers get a dot here, not the whole overlay — unselected
            // markers stay hidden as the toggle intends. No MaxViewportDots
            // cap here at all — this loop already only draws SELECTED
            // markers (naturally bounded by however many the user actually
            // selected, never the whole map's marker count), and the old
            // Take(MaxViewportDots) capped which markers were even
            // CONSIDERED before the selection filter ran, so a selected
            // marker past index 400 in the list silently never got a dot.
            foreach (var marker in Data.ObjMarkers)
                if (_selMarkers.Contains(marker.Id) && !_hiddenMarkerIds.Contains(marker.Id))
                    DrawViewportDot(camera, viewportRect, mousePosition, canPick, marker.Position, marker.Id,
                        ViewportObjectKind.Marker, new Color(1f, .9f, .12f, .98f), ref pick);

        // Group move handle: a distinct, larger glyph at the centroid of a
        // multi-object selection (2+ Props/Markers/Units), so there is
        // something on screen showing where to grab to drag a whole
        // selected group. The group-drag mechanism itself already works
        // (click ANY already-selected object and drag — see
        // HandleViewportDrag's "Grab (Blender-style G)" comment), so this
        // glyph doesn't change that; it's just an additional, visible pick
        // target that hits the exact same path via ViewportObjectKind.GroupHandle.
        if (TryGetSelectionCentroid(out var groupCentroid) && TryWorldToGui(camera, viewportRect, groupCentroid, out var groupGui))
        {
            DrawGroupHandleGlyph(groupGui);
            if (canPick)
            {
                var handleDistance = Vector2.Distance(groupGui, mousePosition);
                if (handleDistance < pick.Distance) pick = new ViewportPick { Kind = ViewportObjectKind.GroupHandle, Id = 0, Distance = handleDistance };
            }
        }

        // Hover highlight — in GPU mode (default), Prop/Unit/Marker dots
        // above are drawn entirely by SC2ObjectMarkerRenderer (via its own
        // Sync call below), never by DrawViewportGlyph, so there's nothing
        // for the user to see under the cursor unless something explicitly
        // highlights it. Resolve whatever `pick` ended up landing on (the
        // true nearest candidate under the cursor across every kind
        // checked above) and feed it to the GPU renderer's single hover
        // slot; in legacy IMGUI mode every dot is already drawn directly
        // below via DrawViewportDot, so this instead draws one extra
        // brighter glyph on top, same as before this GPU migration.
        {
            Vector3? hoverWorld = canPick ? pick.Kind switch
            {
                ViewportObjectKind.Prop => Data.ObjProps.FirstOrDefault(x => x.Id == pick.Id)?.Position,
                ViewportObjectKind.Unit => Data.ObjUnits.FirstOrDefault(x => x.Id == pick.Id)?.Position,
                ViewportObjectKind.Marker => Data.ObjMarkers.FirstOrDefault(x => x.Id == pick.Id)?.Position,
                _ => null
            } : null;

            Color? hoverColor = hoverWorld.HasValue ? pick.Kind switch
            {
                ViewportObjectKind.Prop => _selProps.Contains(pick.Id) ? new Color(1f, .9f, .12f, .95f) : new Color(.25f, .86f, 1f, .95f),
                ViewportObjectKind.Unit => _selUnits.Contains(pick.Id) ? new Color(1f, .9f, .12f, .95f) : new Color(.86f, .45f, 1f, .95f),
                ViewportObjectKind.Marker => _selMarkers.Contains(pick.Id) ? new Color(1f, .9f, .12f, .98f) : new Color(.35f, .9f, .95f, .95f),
                _ => (Color?)null
            } : null;

            if (_legacyImguiMarkers)
            {
                if (hoverWorld.HasValue && hoverColor.HasValue && TryWorldToGui(camera, viewportRect, hoverWorld.Value, out var hoverGui))
                    DrawViewportGlyph(hoverGui, hoverColor.Value, pick.Kind);
            }
            else
            {
                SC2ObjectMarkerRenderer.SetHover(hoverWorld, hoverColor ?? Color.white, pick.Kind == ViewportObjectKind.Marker);
            }
        }

        var e = Event.current;

        // Del/Backspace deletes the current SELECTION (a marker, a prop, a
        // unit, or a whole multi-select from box-select) when there is one —
        // including symmetric siblings, via DeleteMarkersById/
        // DeletePropsById/DeleteUnitsById. Only falls back to "whatever's
        // under the cursor right now" (the older hover-delete behavior)
        // when nothing is actually selected.
        if (canPick && e != null && e.type == EventType.KeyDown && SC2Hotkeys.Matches("Objects.DeleteSelection", e))
        {
            if (_selMarkers.Count > 0 || _selProps.Count > 0 || _selUnits.Count > 0)
            {
                PushUndo();
                if (_selMarkers.Count > 0) DeleteMarkersById(new HashSet<int>(_selMarkers));
                if (_selProps.Count > 0) DeletePropsById(new HashSet<int>(_selProps));
                if (_selUnits.Count > 0) DeleteUnitsById(new HashSet<int>(_selUnits));
                e.Use();
                RepaintHost?.Invoke();
                return;
            }
            if (pick.Kind != ViewportObjectKind.None)
            {
                PushUndo();
                DeleteHoveredObject(pick);
                e.Use();
                RepaintHost?.Invoke();
                return;
            }
        }

        // Ctrl+C / Ctrl+V — copy and paste the current Prop/Marker/Unit
        // selection, same "act on whatever's selected" convention as
        // Del/Backspace above.
        if (canPick && e != null && e.type == EventType.KeyDown && SC2Hotkeys.Matches("Objects.Copy", e))
        {
            CopySelectionToClipboard();
            e.Use();
            RepaintHost?.Invoke();
            return;
        }
        if (canPick && e != null && e.type == EventType.KeyDown && SC2Hotkeys.Matches("Objects.Paste", e))
        {
            PasteClipboard();
            e.Use();
            RepaintHost?.Invoke();
            return;
        }

        if (canSelect && e != null && e.type == EventType.MouseDown && e.button == 0)
        {
            // Zones have no drag-move support (HandleViewportDrag only knows
            // Prop/Marker/Unit), and an armed Node A/B path-node pick means
            // the click is choosing a graph endpoint, not starting a move —
            // both keep the old immediate-select-and-consume behavior. Every
            // other pick (Prop/Marker/Unit, the common case) falls through
            // to HandleViewportDrag below instead of being consumed here, so
            // the same click both selects (if not already selected) and arms
            // a drag — see the canMove comment above for why. GroupHandle is
            // excluded from the armed-path-pick branch too: it's not a real
            // Prop/Marker/Unit SelectViewportObject could resolve — falling
            // into that switch's unhandled case would silently CLEAR the
            // whole selection (its default non-additive-click behavior)
            // instead of arming a group drag.
            if (pick.Kind == ViewportObjectKind.Zone || (_pathPickMode != 0 && pick.Kind != ViewportObjectKind.None && pick.Kind != ViewportObjectKind.GroupHandle))
            {
                SelectViewportObject(pick);
                e.Use();
                RepaintHost?.Invoke();
                return;
            }
            if (pick.Kind == ViewportObjectKind.None)
            {
                // Empty space in Select mode starts a box/marquee drag
                // instead of doing nothing — see FinishBoxSelect for what
                // release does. Also cancels an armed Node A/B path-node
                // pick (clicking empty space reads as "never mind", not as
                // leaving it silently stuck armed for whatever the NEXT
                // click happens to be).
                _pathPickMode = 0;
                if (viewportRect.Contains(mousePosition))
                {
                    _boxSelectActive = true;
                    _boxSelectStartScreen = mousePosition;
                    e.Use();
                    return;
                }
            }
        }

        if (_boxSelectActive)
        {
            if (e != null && e.type == EventType.MouseDrag && e.button == 0) { e.Use(); RepaintHost?.Invoke(); return; }
            if (e != null && (e.type == EventType.MouseUp || e.type == EventType.Ignore))
            {
                FinishBoxSelect(camera, viewportRect, mousePosition);
                e?.Use();
                RepaintHost?.Invoke();
                return;
            }
        }

        HandleViewportDrag(camera, viewportRect, mousePosition, canMove, pick);
    }

    /// <summary>Which viewport object kind a box-select on the current
    /// Objects sub-tab should target — mirrors the tab you'd naturally
    /// expect to be selecting from; Armies/Paths/Zones/Chains have no box-
    /// select target (Armies works through markers/units directly, Paths/
    /// Chains work through explicit multi-pick, Zones are edited by
    /// dragging their own corner handles, not a marquee).</summary>
    private ViewportObjectKind BoxSelectTargetKind() => _lastSubTab switch
    {
        TabProps => ViewportObjectKind.Prop,
        TabUnits => ViewportObjectKind.Unit,
        // Paths/Chains both multi-select MARKERS (path nodes / waypoints)
        // as the input to their own "Connect selected" actions — box-select
        // targets markers there too, not just when the Markers tab itself
        // is open.
        TabMarkers or TabPaths or TabChains => ViewportObjectKind.Marker,
        _ => ViewportObjectKind.None
    };

    private void DrawBoxSelectOverlay(Vector2 mousePosition)
    {
        if (!_boxSelectActive || Event.current == null || Event.current.type != EventType.Repaint) return;
        var rect = Rect.MinMaxRect(
            Mathf.Min(_boxSelectStartScreen.x, mousePosition.x), Mathf.Min(_boxSelectStartScreen.y, mousePosition.y),
            Mathf.Max(_boxSelectStartScreen.x, mousePosition.x), Mathf.Max(_boxSelectStartScreen.y, mousePosition.y));
        var oldColor = GUI.color;
        GUI.color = new Color(1f, .9f, .12f, .12f);
        GUI.DrawTexture(rect, Texture2D.whiteTexture);
        GUI.color = new Color(1f, .9f, .12f, .9f);
        const float t = 1.5f;
        GUI.DrawTexture(new Rect(rect.x, rect.y, rect.width, t), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(rect.x, rect.yMax - t, rect.width, t), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(rect.x, rect.y, t, rect.height), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(rect.xMax - t, rect.y, t, rect.height), Texture2D.whiteTexture);
        GUI.color = oldColor;
    }

    /// <summary>Selects every object of BoxSelectTargetKind() whose
    /// projected viewport position falls inside the dragged rectangle. A
    /// near-zero-area drag (an effective click on empty space) clears the
    /// target kind's selection instead, matching the common "click empty
    /// space to deselect" convention rather than selecting nothing and
    /// leaving a stale selection behind. Shift OR Ctrl held adds to the
    /// existing selection (Shift added directly per request — the more
    /// conventional additive-box-select modifier in most editors; Ctrl kept
    /// too since SelectViewportObject's own single-object additive pick
    /// already used it and existing muscle memory shouldn't break);
    /// otherwise the box-select replaces the selection.</summary>
    private void FinishBoxSelect(Camera camera, Rect viewportRect, Vector2 mousePosition)
    {
        _boxSelectActive = false;
        var rect = Rect.MinMaxRect(
            Mathf.Min(_boxSelectStartScreen.x, mousePosition.x), Mathf.Min(_boxSelectStartScreen.y, mousePosition.y),
            Mathf.Max(_boxSelectStartScreen.x, mousePosition.x), Mathf.Max(_boxSelectStartScreen.y, mousePosition.y));
        var additive = SC2Hotkeys.ModifierHeld("Objects.BoxSelectAdditiveModifier", Event.current);
        var targetKind = BoxSelectTargetKind();

        if (rect.width < 4f && rect.height < 4f)
        {
            if (!additive)
            {
                switch (targetKind)
                {
                    case ViewportObjectKind.Prop: _selProps.Clear(); break;
                    case ViewportObjectKind.Unit: _selUnits.Clear(); break;
                    case ViewportObjectKind.Marker: _selMarkers.Clear(); break;
                }
                _lastActionSummary = "Selection cleared.";
            }
            return;
        }
        if (targetKind == ViewportObjectKind.None) { _lastActionSummary = "Box select: open the Props/Units/Markers panel first."; return; }

        var found = new List<int>();
        switch (targetKind)
        {
            case ViewportObjectKind.Prop:
                foreach (var prop in Data.ObjProps)
                    if (TryWorldToGui(camera, viewportRect, prop.Position, out var g) && rect.Contains(g)) found.Add(prop.Id);
                if (!additive) _selProps.Clear();
                foreach (var id in found) _selProps.Add(id);
                break;
            case ViewportObjectKind.Unit:
                foreach (var unit in Data.ObjUnits.Where(u => !u.IsGroup && !_hiddenUnitIds.Contains(u.Id)))
                    if (TryWorldToGui(camera, viewportRect, unit.Position, out var g) && rect.Contains(g)) found.Add(unit.Id);
                if (!additive) _selUnits.Clear();
                foreach (var id in found) _selUnits.Add(id);
                break;
            case ViewportObjectKind.Marker:
                foreach (var marker in Data.ObjMarkers)
                    if (TryWorldToGui(camera, viewportRect, marker.Position, out var g) && rect.Contains(g)) found.Add(marker.Id);
                if (!additive) _selMarkers.Clear();
                foreach (var id in found) _selMarkers.Add(id);
                break;
        }
        _lastActionSummary = $"Box-selected {found.Count} {targetKind.ToString().ToLowerInvariant()}(s).";
        ReportStatus?.Invoke(_lastActionSummary);
    }

    /// <summary>Deletes whatever the viewport pick currently points at,
    /// mirroring each category's existing "X"/"Delete selected" cleanup
    /// (graph/chain reference removal for markers, subtree removal for
    /// grouped units) so a hover-delete can't leave dangling references
    /// a selection-based delete would have caught.</summary>
    private void DeleteHoveredObject(ViewportPick pick)
    {
        switch (pick.Kind)
        {
            case ViewportObjectKind.Prop:
                DeletePropsById(new HashSet<int> { pick.Id }); // sets its own _lastActionSummary
                break;
            case ViewportObjectKind.Marker:
                DeleteMarkersById(new HashSet<int> { pick.Id }); // sets its own _lastActionSummary
                break;
            case ViewportObjectKind.Unit:
                var idx = Data.ObjUnits.FindIndex(u => u.Id == pick.Id);
                if (idx >= 0) RemoveUnitSubtree(idx);
                _selUnits.Remove(pick.Id);
                _lastActionSummary = "Deleted unit under cursor.";
                MarkObjectsDirty();
                break;
            case ViewportObjectKind.Zone:
                var zone = Data.ObjZones.FirstOrDefault(z => z.Id == pick.Id);
                if (zone != null) Data.ObjZones.Remove(zone);
                if (_selectedZoneId == pick.Id) _selectedZoneId = 0;
                _lastActionSummary = "Deleted area under cursor.";
                MarkObjectsDirty();
                break;
        }
        ReportStatus?.Invoke(_lastActionSummary);
    }

    private void HandleViewportDrag(Camera camera, Rect viewportRect, Vector2 mousePosition, bool canMove, ViewportPick pick)
    {
        var e = Event.current;
        if (e == null) return;

        if (canMove && !_dragActive && e.type == EventType.MouseDown && e.button == 0)
        {
            if (pick.Kind == ViewportObjectKind.None || pick.Kind == ViewportObjectKind.Zone)
            {
                _lastActionSummary = "Move tool: nothing pickable under the cursor (click closer to a marker/prop dot).";
                return;
            }

            // Grab (Blender-style "G"): if you click an already-selected
            // item, the whole selection drags together; clicking an
            // unselected one selects just that one first. GroupHandle is
            // always "already selected" — it has no single object identity
            // of its own, it just represents the current whole selection
            // (see DrawGroupHandleGlyph), so there is nothing to select.
            var alreadySelected = pick.Kind switch
            {
                ViewportObjectKind.Prop => _selProps.Contains(pick.Id),
                ViewportObjectKind.Marker => _selMarkers.Contains(pick.Id),
                ViewportObjectKind.Unit => _selUnits.Contains(pick.Id),
                ViewportObjectKind.GroupHandle => true,
                _ => false
            };
            if (!alreadySelected) SelectViewportObject(pick);

            if (!TryGetDragPoint(camera, viewportRect, mousePosition, out _dragStartWorld))
            {
                _lastActionSummary = "Move tool: could not find a drag point under the cursor.";
                return;
            }
            // When a real map session is wired,
            // this gesture is committed as a Command-sourced step at MouseUp
            // instead (CommitDragPositions) — pushing a legacy snapshot here
            // AS WELL would double-record the same drag under two different
            // undo sources. Without a session, fall back to the original
            // legacy-only behavior unchanged.
            if (Session?.Document == null)
                PushUndo();
            _dragItems.Clear();
            foreach (var id in _selProps) { var p = Data.ObjProps.FirstOrDefault(x => x.Id == id); if (p != null) _dragItems.Add((ViewportObjectKind.Prop, id, p.Position)); }
            foreach (var id in _selMarkers) { var m = Data.ObjMarkers.FirstOrDefault(x => x.Id == id); if (m != null) _dragItems.Add((ViewportObjectKind.Marker, id, m.Position)); }
            foreach (var id in _selUnits) { var u = Data.ObjUnits.FirstOrDefault(x => x.Id == id); if (u != null) _dragItems.Add((ViewportObjectKind.Unit, id, u.Position)); }
            _dragActive = _dragItems.Count > 0;
            _lastActionSummary = _dragActive ? $"Dragging {_dragItems.Count} object(s)..." : "Move tool: selection became empty, nothing to drag.";
            e.Use();
            return;
        }

        if (_dragActive && e.type == EventType.MouseDrag && e.button == 0)
        {
            if (TryGetDragPoint(camera, viewportRect, mousePosition, out var currentWorld))
            {
                var delta = currentWorld - _dragStartWorld;
                foreach (var (kind, id, originalPos) in _dragItems)
                {
                    var newPos = originalPos + delta;
                    if (kind == ViewportObjectKind.Prop)
                    {
                        var p = Data.ObjProps.FirstOrDefault(x => x.Id == id);
                        if (p == null) continue;
                        if (p.AnchorToTerrain) newPos.y = SampleHeight(newPos.x, newPos.z) + p.HeightOffset;
                        SetPropPosition(p, newPos);
                    }
                    else if (kind == ViewportObjectKind.Marker)
                    {
                        var m = Data.ObjMarkers.FirstOrDefault(x => x.Id == id);
                        if (m == null) continue;
                        if (m.AnchorToTerrain) newPos.y = SampleHeight(newPos.x, newPos.z) + m.HeightOffset;
                        SetMarkerPosition(m, newPos);
                    }
                    else if (kind == ViewportObjectKind.Unit)
                    {
                        var u = Data.ObjUnits.FirstOrDefault(x => x.Id == id);
                        if (u == null) continue;
                        if (u.AnchorToTerrain) newPos.y = SampleUnitHeight(newPos.x, newPos.z, u.Type) + u.HeightOffset;
                        SetUnitPosition(u, newPos);
                    }
                }
                RepaintHost?.Invoke();
            }
            e.Use();
            return;
        }

        if (_dragActive && (e.type == EventType.MouseUp || e.type == EventType.Ignore))
        {
            _dragActive = false;
            _dragItems.Clear();
            CommitDragPositions();
            _lastActionSummary = "Moved selection on the map.";
            e.Use();
        }
    }

    /// <summary>Commits a COMPLETED viewport drag as real, undoable
    /// ISC2DocumentCommand(s) — a no-op if no map session is wired (the
    /// legacy PushUndo() at MouseDown already covered the whole gesture in
    /// that case). Diffs EVERY Prop/Marker/Unit's current position against
    /// what Session.Document.Objects last held, rather than only the items
    /// in _dragItems: symmetry-linked objects can move OTHER members too
    /// (SetMarkerPosition/SetPropPosition call PropagateSymmetryMove, which
    /// silently repositions every other member of the group via
    /// SetObjectPositionRaw) — a diff catches those followers as well,
    /// without this method needing to understand the symmetry graph
    /// itself. Multiple changed items become ONE SC2CompositeCommand, so
    /// the whole drag (directly-dragged items AND any symmetry followers)
    /// undoes as a single step, matching the legacy PushUndo()'s own
    /// "one drag = one undo step" behavior.</summary>
    private void CommitDragPositions()
    {
        if (Session?.Document == null) return;

        var doc = Session.Document.Objects;
        var commands = new List<ISC2DocumentCommand>();

        foreach (var m in Data.ObjMarkers)
        {
            var docM = doc.Markers.FirstOrDefault(x => x.Id == m.Id);
            if (docM != null && docM.Position != m.Position)
                commands.Add(new SC2SetMarkerPositionCommand(m.Id, m.Position));
        }
        foreach (var p in Data.ObjProps)
        {
            var docP = doc.Props.FirstOrDefault(x => x.Id == p.Id);
            if (docP != null && docP.Position != p.Position)
                commands.Add(new SC2SetPropPositionCommand(p.Id, p.Position));
        }
        foreach (var u in Data.ObjUnits)
        {
            var docU = doc.Units.FirstOrDefault(x => x.Id == u.Id);
            if (docU != null && docU.Position != u.Position)
                commands.Add(new SC2SetUnitPositionCommand(u.Id, u.Position));
        }

        if (commands.Count == 0) return;

        var toExecute = commands.Count == 1 ? commands[0] : new SC2CompositeCommand("Move object(s)", commands);
        Session.History.Execute(Session.Document, toExecute);
        _undo.Record(new SC2DelegateUndoChannel(TryCommandUndo, TryCommandRedo));
        // Without this, the side panel's Undo/Redo row (drawn from
        // DrawEmbeddedGUI, a separate call from the game-view drag handling
        // above) doesn't reflect the new count until something ELSE
        // happens to trigger a repaint — MouseDrag already repaints for the
        // live-move visual feedback, but nothing did for this MouseUp commit.
        RepaintHost?.Invoke();
    }

    /// <summary>Pulls Prop/Marker/Unit positions back from the document
    /// after a Command-sourced Undo/Redo, which mutated Session.Document.
    /// Objects directly rather than through the viewport drag — same
    /// reasoning as SC2EnvironmentShell.SyncFromDocument/SC2Collision2Shell.
    /// SyncOffsetFromDocument/SC2CostsShell.PullTerrainTypesFromDocument.</summary>
    private void PullObjectsPositionsFromDocument()
    {
        if (Session?.Document == null) return;
        var doc = Session.Document.Objects;

        foreach (var m in Data.ObjMarkers)
        {
            var docM = doc.Markers.FirstOrDefault(x => x.Id == m.Id);
            if (docM != null) m.Position = docM.Position;
        }
        foreach (var p in Data.ObjProps)
        {
            var docP = doc.Props.FirstOrDefault(x => x.Id == p.Id);
            if (docP != null) p.Position = docP.Position;
        }
        foreach (var u in Data.ObjUnits)
        {
            var docU = doc.Units.FirstOrDefault(x => x.Id == u.Id);
            if (docU != null) u.Position = docU.Position;
        }
    }

    /// <summary>Terrain raycast first (gives a proper surface height for
    /// anchored objects); if that fails for any reason — camera looking past
    /// the grid edge, Runtime not built yet, mid-drag over a steep cliff,
    /// etc. — falls back to intersecting a horizontal plane at the drag's
    /// starting height, so a grab that already picked a valid object never
    /// silently does nothing just because the terrain-specific raycast had
    /// an edge case. XZ is what actually matters for the drag delta; a
    /// slightly-off Y from the plane fallback gets corrected immediately
    /// anyway for anchored objects (SampleHeight + offset).</summary>
    private bool TryGetDragPoint(Camera camera, Rect viewportRect, Vector2 mousePosition, out Vector3 hit)
    {
        var ray = SC2TerrainShell.GameGuiPointToRay(camera, viewportRect, mousePosition);
        // Imported terrain first (see SampleHeight's doc comment) - only
        // takes over where an imported mesh actually exists under the
        // cursor, otherwise falls through to the live procedural terrain
        // exactly as before.
        if (TryRaycastImportedTerrain(ray.origin, ray.direction, out hit))
            return true;
        if (SC2TerrainShell.RaycastTerrainHeight(_terrainShell.Runtime, ray, out hit))
            return true;

        var planeY = _dragActive || _dragItems.Count > 0 ? _dragStartWorld.y : 0f;
        var plane = new Plane(Vector3.up, new Vector3(0f, planeY, 0f));
        if (plane.Raycast(ray, out var enter))
        {
            hit = ray.GetPoint(enter);
            return true;
        }
        hit = default;
        return false;
    }

    /// <summary>Validation pin (practical-recommendations #3 from the FAF
    /// analysis pass) — a world-space red ring around anything Validate()
    /// would flag, drawn UNDER the object's own glyph, so problems are
    /// visible spatially in the viewport instead of only as Overview text.</summary>
    private static void DrawInvalidRing(Camera camera, Rect viewportRect, Vector3 world) =>
        SC2TerrainShell.DrawGameViewBrush(camera, viewportRect, world, 2.2f, new Color(1f, .15f, .1f, .85f), Color.clear);

    private void DrawViewportDot(Camera camera, Rect viewportRect, Vector2 mousePosition, bool canPick, Vector3 world, int id,
        ViewportObjectKind kind, Color color, ref ViewportPick pick)
    {
        if (!TryWorldToGui(camera, viewportRect, world, out var gui)) return;
        // Object markers are screen-space editor glyphs anchored through a
        // world-to-viewport projection. Do not reuse the terrain brush: its
        // world-radius ring changes shape and apparent position as the camera
        // rotates, which makes stationary objects look camera-relative.
        //
        // IMGUI drawing only happens in _legacyImguiMarkers debug mode now —
        // by default (GPU mode) every dot is drawn by SC2ObjectMarkerRenderer
        // instead (its own Sync call, gated in OnGameViewGUI/DrawEmbeddedGUI,
        // builds the exact same Normal/Selected position buckets this method
        // used to draw one GUI.DrawTexture pair per object for — 2871
        // simultaneously-visible dots was the real bottleneck this
        // whole migration fixes). The pick-distance math below still always
        // runs regardless of which mode is active — cheap (one matrix
        // transform + rect containment, already reasoned about in
        // VisibleForViewport's own doc comment) and needed so hovering/
        // clicking still finds the right object; OnGameViewGUI feeds
        // whatever `pick` resolves to into the GPU renderer's hover slot (or
        // draws one extra IMGUI glyph in legacy mode) after every kind's
        // loop finishes.
        if (_legacyImguiMarkers)
            DrawViewportGlyph(gui, color, kind);
        if (!canPick) return;
        var distance = Vector2.Distance(gui, mousePosition);
        if (distance < pick.Distance) pick = new ViewportPick { Kind = kind, Id = id, Distance = distance };
    }

    private static void DrawViewportGlyph(Vector2 center, Color color, ViewportObjectKind kind)
    {
        if (Event.current == null || Event.current.type != EventType.Repaint)
            return;

        var oldColor = GUI.color;
        var oldMatrix = GUI.matrix;
        var size = kind == ViewportObjectKind.Marker ? 11f : 9f;

        GUI.color = color;
        GUIUtility.RotateAroundPivot(45f, center);
        GUI.DrawTexture(new Rect(center.x - size * .5f, center.y - size * .5f, size, size), Texture2D.whiteTexture);
        GUI.matrix = oldMatrix;

        GUI.color = new Color(.04f, .05f, .07f, Mathf.Clamp01(color.a));
        var innerSize = Mathf.Max(2f, size - 5f);
        GUI.DrawTexture(new Rect(center.x - innerSize * .5f, center.y - innerSize * .5f, innerSize, innerSize), Texture2D.whiteTexture);
        GUI.color = oldColor;
        GUI.matrix = oldMatrix;
    }

    /// <summary>World-space average position of the current Prop/Marker/Unit
    /// selection — only meaningful for an actual GROUP (2+ objects); a
    /// single-object selection already has its own glyph to grab, so this
    /// returns false below that count rather than drawing a redundant
    /// second handle sitting right on top of it.</summary>
    private bool TryGetSelectionCentroid(out Vector3 centroid)
    {
        var sum = Vector3.zero;
        var count = 0;
        foreach (var id in _selProps) { var p = Data.ObjProps.FirstOrDefault(x => x.Id == id); if (p != null) { sum += p.Position; count++; } }
        foreach (var id in _selMarkers) { var m = Data.ObjMarkers.FirstOrDefault(x => x.Id == id); if (m != null) { sum += m.Position; count++; } }
        foreach (var id in _selUnits) { var u = Data.ObjUnits.FirstOrDefault(x => x.Id == id); if (u != null) { sum += u.Position; count++; } }
        if (count < 2) { centroid = default; return false; }
        centroid = sum / count;
        return true;
    }

    /// <summary>Pivot for the Props rotate/scale gesture — the single
    /// selected prop's own position, or the selection's average position
    /// for 2+ (unlike TryGetSelectionCentroid above, 1 is a valid, common
    /// case here — most rotate/scale gestures act on exactly one prop).</summary>
    private bool TryComputeGesturePivot(out Vector3 pivot)
    {
        var sum = Vector3.zero;
        var count = 0;
        foreach (var id in _selProps) { var p = Data.ObjProps.FirstOrDefault(x => x.Id == id); if (p != null) { sum += p.Position; count++; } }
        if (count == 0) { pivot = default; return false; }
        pivot = sum / count;
        return true;
    }

    /// <summary>Per-frame update for an active R/S gesture (see the field
    /// group's own doc comment) — reads the live mouse position and applies
    /// a rotation/scale delta to every snapshotted prop's OWN Rotation/
    /// Scale, relative to its value when the gesture was armed. Confirm
    /// (left-click or Enter) simply stops updating and keeps the current
    /// live values (already real Data mutations, matching how drag-to-move
    /// already commits by just... not reverting); cancel (Esc) restores
    /// every prop's original value.</summary>
    private void TickPropsGesture(Vector2 mousePosition)
    {
        var e = Event.current;
        if (e != null && ((e.type == EventType.MouseDown && e.button == 0) ||
                           (e.type == EventType.KeyDown && (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter))))
        {
            _lastActionSummary = _propsGestureKind == PropsGestureKind.Rotate
                ? $"Rotated {_gestureItems.Count} prop(s)."
                : $"Scaled {_gestureItems.Count} prop(s).";
            _propsGestureKind = PropsGestureKind.None;
            _gestureItems.Clear();
            e.Use();
            RepaintHost?.Invoke();
            return;
        }
        if (e != null && e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
        {
            foreach (var (prop, originalRotation, originalScale) in _gestureItems)
            {
                prop.Rotation = originalRotation;
                prop.Scale = originalScale;
            }
            _propsGestureKind = PropsGestureKind.None;
            _gestureItems.Clear();
            _lastActionSummary = "Cancelled.";
            e.Use();
            RepaintHost?.Invoke();
            return;
        }

        if (_propsGestureKind == PropsGestureKind.Rotate)
        {
            var startVec = _gestureStartMouse - _gestureAnchorScreen;
            var currentVec = mousePosition - _gestureAnchorScreen;
            if (startVec.sqrMagnitude > 4f && currentVec.sqrMagnitude > 4f)
            {
                var startAngle = Mathf.Atan2(startVec.y, startVec.x);
                var currentAngle = Mathf.Atan2(currentVec.y, currentVec.x);
                // Negated: GUI-space Y increases downward, the opposite of
                // the math convention atan2 assumes, so a raw angle delta
                // here would spin the object the "wrong" way relative to
                // how the mouse visibly circled the pivot on screen.
                var deltaDegrees = -(currentAngle - startAngle) * Mathf.Rad2Deg;
                foreach (var (prop, originalRotation, _) in _gestureItems)
                    prop.Rotation = originalRotation * Quaternion.Euler(0f, deltaDegrees, 0f);
                _lastActionSummary = $"Rotate: {deltaDegrees:0.#}° — click/Enter to confirm, Esc to cancel.";
            }
        }
        else if (_propsGestureKind == PropsGestureKind.Scale)
        {
            var currentDistance = Mathf.Max(1f, Vector2.Distance(_gestureAnchorScreen, mousePosition));
            var factor = currentDistance / _gestureStartDistance;
            foreach (var (prop, _, originalScale) in _gestureItems)
                prop.Scale = Mathf.Max(0.01f, originalScale * factor);
            _lastActionSummary = $"Scale: {factor:0.00}x — click/Enter to confirm, Esc to cancel.";
        }
        RepaintHost?.Invoke();
    }

    /// <summary>Visibly distinct from DrawViewportGlyph's per-object diamonds
    /// (larger dark diamond backdrop + a 4-way move cross on top, in the
    /// same yellow already used for "selected") — there was previously no
    /// visible element to grab to drag a whole selected group. Picking this
    /// glyph is wired in OnGameViewGUI/HandleViewportDrag via
    /// ViewportObjectKind.GroupHandle, which reuses the exact same "already
    /// selected, arm a group drag" path a click on any already-selected
    /// object already takes.</summary>
    private static void DrawGroupHandleGlyph(Vector2 center)
    {
        if (Event.current == null || Event.current.type != EventType.Repaint)
            return;

        var oldColor = GUI.color;
        var oldMatrix = GUI.matrix;

        const float backdropSize = 22f;
        GUI.color = new Color(.04f, .05f, .07f, .85f);
        GUIUtility.RotateAroundPivot(45f, center);
        GUI.DrawTexture(new Rect(center.x - backdropSize * .5f, center.y - backdropSize * .5f, backdropSize, backdropSize), Texture2D.whiteTexture);
        GUI.matrix = oldMatrix;

        const float armLength = 8f;
        const float thickness = 2.5f;
        GUI.color = new Color(1f, .9f, .12f, .95f);
        GUI.DrawTexture(new Rect(center.x - armLength, center.y - thickness * .5f, armLength * 2f, thickness), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(center.x - thickness * .5f, center.y - armLength, thickness, armLength * 2f), Texture2D.whiteTexture);

        GUI.color = oldColor;
    }

    private static bool TryWorldToGui(Camera camera, Rect viewportRect, Vector3 world, out Vector2 gui)
    {
        // viewportRect clips objects behind the Unified UI; it is not the
        // camera's render rect. Using it to rescale viewport coordinates makes
        // every marker drift relative to the terrain as the camera zooms.
        var pixel = SC2TopViewMirror.WorldToGui(camera, world);
        gui = new Vector2(pixel.x, pixel.y);
        return pixel.z > 0f && viewportRect.Contains(gui);
    }

    /// <summary>Visibility menu now has one Path toggle per real graph kind
    /// (2 or 4, depending on Data.PlayableWater — see RequiredGraphKinds)
    /// instead of one blanket "Graphs" checkbox, so each graph's own
    /// GraphKind decides which of these 4 flags gates it.</summary>
    private void DrawGraphOverlays(Camera camera, Rect viewportRect, bool showLand, bool showAir, bool showNaval, bool showAmphibious)
    {
        foreach (var graph in Data.ObjGraphs)
        {
            var visible = graph.GraphKind switch
            {
                "Air" => showAir,
                "Naval" => showNaval,
                "Amphibious" => showAmphibious,
                _ => showLand
            };
            if (!visible) continue;
            var color = graph.GraphKind switch
            {
                "Air" => new Color(.3f, .65f, 1f, .8f),
                "Naval" => new Color(.2f, .4f, 1f, .8f),
                "Amphibious" => new Color(.2f, .95f, .85f, .8f),
                _ => new Color(.5f, .95f, .25f, .8f)
            };
            foreach (var (a, b) in SC2ObjectGraphRefs.Enumerate(graph))
                if (SC2ObjectGraphRefs.TryGetMarker(Data, a, out var ma) && SC2ObjectGraphRefs.TryGetMarker(Data, b, out var mb))
                    DrawOverlayLine(camera, viewportRect, ma.Position, mb.Position, color, 2f);
        }
    }

    private void DrawChainOverlays(Camera camera, Rect viewportRect)
    {
        // Built via an indexer loop (last-name-wins), not .ToDictionary —
        // this runs every viewport repaint, so a duplicate SC2ObjMarker.Name
        // (MakeUniqueMarkerName prevents new ones, but old save data or an
        // imported map could still carry one) must never throw here; a
        // best-effort overlay line is fine picking either marker.
        var markers = new Dictionary<string, SC2ObjMarker>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in Data.ObjMarkers)
            if (!string.IsNullOrEmpty(m.Name))
                markers[m.Name] = m;
        foreach (var chain in Data.ObjChains)
        {
            SC2ObjMarker previous = null;
            foreach (var markerName in chain.MarkerNames)
            {
                if (!markers.TryGetValue(markerName, out var current)) continue;
                if (previous != null) DrawOverlayLine(camera, viewportRect, previous.Position, current.Position, new Color(1f, .6f, .15f, .78f), 2f);
                previous = current;
            }
        }
    }

    private void DrawZoneOverlays(Camera camera, Rect viewportRect, Vector2 mousePosition, bool canSelect, ref ViewportPick pick)
    {
        foreach (var zone in Data.ObjZones)
        {
            // Each corner samples its OWN height instead of sharing one
            // center-sampled Y across all four - on sloped terrain a single
            // shared height made the wireframe float above or clip below the
            // real surface at the corners instead of following it.
            var p0 = new Vector3(zone.X1, SampleHeight(zone.X1, zone.Z1) + .3f, zone.Z1);
            var p1 = new Vector3(zone.X2, SampleHeight(zone.X2, zone.Z1) + .3f, zone.Z1);
            var p2 = new Vector3(zone.X2, SampleHeight(zone.X2, zone.Z2) + .3f, zone.Z2);
            var p3 = new Vector3(zone.X1, SampleHeight(zone.X1, zone.Z2) + .3f, zone.Z2);
            var color = _selectedZoneId == zone.Id ? new Color(1f, .9f, .12f, .95f) : new Color(.25f, 1f, .55f, .7f);
            DrawOverlayLine(camera, viewportRect, p0, p1, color, 2f); DrawOverlayLine(camera, viewportRect, p1, p2, color, 2f);
            DrawOverlayLine(camera, viewportRect, p2, p3, color, 2f); DrawOverlayLine(camera, viewportRect, p3, p0, color, 2f);
            var center = (p0 + p2) * .5f;
            if (canSelect && TryWorldToGui(camera, viewportRect, center, out var gui))
            {
                var distance = Vector2.Distance(gui, mousePosition);
                if (distance < pick.Distance) pick = new ViewportPick { Kind = ViewportObjectKind.Zone, Id = zone.Id, Distance = distance };
            }

            // Drag-to-resize handles at each EDGE midpoint (not each corner)
            // — only drawn for the SELECTED area (avoids visual clutter with
            // many areas on screen at once). A corner handle let a drag
            // perturb both the X and Z edge meeting at that point together;
            // real per-edge dragging replaces that, matching the numeric
            // Left/Right/Bottom/Top fields above. Each handle only ever
            // moves along its own edge's axis; Mirror X/Z (the same toggles
            // driving the numeric fields) make the opposite edge follow.
            if (canSelect && _selectedZoneId == zone.Id)
                DrawZoneEdgeHandles(camera, viewportRect, mousePosition, zone, p0, p1, p2, p3);
        }
    }

    private enum ZoneEdge { Left, Right, Bottom, Top }
    private bool _zoneEdgeDragActive;
    private int _zoneEdgeDragId;
    private ZoneEdge _zoneEdgeDragEdge;
    // Which raw field this drag gesture writes to, captured once at
    // MouseDown and held fixed for the whole gesture — Left/Right map to
    // X1/X2 and Bottom/Top map to Z1/Z2, but WHICH of X1/X2 is currently the
    // "left" one depends on which is smaller, and re-deriving that every
    // drag frame would let the drag flip which field it's writing to the
    // instant the user drags one edge past the other.
    private bool _zoneEdgeDragIsFirstField;

    private void DrawZoneEdgeHandles(Camera camera, Rect viewportRect, Vector2 mousePosition, SC2ObjZone zone, Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3)
    {
        var e = Event.current;
        var handles = new[]
        {
            (ZoneEdge.Left, (p0 + p3) * .5f),
            (ZoneEdge.Right, (p1 + p2) * .5f),
            (ZoneEdge.Bottom, (p0 + p1) * .5f),
            (ZoneEdge.Top, (p3 + p2) * .5f)
        };
        foreach (var (edge, worldPos) in handles)
        {
            if (!TryWorldToGui(camera, viewportRect, worldPos, out var gui)) continue;

            if (e != null && e.type == EventType.Repaint)
            {
                var isX = edge is ZoneEdge.Left or ZoneEdge.Right;
                var mirrorActive = isX ? _areaMirrorX : _areaMirrorZ;
                var oldColor = GUI.color;
                GUI.color = mirrorActive ? new Color(.3f, .85f, 1f, .95f) : new Color(1f, .95f, .2f, .95f);
                // A bar oriented ALONG the edge (perpendicular to its own
                // drag axis) reads as "grab this face," not "grab this
                // point" — the corner handles it replaces were plain
                // squares, indistinguishable from a point-drag handle.
                var rect = isX
                    ? new Rect(gui.x - 3f, gui.y - 9f, 6f, 18f)
                    : new Rect(gui.x - 9f, gui.y - 3f, 18f, 6f);
                GUI.DrawTexture(rect, Texture2D.whiteTexture);
                GUI.color = oldColor;
            }

            if (!_zoneEdgeDragActive && e != null && e.type == EventType.MouseDown && e.button == 0 &&
                Vector2.Distance(gui, mousePosition) < 10f)
            {
                _zoneEdgeDragActive = true;
                _zoneEdgeDragId = zone.Id;
                _zoneEdgeDragEdge = edge;
                _zoneEdgeDragIsFirstField = edge switch
                {
                    ZoneEdge.Left => zone.X1 <= zone.X2,
                    ZoneEdge.Right => zone.X1 > zone.X2,
                    ZoneEdge.Bottom => zone.Z1 <= zone.Z2,
                    _ => zone.Z1 > zone.Z2
                };
                if (Session?.Document == null) PushUndo();
                e.Use();
                return;
            }
        }

        if (!_zoneEdgeDragActive || _zoneEdgeDragId != zone.Id) return;

        if (e != null && e.type == EventType.MouseDrag && e.button == 0)
        {
            if (TryGetDragPoint(camera, viewportRect, mousePosition, out var world))
            {
                var isX = _zoneEdgeDragEdge is ZoneEdge.Left or ZoneEdge.Right;
                var mirrorActive = isX ? _areaMirrorX : _areaMirrorZ;
                if (isX)
                {
                    var oldValue = _zoneEdgeDragIsFirstField ? zone.X1 : zone.X2;
                    var delta = world.x - oldValue;
                    if (_zoneEdgeDragIsFirstField) zone.X1 = world.x; else zone.X2 = world.x;
                    if (mirrorActive) { if (_zoneEdgeDragIsFirstField) zone.X2 -= delta; else zone.X1 -= delta; }
                }
                else
                {
                    var oldValue = _zoneEdgeDragIsFirstField ? zone.Z1 : zone.Z2;
                    var delta = world.z - oldValue;
                    if (_zoneEdgeDragIsFirstField) zone.Z1 = world.z; else zone.Z2 = world.z;
                    if (mirrorActive) { if (_zoneEdgeDragIsFirstField) zone.Z2 -= delta; else zone.Z1 -= delta; }
                }
                RepaintHost?.Invoke();
            }
            e.Use();
        }
        else if (e != null && (e.type == EventType.MouseUp || e.type == EventType.Ignore))
        {
            _zoneEdgeDragActive = false;
            CommitZoneRectChange(zone);
            e.Use();
        }
    }

    /// <summary>Commits a completed corner-drag as a real, undoable
    /// ISC2DocumentCommand (mirrors the existing numeric Corner 1/Corner 2
    /// field's own Session.History.Execute call) — only fires once, at
    /// drag-end, not per drag frame, same "coalesce the whole gesture into
    /// one undo step" reasoning as Collision2's slider drag.</summary>
    private void CommitZoneRectChange(SC2ObjZone zone)
    {
        if (Session?.Document == null) return;
        Session.History.Execute(Session.Document, new SC2SetZoneRectCommand(zone.Id, zone.X1, zone.Z1, zone.X2, zone.Z2));
        _undo.Record(new SC2DelegateUndoChannel(TryCommandUndo, TryCommandRedo));
    }

    /// <summary>Bailing out (drawing nothing) whenever EITHER
    /// endpoint's projected pixel falls outside viewportRect or behind the
    /// camera (TryWorldToGui's all-or-nothing check) would make path
    /// marker edges disappear when the camera rotates. Graph/chain/zone
    /// edges often span much of the map, so rotating or panning constantly
    /// puts one endpoint off-screen while the edge itself is still crossing
    /// visible space; the old code made the whole edge flicker away instead
    /// of just getting shorter. Fixed with real segment clipping: first in
    /// 3D against the camera's near plane (a point behind the camera
    /// projects to a numerically meaningless pixel, so it must be clipped
    /// BEFORE projecting, not after), then in 2D against viewportRect
    /// (Liang-Barsky) - only the sub-segment that's actually visible gets
    /// drawn.</summary>
    private static void DrawOverlayLine(Camera camera, Rect viewportRect, Vector3 from, Vector3 to, Color color, float width)
    {
        var camPos = camera.transform.position;
        var camFwd = camera.transform.forward;
        const float nearEps = 0.05f;
        var zFrom = Vector3.Dot(from - camPos, camFwd);
        var zTo = Vector3.Dot(to - camPos, camFwd);
        if (zFrom <= nearEps && zTo <= nearEps) return; // whole segment behind (or at) the camera

        var clippedFrom = from;
        var clippedTo = to;
        if (zFrom <= nearEps)
            clippedFrom = Vector3.Lerp(from, to, (nearEps - zFrom) / (zTo - zFrom));
        else if (zTo <= nearEps)
            clippedTo = Vector3.Lerp(to, from, (nearEps - zTo) / (zFrom - zTo));

        var pixelA = SC2TopViewMirror.WorldToGui(camera, clippedFrom);
        var pixelB = SC2TopViewMirror.WorldToGui(camera, clippedTo);
        var a = new Vector2(pixelA.x, pixelA.y);
        var b = new Vector2(pixelB.x, pixelB.y);
        if (!ClipLineToRect(viewportRect, ref a, ref b)) return;

        var delta = b - a; var length = delta.magnitude;
        if (length < .5f) return;
        var oldMatrix = GUI.matrix; var oldColor = GUI.color;
        GUI.color = color;
        GUIUtility.RotateAroundPivot(Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg, a);
        GUI.DrawTexture(new Rect(a.x, a.y - width * .5f, length, width), Texture2D.whiteTexture);
        GUI.matrix = oldMatrix;
        GUI.color = oldColor;
    }

    /// <summary>Liang-Barsky line-vs-axis-aligned-rect clipping - returns
    /// false if the segment lies entirely outside rect, otherwise clamps
    /// a/b in place to the visible sub-segment.</summary>
    private static bool ClipLineToRect(Rect rect, ref Vector2 a, ref Vector2 b)
    {
        var dx = b.x - a.x; var dy = b.y - a.y;
        var tMin = 0f; var tMax = 1f;

        if (!ClipTest(-dx, a.x - rect.xMin, ref tMin, ref tMax)) return false;
        if (!ClipTest(dx, rect.xMax - a.x, ref tMin, ref tMax)) return false;
        if (!ClipTest(-dy, a.y - rect.yMin, ref tMin, ref tMax)) return false;
        if (!ClipTest(dy, rect.yMax - a.y, ref tMin, ref tMax)) return false;

        var newB = new Vector2(a.x + tMax * dx, a.y + tMax * dy);
        var newA = new Vector2(a.x + tMin * dx, a.y + tMin * dy);
        a = newA; b = newB;
        return true;
    }

    private static bool ClipTest(float p, float q, ref float tMin, ref float tMax)
    {
        if (Mathf.Approximately(p, 0f)) return q >= 0f; // parallel to this edge - reject only if outside
        var r = q / p;
        if (p < 0f)
        {
            if (r > tMax) return false;
            if (r > tMin) tMin = r;
        }
        else
        {
            if (r < tMin) return false;
            if (r < tMax) tMax = r;
        }
        return true;
    }

    private void SelectViewportObject(ViewportPick pick)
    {
        if (_lastSubTab == TabPaths && _pathPickMode != 0)
        {
            if (pick.Kind == ViewportObjectKind.Marker &&
                SC2ObjectGraphRefs.TryGetMarker(Data, pick.Id, out var pathMarker) && !string.IsNullOrEmpty(pathMarker.Type) && pathMarker.Type.Contains("Path Node"))
            {
                if (_pathPickMode == 1) _pathNodeA = pick.Id; else _pathNodeB = pick.Id;
                _pathPickMode = 0;
                _lastActionSummary = $"Selected path node '{pathMarker.Name}' from viewport.";
                return;
            }
            // Clicked something that isn't a valid path-node marker while
            // Node A/B pick mode was armed — cancel the pick instead of
            // leaving it silently stuck (the "Node A/B: ..." button would
            // otherwise stay highlighted "active" and swallow the NEXT
            // path-node click too), then fall through to normal selection
            // below so this click still selects whatever was actually
            // clicked.
            _pathPickMode = 0;
        }

        var additive = SC2Hotkeys.ModifierHeld("Objects.SingleSelectAdditiveModifier", Event.current);
        if (!additive) { _selProps.Clear(); _selMarkers.Clear(); _selUnits.Clear(); _selectedZoneId = 0; }
        switch (pick.Kind)
        {
            case ViewportObjectKind.Prop: HandleViewportClick(_selProps, pick.Id); break;
            case ViewportObjectKind.Marker: HandleViewportClick(_selMarkers, pick.Id); break;
            case ViewportObjectKind.Unit: HandleViewportClick(_selUnits, pick.Id); break;
            case ViewportObjectKind.Zone: _selectedZoneId = _selectedZoneId == pick.Id && additive ? 0 : pick.Id; break;
        }
        _lastActionSummary = $"Selected {pick.Kind.ToString().ToLowerInvariant()} #{pick.Id} in viewport.";
        ReportStatus?.Invoke(_lastActionSummary);
    }
}
