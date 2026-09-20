using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;
using SC2.MapEditor.Document;

/// <summary>
/// Costs + TerrainTypes painter for the PROCEDURAL SC2TerrainData workflow —
/// the Build &amp; Run GameView UI's "Costs" tab (SC2GameViewEditorUI.
/// DrawCostsInspector). This is the sibling of SC2CostsTerrainTypesEditor
/// (Editor/SC2CostsTerrainTypesEditor.cs), which does the same job for the
/// EXISTING-map workflow (SC2MapRawData / costs.bytes+hfield.bytes loaded
/// from an imported .scd) — that class is Editor-only (SceneView/Handles/
/// AssetDatabase) and cannot compile into a standalone Build &amp; Run player at
/// all, which is exactly why the GameView UI's Costs tab was still showing
/// SetStatus(...) stub buttons: there was nothing build-compatible to call.
///
/// This class instead:
///  - lives in Scripts/Unified (not Editor/), so it compiles for both the
///    Editor and a real .exe build, the same way SC2TerrainShell/SC2PaintMode
///    do (via UnityEditorRuntimeShim's non-Editor EditorGUILayout etc.);
///  - hit-tests the terrain via SC2TerrainShell.GameGuiPointToRay +
///    RaycastTerrainHeight — the same analytic CPU heightfield raycast Paint
///    mode already uses for brush painting in a build, NOT SceneView/Handles/
///    MeshCollider+Physics.Raycast (Handles are no-ops outside the Editor per
///    UnityEditorRuntimeShim, and procedural terrain chunks may not carry a
///    MeshCollider at all);
///  - paints directly into SC2TerrainData.CostsBlocks / .TerrainTypes (new
///    fields added alongside Materials/Pairs — see SC2TerrainData.cs), which
///    persist through both AssetDatabase (Editor) and SC2RuntimeTerrainPersistence
///    (Build &amp; Run JSON save/load), and later get folded into the export
///    pipeline the same way terrain.bdf/hfield.bdf already are;
///  - reads the SAME Data.Symmetry/Data.SymmetryCenter every other procedural
///    painting tool (height sculpt, pair paint) already uses, instead of a
///    separate private symmetry setting;
///  - renders its overlay via SC2TerrainRuntime.SetCostsOverlayTexture, a
///    texture parameter on the shared chunk material sampled every frame by
///    SC2TerrainView.shader's normal render pass — not per-frame
///    Graphics.DrawMesh calls issued from OnGUI (an earlier version did that,
///    and it could vanish on mouse-up or not render at all in the Editor's
///    Game tab vs. a real build, since it depended on exactly when OnGUI
///    fires relative to the camera's render pass).
/// </summary>
public sealed class SC2CostsShell
{
    public enum PaintTarget { Costs, TerrainTypes }

    /// <summary>Overlay tint for cells explicitly hand-painted (pinned) on
    /// the shore-hint-capable block (ShoreHintBlockIndex) — see
    /// SC2TerrainData.CostsShoreHintOverrideMask's own doc comment. Not a
    /// separate paint target anymore — the
    /// user paints straight onto that block's own Costs tab like any other
    /// block; this just tints the pinned cells differently in the overlay
    /// so they're visible as "will survive the next auto-resync."</summary>
    private static readonly Color ShoreHintOverrideColor = new Color(1f, 0.55f, 0f, 0.55f);

    /// <summary>Overlay tint of an automatic outline cell the user marked as unneeded
    /// (passable) with the Outline brush.</summary>
    private static readonly Color OutlineOpenedColor = new Color(0.1f, 0.9f, 0.5f, 0.55f);

    /// <summary>Costs paint mode of the Outline brush (blocks 1+): opens automatic outline
    /// cells. 0 = impassable, 1 = passable, 2 = Shore (block 4 only).</summary>
    private const int OutlineMode = 3;

    public sealed class TerrainTypePreset
    {
        public ushort Id;
        public string Label;
        public Color Color;
    }

    private readonly List<TerrainTypePreset> _presets = BuildDefaultPresets();
    private int _activePreset;

    /// <summary>Verified against a real vanilla hfield.bdf's TerrainTypes
    /// grid — the only two values that actually occur are 2 ("Default") and
    /// 3 ("Chasm_Bottom"); everything else is unused. "Default" carries
    /// alpha 0 (Color.a) so the overlay shows the terrain's own natural
    /// appearance with no tint there — only Chasm_Bottom gets a visible red
    /// tint, matching how costs marks impassable cells.</summary>
    private static List<TerrainTypePreset> BuildDefaultPresets() => new()
    {
        new TerrainTypePreset { Id = 2, Label = "Default", Color = new Color(0.5f, 0.5f, 0.5f, 0f) },
        new TerrainTypePreset { Id = 3, Label = "Chasm_Bottom", Color = new Color(1f, 0.1f, 0.1f, 0.55f) },
    };

    private readonly SC2TerrainShell _terrainShell;
    public System.Action RepaintHost;

    /// <summary>Set by SC2GameViewEditorUI.EnsureCostsShell(). Null is a
    /// valid, supported state (SyncDocumentFromLive no-ops without it) —
    /// same reasoning as SC2TerrainShell.Session.</summary>
    public SC2MapSession Session;

    public PaintTarget ActiveTarget = PaintTarget.Costs;
    private bool _painting;
    private float _brushRadius = 24f;
    // F interactive resize, Blender-style —
    // see SC2BrushRadiusGesture's own doc comment.
    private readonly SC2BrushRadiusGesture _brushRadiusGesture = new SC2BrushRadiusGesture();
    private bool _allBlocks;
    private int _activeBlock;
    private int _costsMode; // 0 = impassable, 1 = passable, 2 = shore, OutlineMode = outline
    // Copies one Costs block into another, and adds an outline (safety
    // margin) around the impassable area — see
    // DrawCopyBlockControls/DrawDilateControls.
    private int _copySourceBlock;
    private int _dilateMargin = 2;
    // A toggle for fully manual drawing of any Costs block — an escape hatch back
    // to the pre-auto-derive behavior. See IsAutoManagedCostsBlock (returns
    // false for every block while this is on) and RecomputeDerivedCostsBlocks
    // (no-ops entirely while this is on, so a manual stroke on a normally-
    // derived block isn't immediately overwritten by the next auto-resync).
    private bool _manualCostsMode;
    // Imports a costs mask from a black/white image — see
    // ImportCostsImageFromFile; inverts which end of the black/white scale
    // means passable.
    private bool _importCostsInvertColors;
    private Vector2? _lastPaintPos;
    /// <summary>Tracks the host's top-level sub-tab index (SC2GameViewEditorUI.
    /// _subTab) so a real CHANGE (a tab click) can be told apart from every
    /// other frame's redraw — see GetSubtabLabels/DrawEmbeddedGUI.</summary>
    private int _lastSubTab = -1;

    private Texture2D _overlayTex;
    private Color[] _overlayColors;
    private bool _overlayDirty = true;

    /// <summary>Tunable slope/water-depth thresholds for
    /// "Auto-generate from height", live in this panel next to the button
    /// that consumes them (not a general Costs settings area). Session-only
    /// UI state, not exported/undo-tracked — SC2CostsAutoGen.DefaultThresholds'
    /// own doc comment explains why these aren't asserted vanilla-exact
    /// values.</summary>
    private SC2CostsAutoGen.Thresholds _genThresholds = new();

    private sealed class Snapshot
    {
        public PaintTarget Target;
        public byte[][] CostsNavmesh;
        public ushort[] TerrainTypes;
        public byte[] CostsShoreHintOverrideMask;
        public byte[][] CostsOutlineOpen;
        // Paint tool config below — none of this is exported terrain data,
        // but it's editable state in this panel, so it gets the same
        // "everything editable here is undo-able" treatment SC2GeometryMode's
        // brush tool/radius/strength/hardness got in the Terrain UI cutover
        // pass, folded directly into this shell's own Snapshot since (unlike
        // SC2TerrainShell/SC2TerrainUndoStack) there's no cross-class
        // boundary here to keep opaque — this class owns both the capture
        // and the state being captured.
        public int CostsMode;
        public bool AllBlocks;
        public bool ManualCostsMode;
        public int ActiveBlock;
        public int ActivePreset;
        public float BrushRadius;
        public List<string> PresetLabels;
    }
    private readonly Stack<Snapshot> _undoStack = new();
    private readonly Stack<Snapshot> _redoStack = new();
    private const int MaxUndoSteps = 30;

    /// <summary>TerrainTypes strokes now record
    /// a REAL ISC2DocumentCommand (SC2SetHfieldTerrainTypesCommand) through
    /// Session.History instead of a legacy Snapshot — but Costs-target
    /// strokes and all tool-config edits (target/mode/block/preset/radius)
    /// stay on the legacy Stack&lt;Snapshot&gt; above exactly as before. The
    /// combined marker stack that lets this shell's
    /// own Undo/Redo buttons dispatch each step to whichever system actually
    /// owns it (in true chronological order) is now the shared
    /// SC2ShellUndoCoordinator instead of a locally hand-copied enum/stack —
    /// same class SC2TerrainCombinedUndo/SC2ObjectsShell now use too.</summary>
    private readonly SC2ShellUndoCoordinator _undo = new();

    public int UndoCount => _undo.UndoCount;
    public int RedoCount => _undo.RedoCount;

    /// <summary>See SC2ObjectsShell's identical property for why.</summary>
    public bool HasUndo => _undo.CanUndo;
    public bool HasRedo => _undo.CanRedo;
    public long LastActionTimestamp => _undo.LastActionTimestamp;
    /// <summary>See SC2ShellUndoCoordinator.LastRedoTimestamp — the global
    /// Redo dispatcher must compare this, not LastActionTimestamp, when
    /// deciding which shell's Redo() to call.</summary>
    public long LastRedoTimestamp => _undo.LastRedoTimestamp;

    public SC2CostsShell(SC2TerrainShell terrainShell)
    {
        _terrainShell = terrainShell;
    }

    private SC2TerrainData Data => _terrainShell?.Data;
    private SC2TerrainRuntime Runtime => _terrainShell?.Runtime;
    public bool IsReady => Data != null && Data.IsInitialized;

    /// <summary>Real vanilla index of "Land_Medium_Water_Surface" in the
    /// 5-block playable-water set — the one block besides block 0 the user
    /// hand-paints directly (a "passable shore" hint), everything else is
    /// pure auto (see IsAutoManagedCostsBlock). Swapped from index 3
    /// ("Land_Large_water_Seafloor") to index 4 — index 3 is now the one
    /// dilated FROM this block instead of the other way around, see
    /// RecomputeDerivedCostsBlocks. -1 (no such block) for the 3-block
    /// land-only set or any other block count.</summary>
    private int ShoreHintBlockIndex => Data != null && Data.CostsBlocks.Count == 5 ? 4 : -1;

    /// <summary>True for a Costs block that is now purely computed from
    /// block 0 (see RecomputeDerivedCostsBlocks) and should never be
    /// directly hand-painted — every block besides the first is in
    /// auto-mode. Block 0 itself
    /// and the shore-hint-capable block are excluded (both stay paintable).
    /// Always false while _manualCostsMode is on — the whole point of that
    /// toggle is that no block is treated as auto-managed anymore.</summary>
    private bool IsAutoManagedCostsBlock(int blockIndex) =>
        !_manualCostsMode && blockIndex >= 0 && blockIndex < Data.CostsBlocks.Count && blockIndex != 0 && blockIndex != ShoreHintBlockIndex;

    // ==================================================================
    // Embedded GUI (side panel) — mirrors SC2PaintMode.DrawPanel's layout
    // conventions (GetPanelContentWidth, DrawUnifiedSegmentGrid, etc.) so it
    // reads as part of the same shell, not a bolted-on extra panel.
    // ==================================================================

    /// <summary>Sub-tab labels for the host's top-level tab row
    /// (SC2GameViewEditorUI.DrawSubtabs) — one entry per real Costs block (3
    /// land-only or 5 playable-water, matching Data.PlayableWater/
    /// Data.CostsBlocks) plus a trailing "Terrain Types" entry, so the tab
    /// row itself IS the block/TerrainTypes selector instead of a
    /// dropdown/segmented-control living inside a single panel. Falls back to
    /// a plain 2-entry placeholder before a terrain exists, so the tab row
    /// still renders sensibly pre-load.</summary>
    public string[] GetSubtabLabels()
    {
        if (!IsReady) return new[] { "Costs", "Terrain Types" };
        Data.EnsureCostsAndTerrainTypes();
        var blockNames = Data.CostsBlocks.Select(b => b.Name).ToArray();
        var labels = new string[blockNames.Length + 1];
        System.Array.Copy(blockNames, labels, blockNames.Length);
        labels[blockNames.Length] = "Terrain Types";
        return labels;
    }

    public void DrawEmbeddedGUI(float panelWidth, int subTab)
    {
        // See SC2TerrainShell.SyncEffectiveViewWidth's own doc comment — without
        // this, DrawUnifiedSegmentGrid/GetPanelContentWidth (used below by
        // DrawBlockSetSwitcher etc.) size themselves against whatever width was
        // last cached by a DIFFERENT tab (Terrain, or Objects), not this panel's
        // own actual width — the "squished until you switch tabs and back" bug.
        SC2TerrainShell.SyncEffectiveViewWidth(panelWidth);
        var w = Mathf.Max(1f, panelWidth);
        EditorGUILayout.BeginVertical(GUILayout.Width(w), GUILayout.MaxWidth(w));

        if (!IsReady)
        {
            SC2TerrainShell.DrawUnifiedNote("Create/load a terrain first (Terrain tab) — costs and terrain types paint over the actual sculpted surface.");
            EditorGUILayout.EndVertical();
            return;
        }

        Data.EnsureCostsAndTerrainTypes();

        // The host's sub-tab row (labels from GetSubtabLabels above) now
        // drives ActiveTarget/_activeBlock directly — a real tab CHANGE
        // (subTab != _lastSubTab) just re-points which block/target is
        // active, same as switching Terrain's Water/Geometry tabs isn't
        // itself an undoable action (pure navigation, not painted data).
        var blockCount = Data.CostsBlocks.Count;
        if (subTab != _lastSubTab)
        {
            if (subTab < blockCount)
            {
                ActiveTarget = PaintTarget.Costs;
                _activeBlock = subTab;
                // "Shore" (costsMode 2) only exists on the shore-hint block's
                // own 3-way segmented control — clamp back to Impassable/
                // Passable when landing on any other block's tab, otherwise
                // this stale value would silently re-trigger RecordUndo() the
                // next time DrawCostsControls reconciles its 2-way control
                // against it.
                EnforceCostsModeForActiveBlock();
            }
            else
            {
                ActiveTarget = PaintTarget.TerrainTypes;
            }
            _overlayDirty = true;
            _lastSubTab = subTab;
        }

        DrawBlockSetSwitcher(w);
        EditorGUILayout.Space(4);

        DrawGridSizeControl(w);
        EditorGUILayout.Space(4);

        if (ActiveTarget == PaintTarget.Costs && _activeBlock >= 0 && _activeBlock < Data.CostsBlocks.Count)
            EditorGUILayout.LabelField($"Editing block: {Data.CostsBlocks[_activeBlock].Name}", EditorStyles.boldLabel, GUILayout.Width(w), GUILayout.MaxWidth(w));

        EditorGUILayout.Space(4);

        if (ActiveTarget == PaintTarget.Costs)
        {
            var newManual = EditorGUILayout.ToggleLeft("Manual Mode (paint every block directly, no auto-resync)", _manualCostsMode, GUILayout.Width(w), GUILayout.MaxWidth(w));
            if (newManual != _manualCostsMode)
            {
                RecordUndo();
                _manualCostsMode = newManual;
                EnforceCostsModeForActiveBlock();
            }
            EditorGUILayout.Space(4);
        }

        // Every block besides block 0 (and
        // the shore-hint-capable block) is now purely computed from block 0
        // — see RecomputeDerivedCostsBlocks — so it isn't paintable at all;
        // _painting itself stays a global (not per-block) toggle, but this
        // tab simply never shows the controls that would arm it.
        EnforceCostsModeForActiveBlock();
        var autoManaged = ActiveTarget == PaintTarget.Costs && IsAutoManagedCostsBlock(_activeBlock);
        if (autoManaged)
        {
            SC2TerrainShell.DrawUnifiedNote(
                "Automatically derived from block 0 — its walls are not directly paintable. It resyncs whenever block 0 " +
                "(or the shore-hint block) changes: a paint stroke, Auto-generate, Dilate, or Copy. Use the " +
                "button below to resync it by hand (e.g. right after loading an older save).");
            if (GUILayout.Button("Resync derived blocks now", GUILayout.Height(SC2UiScale.Pxf(22f)), GUILayout.Width(w), GUILayout.MaxWidth(w)))
            {
                RecordUndo();
                RecomputeDerivedCostsBlocks();
                SyncDocumentFromLive();
            }
            EditorGUILayout.Space(4);
            DrawPaintToggleAndRadius(w);
            EditorGUILayout.Space(4);
            DrawOutlineControls(w);
        }
        else
        {
            DrawPaintToggleAndRadius(w);

            EditorGUILayout.Space(4);
            if (ActiveTarget == PaintTarget.Costs)
                DrawCostsControls(w);
            else
                DrawTerrainTypesControls(w);
        }

        EditorGUILayout.Space(6);
        DrawUndoRedoRow(w);

        EditorGUILayout.Space(6);
        if (GUILayout.Button("Save Terrain (incl. Costs + Terrain Types)", GUILayout.Height(SC2UiScale.Pxf(26f)), GUILayout.Width(w), GUILayout.MaxWidth(w)))
        {
            SyncDocumentFromLive();
            _terrainShell.SaveTerrainState(writeAssets: true);
        }

        EditorGUILayout.EndVertical();
    }

    // Real vanilla maps use 512/1024/2048/4096, not
    // just the previously-hardcoded 1024 (measured directly against the
    // 56-map vanilla corpus: some real shipped maps use 512, others use
    // 2048; 4096 isn't attested in that corpus but is included
    // since the underlying format has no fixed ceiling).
    private static readonly int[] GridSizeOptions = { 512, 1024, 2048, 4096 };

    private void DrawGridSizeControl(float w)
    {
        var current = Data.CostsGridSize;
        var currentIndex = System.Array.IndexOf(GridSizeOptions, current);
        var labels = GridSizeOptions.Select(s => s.ToString()).ToArray();
        EditorGUILayout.LabelField("Costs/Hfield grid size", EditorStyles.miniLabel);
        var nextIndex = SC2TerrainShell.DrawUnifiedSegmentGrid(Mathf.Max(0, currentIndex), labels, 4);
        var nextSize = GridSizeOptions[Mathf.Clamp(nextIndex, 0, GridSizeOptions.Length - 1)];
        if (nextSize != current)
        {
            var proceed = EditorUtility.DisplayDialog("Change grid size",
                $"Changing the Costs/Hfield grid size from {current} to {nextSize} resizes " +
                "every Costs block and the TerrainTypes grid to match, discarding any " +
                "hand-painted data outside the new size (and re-filling with defaults if " +
                "the new size is larger). This cannot be undone via ↩ Undo.\n\nProceed?",
                "Change Size", "Cancel");
            if (proceed)
            {
                RecordUndo();
                Data.CostsGridSize = nextSize;
                Data.EnsureCostsAndTerrainTypes();
                _overlayDirty = true;
                SyncDocumentFromLive();
                // The live height grid's own
                // resolution (mapX/mapY = CostsGridSize+1) just changed.
                _terrainShell?.InvalidateLiveHeightGrid();
            }
        }
        if (current != 1024)
            SC2TerrainShell.DrawUnifiedNote($"Non-default grid size ({current}) — make sure the terrain's actual world-space footprint matches this size (0..{current}); the grid is still anchored at world origin.");
    }

    /// <summary>Moved to the very top of the whole Costs panel (above grid
    /// size, above the Costs/Terrain Types target selector) — this is a
    /// foundational, map-level setting (which real vanilla Costs block-set
    /// EnsureCostsAndTerrainTypes creates), not a minor per-field toggle, so
    /// it reads as the first real decision the panel presents rather than
    /// being buried inside DrawCostsControls. A real 2-option switcher (not
    /// a checkbox) so "3 blocks vs 5 blocks" is what the control visibly
    /// says, not an indirect "is water playable" question the user has to
    /// translate themselves. Deliberately its own setting, not
    /// InfoModel.WaterEnabled (Environment > Water tab) - that field only
    /// controls whether the decorative water surface renders, a different
    /// question from whether this map has any real navigable water. See
    /// SC2TerrainData.PlayableWater.</summary>
    private void DrawBlockSetSwitcher(float w)
    {
        var labels = new[] { "3 Blocks (Land)", "5 Blocks (+ Water)" };
        var selected = Data.PlayableWater ? 1 : 0;
        var next = SC2TerrainShell.DrawUnifiedSegmentGrid(selected, labels, 2);
        var newPlayableWater = next == 1;
        if (newPlayableWater != Data.PlayableWater)
            ApplyPlayableWaterToggle(newPlayableWater);
    }

    private void DrawPaintToggleAndRadius(float w)
    {
        // _painting is a tool-armed toggle, not paintable data — deliberately
        // not part of Snapshot/undo, same reasoning as SC2PairsMode's
        // selected-pair index staying untracked.
        var paintingLabel = _painting ? "■ Painting ACTIVE (click terrain)" : "▶ Enable Painting";
        var painting = GUILayout.Toggle(_painting, paintingLabel, "Button", GUILayout.Height(SC2UiScale.Pxf(26f)), GUILayout.Width(w), GUILayout.MaxWidth(w));
        if (painting != _painting)
            _painting = painting;

        _brushRadius = SC2TerrainShell.DrawCompactSlider("Radius (F resize)", _brushRadius, 1f, 128f,
            onValueChangeStart: RecordUndo);

        if (Data.Symmetry != SymmetryMode.None)
            EditorGUILayout.LabelField($"Symmetry: {Data.Symmetry} (Terrain tab settings)", EditorStyles.centeredGreyMiniLabel);
    }

    /// <summary>Blocks 1+ are derived from block 0, but their automatic outline (the walls
    /// added around block 0's walls) can be marked as unneeded here.</summary>
    /// <summary>The brush mode a block's tab can actually use: an auto-managed block (1+)
    /// only has the Outline brush, block 0 and Manual Mode blocks only Impassable/Passable,
    /// the shore-hint block also Shore and Outline. Re-applied on every tab change, Manual
    /// Mode toggle, panel draw and undo, so the brush cannot be left in a mode its block
    /// does not offer (and thereby stop painting).</summary>
    private void EnforceCostsModeForActiveBlock()
    {
        if (ActiveTarget != PaintTarget.Costs || Data == null) return;
        if (IsAutoManagedCostsBlock(_activeBlock))
            _costsMode = OutlineMode;
        else if (_activeBlock != ShoreHintBlockIndex)
            _costsMode = Mathf.Min(_costsMode, 1);
        else if (_costsMode == OutlineMode && !OutlineAvailable(_activeBlock))
            _costsMode = 1;
    }

    private bool OutlineAvailable(int block) =>
        !_manualCostsMode && Data != null && block >= 1 && block < Data.CostsBlocks.Count;

    private void DrawOutlineControls(float w)
    {
        EditorGUILayout.BeginHorizontal(GUILayout.Width(w), GUILayout.MaxWidth(w));
        var swatch = GUILayoutUtility.GetRect(SC2UiScale.Pxf(16f), SC2UiScale.Pxf(16f), GUILayout.Width(SC2UiScale.Pxf(16f)));
        EditorGUI.DrawRect(swatch, OutlineOpenedColor);
        GUILayout.Label("Marked as unneeded outline (passable)", GUILayout.MinWidth(40), GUILayout.ExpandWidth(true));
        EditorGUILayout.EndHorizontal();
        SC2TerrainShell.DrawUnifiedNote("Outline brush: the lighter walls on this block are the outline added automatically around block 0's walls. Click paints them passable (they turn green); the cost ring is recomputed around what remains. Only outline cells are affected. Hold Ctrl to restore the outline.");
    }

    private void DrawCostsControls(float w)
    {
        // On the shore-hint-capable block's
        // own tab, "Shore" is a real THIRD paint mode (costsMode 2), not
        // just a passive color legend — see PaintAt's own Shore-mode branch
        // for the exact semantics (only ever converts an impassable cell to
        // pinned-passable; never touches an already-passable one; Ctrl
        // unpins instead of "painting the opposite"). Every other block's
        // tab keeps the plain 2-way toggle.
        var isShoreHintBlock = _activeBlock == ShoreHintBlockIndex;
        var outlineOnBlock = isShoreHintBlock && OutlineAvailable(_activeBlock);
        int newCostsMode;
        if (outlineOnBlock)
        {
            // segment 3 is the Outline brush (mode value OutlineMode)
            var shown = SC2TerrainShell.DrawUnifiedSegmentGrid(Mathf.Clamp(_costsMode, 0, 3), new[] { "Impassable", "Passable", "Shore", "Outline" }, 4);
            newCostsMode = shown;
        }
        else
            newCostsMode = isShoreHintBlock
                ? SC2TerrainShell.DrawUnifiedSegmentGrid(Mathf.Clamp(_costsMode, 0, 2), new[] { "Impassable", "Passable", "Shore" }, 3)
                : SC2TerrainShell.DrawUnifiedSegmentGrid(Mathf.Clamp(_costsMode, 0, 1), new[] { "Impassable", "Passable" }, 2);
        if (newCostsMode != _costsMode)
        {
            RecordUndo();
            _costsMode = newCostsMode;
        }

        if (isShoreHintBlock)
        {
            EditorGUILayout.BeginHorizontal(GUILayout.Width(w), GUILayout.MaxWidth(w));
            var swatch = GUILayoutUtility.GetRect(SC2UiScale.Pxf(16f), SC2UiScale.Pxf(16f), GUILayout.Width(SC2UiScale.Pxf(16f)));
            EditorGUI.DrawRect(swatch, ShoreHintOverrideColor);
            GUILayout.Label("Shore color — every pinned cell renders this color, regardless of passable/impassable", GUILayout.MinWidth(40), GUILayout.ExpandWidth(true));
            EditorGUILayout.EndHorizontal();
            if (_costsMode == OutlineMode)
                DrawOutlineControls(w);
            if (_costsMode == 2)
                SC2TerrainShell.DrawUnifiedNote("Shore mode: click paints any currently-impassable cell passable and pins it (survives every auto-resync). Already-passable cells are left untouched. Hold Ctrl to unpin instead (the cell reverts to the automatic value on the next resync).");
        }

        // Which block is "active" for painting is now driven by the host's
        // top-level sub-tab row (see GetSubtabLabels/DrawEmbeddedGUI) — this
        // toggle is the one remaining override, for strokes meant to apply
        // uniformly across every block regardless of which one's tab is open
        // (e.g. marking an area impassable everywhere at once).
        var newAllBlocks = EditorGUILayout.ToggleLeft("All blocks at once (ignore active tab)", _allBlocks, GUILayout.Width(w), GUILayout.MaxWidth(w));
        if (newAllBlocks != _allBlocks)
        {
            RecordUndo();
            _allBlocks = newAllBlocks;
        }

        EditorGUILayout.Space(4);
        if (GUILayout.Button("Recompute Islands (all blocks)", GUILayout.Height(SC2UiScale.Pxf(22f)), GUILayout.Width(w), GUILayout.MaxWidth(w)))
        {
            RecordUndo();
            RecomputeAllIslandsOnly();
            SyncDocumentFromLive();
        }

        DrawCopyBlockControls(w);
        DrawDilateControls(w);
        DrawImportCostsImageControls(w);

        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("Auto-generate from height", EditorStyles.miniLabel);
        // SC2EnvFieldGui.Slider — the same real slider widget Environment's
        // own tabs use (label + drag handle + a manually-editable, non-
        // clamped number box), not the plain EditorGUILayout.Slider used
        // before. Keys are unique per field so this panel's own drag/text
        // state never collides with anything else using the same helper.
        // Block 0 is the ONLY block ever
        // generated from height now (see TryAutoGenerateBlockZero/
        // RecomputeDerivedCostsBlocks) and it's always the "Small"-classified
        // block in both real vanilla profiles — the old Medium/Large slope
        // sliders were dead (nothing ever read them), same for the water
        // tolerance sliders (GenerateFromHeight no longer uses a depth
        // tolerance at all when playable water is on: water is fully
        // passable there except the separate mandatory shoreline band).
        // Removed rather than left visible-but-inert. Range starts at 0 on
        // every slider here (was 5 on the old slope ones).
        _genThresholds.MaxSlopeDegreesSmall = SC2EnvFieldGui.Slider("Max slope", _genThresholds.MaxSlopeDegreesSmall, 0f, 85f, "costsgen:maxSlopeSmall");
        _genThresholds.MinIslandSize = Mathf.RoundToInt(SC2EnvFieldGui.Slider("Min island size (cells)", _genThresholds.MinIslandSize, 0f, 500f, "costsgen:minIslandSize"));
        // The shoreline border thickness is
        // a real slider instead of a hardcoded constant — read fresh by
        // RecomputeDerivedCostsBlocks every time it runs, so moving this
        // takes effect on the very next resync (any paint stroke, Dilate,
        // Copy, or a fresh Auto-generate), not just a brand-new generation.
        // Only meaningful for the 5-block playable-water profile.
        _genThresholds.ShorelineBandMargin = Mathf.RoundToInt(SC2EnvFieldGui.Slider("Shoreline border thickness (cells)", _genThresholds.ShorelineBandMargin, 0f, 20f, "costsgen:shorelineBandMargin"));
        if (GUILayout.Button("Auto-generate from height (block 0)", GUILayout.Height(SC2UiScale.Pxf(22f)), GUILayout.Width(w), GUILayout.MaxWidth(w)))
            TryAutoGenerateBlockZero();
        var blockCount = Data.CostsBlocks.Count;
        var cascadeSupported = blockCount == 3 || blockCount == 5;
        SC2TerrainShell.DrawUnifiedNote(cascadeSupported
            ? "Real slope/water-depth generation, calibrated against in-game testing, not asserted vanilla-exact values. Only block 0 is ever generated from height — every other block (and the shore-hint-capable block's own auto base) is immediately re-derived from this result, see the note on those tabs."
            : $"Real slope/water-depth generation for block 0. Note: auto-derivation of the other blocks only supports the real 3-block (land-only) or 5-block (playable-water) vanilla profiles — this map currently has {blockCount} blocks, so they won't resync automatically.");
    }

    /// <summary>Lets one Costs block's
    /// painted navmesh be copied wholesale into another (e.g. start
    /// Land_Medium from whatever's already painted in Land_Small, then
    /// adjust) — picks a source block via a segmented row, copies into
    /// whichever block the host's own sub-tab row currently has active
    /// (_activeBlock), same "active block = current tab" convention every
    /// other per-block action in this panel already uses.</summary>
    private void DrawCopyBlockControls(float w)
    {
        if (Data.CostsBlocks.Count < 2) return;
        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("Copy block", EditorStyles.miniLabel);
        var labels = Data.CostsBlocks.Select(b => b.Name).ToArray();
        _copySourceBlock = Mathf.Clamp(_copySourceBlock, 0, labels.Length - 1);
        _copySourceBlock = SC2TerrainShell.DrawUnifiedSegmentGrid(_copySourceBlock, labels, Mathf.Min(3, labels.Length));

        var hasActiveBlock = _activeBlock >= 0 && _activeBlock < Data.CostsBlocks.Count;
        var canCopy = hasActiveBlock && _copySourceBlock != _activeBlock;
        var destLabel = hasActiveBlock ? labels[_activeBlock] : "(no active block)";
        EditorGUI.BeginDisabledGroup(!canCopy);
        if (GUILayout.Button($"Copy '{labels[_copySourceBlock]}' → '{destLabel}'", GUILayout.Height(SC2UiScale.Pxf(22f)), GUILayout.Width(w), GUILayout.MaxWidth(w)))
        {
            RecordUndo();
            Data.CostsBlocks[_activeBlock].Navmesh = (byte[])Data.CostsBlocks[_copySourceBlock].Navmesh.Clone();
            // A copy onto the shore-hint block replaces its whole content
            // deliberately — pin every cell, otherwise the immediate resync
            // below would just overwrite it straight back from block 0.
            if (_activeBlock == ShoreHintBlockIndex && Data.CostsShoreHintOverrideMask != null)
                for (var i = 0; i < Data.CostsShoreHintOverrideMask.Length; i++)
                    Data.CostsShoreHintOverrideMask[i] = 1;
            RecomputeDerivedCostsBlocks();
            _overlayDirty = true;
            SyncDocumentFromLive();
        }
        EditorGUI.EndDisabledGroup();
    }

    /// <summary>Grows the active block's (or every block's, if "All blocks
    /// at once" above is checked) impassable region by a chosen number of
    /// cells — see SC2CostsAutoGen.DilateImpassable for why this is
    /// distinct from the navmesh format's own fixed 2px border marker.</summary>
    private void DrawDilateControls(float w)
    {
        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("Grow impassable area (safety margin)", EditorStyles.miniLabel);
        _dilateMargin = Mathf.RoundToInt(SC2EnvFieldGui.Slider("Margin (cells)", _dilateMargin, 1f, 20f, "costs:dilateMargin"));
        var hasTarget = _allBlocks || (_activeBlock >= 0 && _activeBlock < Data.CostsBlocks.Count);
        EditorGUI.BeginDisabledGroup(!hasTarget);
        if (GUILayout.Button($"Grow impassable by {_dilateMargin} cell(s)", GUILayout.Height(SC2UiScale.Pxf(22f)), GUILayout.Width(w), GUILayout.MaxWidth(w)))
            ApplyDilateImpassable();
        EditorGUI.EndDisabledGroup();
        SC2TerrainShell.DrawUnifiedNote("Adds real impassable cells within the chosen distance of any already-impassable cell — a pathing safety buffer around obstacles, not just a visual outline. Applies to the active block, or every block at once if 'All blocks at once' above is checked.");
    }

    private void ApplyDilateImpassable()
    {
        RecordUndo();
        var gridSize = Data.CostsGridSize;

        void DilateBlock(SC2CostsBlockData block, int blockIndex)
        {
            var impassable = SC2CostsAutoGen.NavmeshToImpassable(block.Navmesh);
            var grown = SC2CostsAutoGen.DilateImpassable(impassable, gridSize, gridSize, _dilateMargin);
            SC2CostsAutoGen.ApplyImpassableToNavmesh(grown, block.Navmesh, gridSize, gridSize);
            // Pin whichever cells this deliberately changed on the shore-hint
            // block, otherwise the resync right after this method returns
            // would just discard the dilate straight back to block 0's own
            // value there.
            if (blockIndex == ShoreHintBlockIndex && Data.CostsShoreHintOverrideMask != null)
                for (var i = 0; i < grown.Length && i < Data.CostsShoreHintOverrideMask.Length; i++)
                    if (grown[i] != impassable[i]) Data.CostsShoreHintOverrideMask[i] = 1;
        }

        if (_allBlocks)
        {
            for (var bi = 0; bi < Data.CostsBlocks.Count; bi++)
                if (!IsAutoManagedCostsBlock(bi)) DilateBlock(Data.CostsBlocks[bi], bi);
        }
        else if (_activeBlock >= 0 && _activeBlock < Data.CostsBlocks.Count && !IsAutoManagedCostsBlock(_activeBlock))
        {
            DilateBlock(Data.CostsBlocks[_activeBlock], _activeBlock);
        }

        RecomputeDerivedCostsBlocks();
        _overlayDirty = true;
        SyncDocumentFromLive();
    }

    /// <summary>Lets an artist paint the mask externally
    /// (Photoshop/GIMP/etc.) and import it straight into the active Costs
    /// block instead of hand-brushing it in-editor. Disabled for an auto-
    /// managed block (same restriction as regular painting) unless Manual
    /// Mode is on.</summary>
    private void DrawImportCostsImageControls(float w)
    {
        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("Import Costs image", EditorStyles.miniLabel);
        var newInvert = EditorGUILayout.ToggleLeft("Invert (black = passable, white = impassable)", _importCostsInvertColors, GUILayout.Width(w), GUILayout.MaxWidth(w));
        if (newInvert != _importCostsInvertColors)
        {
            RecordUndo();
            _importCostsInvertColors = newInvert;
        }
        var canImport = _activeBlock >= 0 && _activeBlock < Data.CostsBlocks.Count && !IsAutoManagedCostsBlock(_activeBlock);
        EditorGUI.BeginDisabledGroup(!canImport);
        if (GUILayout.Button("Import black/white image…", GUILayout.Height(SC2UiScale.Pxf(22f)), GUILayout.Width(w), GUILayout.MaxWidth(w)))
            ImportCostsImageFromFile();
        EditorGUI.EndDisabledGroup();
        SC2TerrainShell.DrawUnifiedNote(canImport
            ? "Resamples any image to the current grid size — white cells become passable, black become impassable (Invert flips that). Overwrites this block's whole navmesh."
            : "This block is automatically derived from block 0 — switch to block 0 (or the shore-hint block), or enable Manual Mode above, to import here.");
    }

    private void ImportCostsImageFromFile()
    {
        if (_activeBlock < 0 || _activeBlock >= Data.CostsBlocks.Count || IsAutoManagedCostsBlock(_activeBlock))
            return;

#if UNITY_EDITOR
        var path = EditorUtility.OpenFilePanel("Import Costs Image", "", "png,jpg,jpeg");
#else
        var path = SC2RuntimeFileDialog.OpenFile("Import Costs Image", "Image (*.png;*.jpg;*.jpeg)", "png");
#endif
        if (string.IsNullOrEmpty(path)) return;

        byte[] bytes;
        try
        {
            bytes = System.IO.File.ReadAllBytes(path);
        }
        catch (System.Exception ex)
        {
            EditorUtility.DisplayDialog("Import Costs Image", "Could not read the selected file: " + ex.Message, "OK");
            return;
        }

        var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        var decoded = ImageConversion.LoadImage(tex, bytes, false);
        if (!decoded || tex.width <= 0 || tex.height <= 0)
        {
            UnityEngine.Object.DestroyImmediate(tex);
            EditorUtility.DisplayDialog("Import Costs Image", "Could not decode the selected file as an image.", "OK");
            return;
        }

        var pixels = tex.GetPixels32();
        var texW = tex.width;
        var texH = tex.height;
        UnityEngine.Object.DestroyImmediate(tex);

        RecordUndo();
        var gridSize = Data.CostsGridSize;
        var impassable = new bool[gridSize * gridSize];
        for (var z = 0; z < gridSize; z++)
        {
            var ty = Mathf.Clamp(Mathf.FloorToInt((float)z / gridSize * texH), 0, texH - 1);
            for (var x = 0; x < gridSize; x++)
            {
                var tx = Mathf.Clamp(Mathf.FloorToInt((float)x / gridSize * texW), 0, texW - 1);
                var p = pixels[ty * texW + tx];
                var luminance = (p.r + p.g + p.b) / (3f * 255f);
                var passable = luminance > 0.5f;
                if (_importCostsInvertColors) passable = !passable;
                impassable[z * gridSize + x] = !passable;
            }
        }

        var block = Data.CostsBlocks[_activeBlock];
        block.Navmesh = SC2CostsAutoGen.BuildNavmesh(impassable, gridSize, gridSize);
        // An image import replaces the WHOLE block deliberately, same
        // reasoning as Copy-block's own pin-everything treatment — otherwise
        // the very next resync would just overwrite it straight back.
        if (_activeBlock == ShoreHintBlockIndex && Data.CostsShoreHintOverrideMask != null)
            for (var i = 0; i < Data.CostsShoreHintOverrideMask.Length; i++)
                Data.CostsShoreHintOverrideMask[i] = 1;

        RecomputeDerivedCostsBlocks();
        _overlayDirty = true;
        SyncDocumentFromLive();
    }

    /// <summary>Asks for confirmation before overwriting a block whose
    /// navmesh already looks hand-painted (anything other than the
    /// freshly-initialized all-passable default), so a stray click can't
    /// silently discard manual work — SC2CostsLayerDocument.PaintOverrides
    /// would be the real separation for this (auto-gen and paint deltas
    /// stored apart, so regenerating never loses hand painting), but that
    /// document-side split isn't wired to live edits yet (see its own doc
    /// comment) - this confirm dialog is the honest, available substitute.</summary>
    private bool AnyCostsBlockPainted() =>
        Data.CostsBlocks.Any(b => b.Navmesh != null && b.Navmesh.Any(v => v != 1));

    /// <summary>Height/slope generation
    /// applies ONLY to block 0 — every other block is a pure computed view
    /// (see RecomputeDerivedCostsBlocks), so generating them independently
    /// from height (the old TryAutoGenerateAllBlocks behavior) no longer
    /// makes sense.</summary>
    private void TryAutoGenerateBlockZero()
    {
        if (Data.CostsBlocks.Count == 0) return;
        if (AnyCostsBlockPainted())
        {
            var proceed = EditorUtility.DisplayDialog(
                "Auto-generate Costs",
                "Block 0 (and possibly others) already have hand-painted data. Auto-generating from height will overwrite block 0, and every derived block will immediately resync from the new result. Continue?",
                "Overwrite", "Cancel");
            if (!proceed) return;
        }

        RecordUndo();
        Data.CostsBlocks[0].Navmesh = SC2CostsAutoGen.GenerateFromHeight(
            Data, Data.CostsBlocks[0].Name, _terrainShell.SampleLiveHeightGrid, _genThresholds);
        RecomputeDerivedCostsBlocks();
        _overlayDirty = true;
        SyncDocumentFromLive();
    }

    /// <summary>Growth-only wall cells of b4 that Shore pins opened in the last
    /// recompute; painted with the shore tint in the overlay.</summary>
    private bool[] _shoreOpenedCells;

    /// <summary>Automatic outline cells of each block from the last recompute (before the
    /// user's marks); the Outline brush only accepts these.</summary>
    private bool[][] _outlineCells;

    /// <summary>Drops marks on cells that are no longer outline (block 0 changed there), so
    /// a stale mark cannot silently reappear later.</summary>
    private void ClearStaleOutlineMarks(bool[][] outline)
    {
        for (var b = 1; b < Data.CostsBlocks.Count && b < outline.Length; b++)
        {
            var mask = Data.CostsBlocks[b].OutlineOpenMask;
            if (mask == null) continue;
            for (var i = 0; i < mask.Length && i < outline[b].Length; i++)
                if (mask[i] != 0 && !outline[b][i]) mask[i] = 0;
        }
    }

    /// <summary>Every Costs block besides
    /// block 0 is now a pure computed view of block 0 — called after ANY
    /// change to block 0's own Navmesh (paint stroke, Auto-generate, Dilate,
    /// Copy block) so they never go stale. Only supports the two real
    /// vanilla profiles (3-block land-only, 5-block playable-water); a no-op
    /// for any other block count.
    ///
    /// Growth and ring values follow SC2CostsNavmeshRules (vanilla-measured):
    /// 3-block: block 1 = block 0 + 1 cross step; block 2 = block 1 + 3.
    ///
    /// 5-block (always PlayableWater==true, matching EnsureCostsAndTerrainTypes'
    /// own block-set choice): block 0 additionally gets a mandatory 3-cell
    /// impassable shoreline band baked in first — every raw-geometric water
    /// cell within 3 cells of raw land is forced impassable, everything else
    /// in the water is fully passable (see GenerateFromHeight — block 0 no
    /// longer uses a depth tolerance at all). Land walls of blocks 1/4 grow
    /// +1 and of blocks 2/3 +4 (cross steps); block 1 also blocks EVERY raw
    /// water cell; blocks 2/3 grow the water band by 2. Block 4
    /// (ShoreHintBlockIndex) = block 0's set, except any cell pinned in
    /// SC2TerrainData.CostsShoreHintOverrideMask keeps whatever the user
    /// hand-painted there verbatim — "shore hint" lives directly on that
    /// block's own Costs tab instead of a separate paint target. A pin that
    /// has become redundant (block 0's own fresh value already matches what
    /// was pinned) is cleared automatically right here:
    /// once block 0 stops disagreeing with a hand-painted cell, that cell
    /// should just track block 0 again instead of staying frozen (and
    /// tinted as shore) forever. Block 3 grows from block 4.
    ///
    /// Each block 1+ also has an automatic outline (walls that exist only because of
    /// the growth); cells the user marked with the Outline brush
    /// (SC2CostsBlockData.OutlineOpenMask) are opened, and the cost ring follows
    /// what remains.</summary>
    private void RecomputeDerivedCostsBlocks()
    {
        // Manual Mode: no block is auto-managed (see IsAutoManagedCostsBlock),
        // so there is nothing to derive — a no-op rather than silently
        // overwriting whatever the user just hand-painted on a normally-
        // derived block.
        if (_manualCostsMode) return;

        var shorelineBandMargin = Mathf.Max(0, _genThresholds.ShorelineBandMargin);

        var blockCount = Data.CostsBlocks.Count;
        if (blockCount != 3 && blockCount != 5) return;
        if (Data.CostsBlocks[0].Navmesh == null) return;

        var gridSize = Data.CostsGridSize;
        System.Func<float, float, float> sample = _terrainShell.SampleLiveHeightGrid;
        var imp0 = SC2CostsAutoGen.NavmeshToImpassable(Data.CostsBlocks[0].Navmesh);

        _shoreOpenedCells = null;
        var outlineMasks = Data.CostsBlocks.Select(b => b.OutlineOpenMask).ToArray();
        if (blockCount == 3)
        {
            var profile = SC2CostsNavmeshRules.DeriveLandOnlyProfile(imp0, outlineMasks, gridSize, gridSize);
            for (var b = 0; b < 3; b++)
                Data.CostsBlocks[b].Navmesh = SC2CostsNavmeshRules.BuildBlock(b, 3, profile.Walls[b], null, null, gridSize, gridSize);
            _outlineCells = profile.Outline;
            ClearStaleOutlineMarks(profile.Outline);
        }
        else
        {
            var isWaterRaw = SC2CostsAutoGen.ComputeRawWaterMask(Data, sample);
            if (Data.PlayableWater)
            {
                var isLandRaw = new bool[isWaterRaw.Length];
                for (var i = 0; i < isWaterRaw.Length; i++) isLandRaw[i] = !isWaterRaw[i];
                var landDilated = SC2CostsAutoGen.DilateImpassable(isLandRaw, gridSize, gridSize, shorelineBandMargin);
                for (var i = 0; i < imp0.Length; i++)
                    if (landDilated[i] && isWaterRaw[i]) imp0[i] = true;
            }

            var overrides = Data.CostsShoreHintOverrideMask;
            var pinned = SC2CostsAutoGen.NavmeshToImpassable(Data.CostsBlocks[4].Navmesh);
            if (overrides != null)
                for (var i = 0; i < imp0.Length && i < overrides.Length; i++)
                {
                    // Once block 0's own value catches up
                    // to match whatever this cell was pinned to (typically
                    // "passable", the only value Shore mode ever pins), the
                    // pin has become redundant — clear it so the cell tracks
                    // block 0 normally again instead of staying frozen (and
                    // tinted as shore) forever even after block 0 no longer
                    // disagrees with it.
                    if (overrides[i] != 0 && pinned[i] == imp0[i]) overrides[i] = 0;
                }

            var profile = SC2CostsNavmeshRules.DeriveWaterProfile(imp0, isWaterRaw, overrides, pinned, outlineMasks, gridSize, gridSize);
            var walls = profile.Walls;
            _shoreOpenedCells = profile.OpenedByPins;
            _outlineCells = profile.Outline;
            ClearStaleOutlineMarks(profile.Outline);
            var shoreZone = SC2CostsNavmeshRules.ShoreZone(walls[0], walls[1], walls[4], isWaterRaw, overrides, outlineMasks[4]);
            for (var b = 0; b < 5; b++)
                Data.CostsBlocks[b].Navmesh = SC2CostsNavmeshRules.BuildBlock(b, 5, walls[b], walls[4], isWaterRaw, gridSize, gridSize, shoreZone);
        }

        _overlayDirty = true;
    }

    /// <summary>Flips SC2TerrainData.PlayableWater and rebuilds CostsBlocks
    /// to match the corresponding real vanilla block-set (3-block land-only
    /// vs 5-block playable-water, see SC2TerrainData.EnsureCostsAndTerrainTypes) —
    /// unlike that method's own normal "only ever add missing entries"
    /// safety (which exists so loading an already-painted map never
    /// silently discards its blocks), this is a deliberate, explicit user
    /// action to switch profiles, so it really does replace the block list
    /// - confirmed first if anything already looks hand-painted. Immediately
    /// runs Auto-generate on the new set too (not left blank/all-passable):
    /// the point of toggling this is to see a real water-aware result, not
    /// an empty grid the user then has to remember to fill in.</summary>
    private void ApplyPlayableWaterToggle(bool playableWater)
    {
        if (AnyCostsBlockPainted())
        {
            var proceed = EditorUtility.DisplayDialog(
                "Playable water",
                "Switching this rebuilds the Costs block set (3 land-only blocks <-> 5 playable-water blocks) and discards any hand-painted Costs data. Continue?",
                "Switch", "Cancel");
            if (!proceed) return;
        }

        RecordUndo();
        Data.PlayableWater = playableWater;
        // Turning on playable water with no visible water plane
        // would mean Costs paints water-passable areas nobody can see -
        // nudge InfoModel.WaterEnabled on too so a real water plane appears
        // alongside the new block set. Deliberately one-directional: turning
        // PlayableWater back OFF does not turn WaterEnabled off, since a map
        // author may still want decorative water visible without it being
        // pathable, and turning WaterEnabled off independently afterward
        // remains a normal Environment-tab edit.
        if (playableWater && Data.EnvironmentHeader != null && Data.EnvironmentHeader.Length >= 508)
        {
            var info = new BdfIO.Formats.Info.InfoModel();
            System.Array.Copy(Data.EnvironmentHeader, info.Header, 508);
            if (info.WaterEnabled == 0)
            {
                info.WaterEnabled = 1;
                System.Array.Copy(info.Header, Data.EnvironmentHeader, 508);
            }
        }
        Data.CostsBlocks.Clear();
        // The block-set itself just changed shape (3 <-> 5 blocks) — any old
        // shore-hint pins no longer correspond to the same block layout.
        if (Data.CostsShoreHintOverrideMask != null)
            System.Array.Clear(Data.CostsShoreHintOverrideMask, 0, Data.CostsShoreHintOverrideMask.Length);
        Data.EnsureCostsAndTerrainTypes();
        Data.CostsBlocks[0].Navmesh = SC2CostsAutoGen.GenerateFromHeight(
            Data, Data.CostsBlocks[0].Name, _terrainShell.SampleLiveHeightGrid, _genThresholds);
        RecomputeDerivedCostsBlocks();
        _activeBlock = 0;
        _overlayDirty = true;
        SyncDocumentFromLive();
    }

    private void DrawTerrainTypesControls(float w)
    {
        SC2TerrainShell.DrawUnifiedNote("Paints the map's TerrainTypes grid (baked into hfield.bdf at export). Presets are editable labels/colors, not a confirmed vanilla-game id table.");
        for (var i = 0; i < _presets.Count; i++)
        {
            EditorGUILayout.BeginHorizontal(GUILayout.Width(w), GUILayout.MaxWidth(w));
            var selected = GUILayout.Toggle(_activePreset == i, "", GUILayout.Width(SC2UiScale.Pxf(18f)));
            if (selected && _activePreset != i)
            {
                RecordUndo();
                _activePreset = i;
            }
            var swatch = GUILayoutUtility.GetRect(SC2UiScale.Pxf(16f), SC2UiScale.Pxf(16f), GUILayout.Width(SC2UiScale.Pxf(16f)));
            EditorGUI.DrawRect(swatch, _presets[i].Color);
            // Fixed vanilla ids (2=Default, 3=Chasm_Bottom, see BuildDefaultPresets)
            // — the name is a label, not an editable field; renaming it here
            // wouldn't change which id actually gets painted/exported.
            GUILayout.Label(_presets[i].Label, GUILayout.MinWidth(40), GUILayout.ExpandWidth(true));
            EditorGUILayout.EndHorizontal();
        }
    }

    private void DrawUndoRedoRow(float w)
    {
        EditorGUILayout.BeginHorizontal(GUILayout.Width(w), GUILayout.MaxWidth(w));
        EditorGUI.BeginDisabledGroup(UndoCount == 0);
        if (GUILayout.Button($"↩ Undo ({UndoCount})")) Undo();
        EditorGUI.EndDisabledGroup();
        EditorGUI.BeginDisabledGroup(RedoCount == 0);
        if (GUILayout.Button($"↪ Redo ({RedoCount})")) Redo();
        EditorGUI.EndDisabledGroup();
        EditorGUILayout.EndHorizontal();
    }

    // ==================================================================
    // GameView input — called every frame the Costs tab is active, exactly
    // like SC2TerrainShell.OnGameViewGUI/HandleTerrainGameViewInput's own
    // per-frame call pattern in SC2GameViewEditorUI.
    // ==================================================================

    public void OnGameViewGUI(Camera camera, Rect viewportRect, Vector2 mousePosition, bool allowInput)
    {
        if (!IsReady || Runtime == null || camera == null)
            return;

        PushOverlayToRuntime();

        if (!allowInput)
            return;

        var e = Event.current;
        if (e == null || !_painting)
            return;
        if (!viewportRect.Contains(mousePosition))
            return;
        // Defense-in-depth alongside the side panel hiding the painting
        // toggle for an auto-managed block (see IsAutoManagedCostsBlock) —
        // _painting itself isn't per-block, so a stroke already armed on a
        // paintable block before switching tabs must not silently keep
        // writing into a now-active auto-managed one.
        EnforceCostsModeForActiveBlock();

        // A new stroke gets a fresh
        // rebuild of the live height grid (covers e.g. the imported terrain
        // having been repositioned via its Transform gizmo since the last
        // stroke, which has no other change-notification hook); a
        // continuing drag within the same stroke reuses the cached grid
        // (EnsureFresh is a cheap no-op when not dirty).
        if (e.type == EventType.MouseDown)
            _terrainShell.InvalidateLiveHeightGrid();

        var ray = SC2TerrainShell.GameGuiPointToRay(camera, viewportRect, mousePosition);
        // One generated heightmap, source-agnostic (live procedural terrain
        // + imported terrain.bdf, merged the same way real export already
        // does) - replaces the previous two-mechanism fallback
        // (TryRaycastImportedTerrain's Physics raycast, then
        // RaycastTerrainHeight's live-only analytic sample), which the user
        // found still silently painted against the live plane instead of
        // the imported surface. See SC2LiveHeightGrid's own header comment.
        var hasHit = _terrainShell.TryRaycastLiveHeightGrid(ray, out var hit);

        // F interactive radius resize — checked BEFORE the raycast-miss
        // early return below (not after) so an already-active resize keeps
        // responding to mouse movement/Escape/click-to-confirm even on a
        // frame where the cursor briefly strays off the raycastable surface;
        // HandleInput itself only needs hasHit/hit to START a new resize,
        // not to continue one already in progress.
        if (_brushRadiusGesture.HandleInput(camera, viewportRect, mousePosition, hasHit, hit, allowInput, ref _brushRadius, 1f, 128f,
            sampleHeight: _terrainShell != null ? _terrainShell.SampleLiveHeightGrid : null))
            return;

        if (!hasHit)
            return;

        // Brush cursor — screen-space IMGUI circle (SC2TerrainShell.
        // DrawGameViewBrush), NOT Handles.DrawSolidDisc/DrawWireDisc: Handles
        // are no-ops outside the Editor (see UnityEditorRuntimeShim), so a
        // Handles-based brush preview would silently never draw anything in
        // a standalone build. This is the same GameView-safe brush-drawing
        // helper SC2PaintMode already uses for terrain pair/blend painting.
        // Reflects what a click would ACTUALLY paint right now, including a
        // held-Ctrl erase override (IsErasingHeld) — so the cursor color
        // itself is the "erase button is on" feedback, not a separate UI
        // element.
        var erasingPreview = IsErasingHeld();
        Color brushColor;
        if (ActiveTarget == PaintTarget.Costs)
        {
            var previewMode = erasingPreview ? 1 - _costsMode : _costsMode;
            brushColor = _costsMode == OutlineMode
                ? (erasingPreview ? new Color(1f, 0.4f, 0.1f, 0.9f) : new Color(0.1f, 0.9f, 0.5f, 0.9f))
                : previewMode == 0 ? new Color(1f, 0.1f, 0.1f, 0.9f) : new Color(0.1f, 1f, 0.1f, 0.9f);
        }
        else
        {
            var previewPreset = erasingPreview ? _presets[0] : _presets[_activePreset];
            brushColor = new Color(previewPreset.Color.r, previewPreset.Color.g, previewPreset.Color.b, 0.9f);
        }
        SC2TerrainShell.DrawGameViewBrush(camera, viewportRect, hit, _brushRadius, brushColor, Color.clear,
            sampleHeight: _terrainShell != null ? _terrainShell.SampleLiveHeightGrid : null);

        // Costs painting mirrors symmetry per-cell at paint time
        // (ToGridSymmetry/SC2Symmetry.GetSymmetricCells below), not by
        // painting a second full brush circle at a mirrored world position
        // — but a point at GetSymmetricPositions(hit) still calls out where
        // that per-cell mirroring is centered, using the same Symmetry/
        // SymmetryCenter/RadialCount config both mechanisms share.
        if (Data.Symmetry != SymmetryMode.None)
            SC2TerrainShell.DrawSymmetryPointMarkers(camera, viewportRect, hit, Data.GetSymmetricPositions(hit), brushColor,
                sampleHeight: _terrainShell != null ? _terrainShell.SampleLiveHeightGrid : null);

        if (e.type == EventType.MouseDown && e.button == 0)
        {
            if (ActiveTarget == PaintTarget.TerrainTypes)
                RecordTerrainTypesStrokeStart();
            else
                RecordUndo();
            _lastPaintPos = new Vector2(hit.x, hit.z);
            PaintAt(_lastPaintPos.Value);
            _overlayDirty = true;
            RepaintHost?.Invoke();
        }
        else if (e.type == EventType.MouseDrag && e.button == 0)
        {
            var cur = new Vector2(hit.x, hit.z);
            var from = _lastPaintPos ?? cur;
            PaintLine(from, cur);
            _lastPaintPos = cur;
            _overlayDirty = true;
            RepaintHost?.Invoke();
        }
        else if (e.type == EventType.MouseUp && e.button == 0)
        {
            _lastPaintPos = null;
            // Sync once per completed stroke, not per drag frame — a Put()
            // call hashes the whole grid (~1MB per block), so doing this on
            // every MouseDrag would be real, avoidable per-frame cost for no
            // benefit (the shell's own legacy undo already covers every
            // intermediate frame of the stroke).
            if (ActiveTarget == PaintTarget.TerrainTypes)
                CommitTerrainTypesStroke();
            else
            {
                // Block 0 (and the shore-hint-capable
                // block) are the only Costs blocks still hand-painted —
                // every other block auto-resyncs right after any stroke
                // completes, not just after the dedicated generate button.
                RecomputeDerivedCostsBlocks();
                SyncDocumentFromLive();
            }
        }
    }

    private void PaintLine(Vector2 from, Vector2 to)
    {
        var dist = Vector2.Distance(from, to);
        var step = Mathf.Max(1f, _brushRadius * 0.5f);
        var steps = Mathf.Max(1, Mathf.CeilToInt(dist / step));
        for (var i = 0; i <= steps; i++)
        {
            var t = steps > 0 ? (float)i / steps : 0f;
            PaintAt(Vector2.Lerp(from, to, t));
        }
    }

    private static SC2SymmetryMode ToGridSymmetry(SymmetryMode m) => m switch
    {
        SymmetryMode.X => SC2SymmetryMode.MirrorX,
        SymmetryMode.Y => SC2SymmetryMode.MirrorZ,
        SymmetryMode.XY => SC2SymmetryMode.MirrorXZ,
        SymmetryMode.Radial => SC2SymmetryMode.Rotational,
        SymmetryMode.DiagonalTLBR => SC2SymmetryMode.MirrorDiagTLBR,
        SymmetryMode.DiagonalTRBL => SC2SymmetryMode.MirrorDiagTRBL,
        SymmetryMode.DiagonalBoth => SC2SymmetryMode.MirrorDiagBoth,
        _ => SC2SymmetryMode.None
    };

    /// <summary>Holding Ctrl while painting temporarily acts as an eraser —
    /// same behavior for Props/Costs/TerrainTypes/RNM-blend painting
    /// alike. Costs has no separate erase flag to begin with (it's already
    /// a 2-state paint), so "erase" means paint the OPPOSITE of the
    /// currently selected Impassable/Passable mode; TerrainTypes has no
    /// erase flag either, so "erase" means paint the "Default" preset
    /// (index 0 — see BuildDefaultPresets' own doc comment: it's the one
    /// preset with alpha 0, i.e. "no override here") regardless of which
    /// preset is currently selected in the UI. Neither the _costsMode
    /// toggle nor _activePreset selection itself changes — only what a
    /// click/drag actually paints, exactly like a held modifier key in any
    /// other paint tool (nothing in the panel visibly flips).</summary>
    private static bool IsErasingHeld() => SC2Hotkeys.ModifierHeld("Costs.EraseModifier", Event.current);

    private void PaintAt(Vector2 worldXZ)
    {
        var erasing = IsErasingHeld();
        var cx = Mathf.RoundToInt(worldXZ.x);
        var cz = Mathf.RoundToInt(worldXZ.y);
        var gridSize = Data.CostsGridSize;
        var symMode = ToGridSymmetry(Data.Symmetry);
        // Grid space is world-unit-aligned (1 cell = 1 world unit, 0..1024 —
        // same convention SC2HfieldProjectionService/SC2TerrainExporter use),
        // so the sculpt symmetry center (world XZ) maps directly to grid
        // coordinates without rescaling.
        var symCenter = new Vector2(Data.SymmetryCenter.x, Data.SymmetryCenter.z);
        var r2 = _brushRadius * _brushRadius;

        for (var dz = -Mathf.CeilToInt(_brushRadius); dz <= Mathf.CeilToInt(_brushRadius); dz++)
        for (var dx = -Mathf.CeilToInt(_brushRadius); dx <= Mathf.CeilToInt(_brushRadius); dx++)
        {
            if (dx * dx + dz * dz > r2) continue;
            var cells = SC2Symmetry.GetSymmetricCells(new Vector2Int(cx + dx, cz + dz), symCenter, gridSize, symMode, Data.RadialCount);
            foreach (var cell in cells)
            {
                if (cell.x < 0 || cell.x >= gridSize || cell.y < 0 || cell.y >= gridSize) continue;
                var idx = cell.y * gridSize + cell.x;
                if (ActiveTarget == PaintTarget.Costs && _costsMode == OutlineMode && OutlineAvailable(_activeBlock))
                {
                    // The Outline brush only ever marks an automatic outline cell as
                    // unneeded (passable); Ctrl restores it. Any other cell is left
                    // untouched — the real navmesh follows on the next resync.
                    var mask = Data.CostsBlocks[_activeBlock].OutlineOpenMask;
                    var outline = _outlineCells != null && _activeBlock < _outlineCells.Length ? _outlineCells[_activeBlock] : null;
                    if (mask != null && idx < mask.Length)
                    {
                        if (erasing) mask[idx] = 0;
                        else if (outline != null && idx < outline.Length && outline[idx]) mask[idx] = 1;
                    }
                }
                else if (ActiveTarget == PaintTarget.Costs && _activeBlock == ShoreHintBlockIndex && _costsMode == 2)
                {
                    // "Shore" is a literal
                    // third paint mode, not just a passive color legend —
                    // it ONLY ever converts an already-impassable cell to
                    // passable (pinned so RecomputeDerivedCostsBlocks
                    // preserves it verbatim from now on); an already-
                    // passable cell is left untouched entirely. Ctrl unpins
                    // instead of "painting the opposite" — the cell then
                    // reverts to the automatic block-0-derived value on the
                    // very next resync, no need to touch Navmesh here too.
                    var shoreBlock = Data.CostsBlocks[ShoreHintBlockIndex];
                    var overrides = Data.CostsShoreHintOverrideMask;
                    if (erasing)
                    {
                        if (overrides != null && idx < overrides.Length) overrides[idx] = 0;
                    }
                    else if (idx < shoreBlock.Navmesh.Length && shoreBlock.Navmesh[idx] == 255)
                    {
                        shoreBlock.Navmesh[idx] = 1;
                        if (overrides != null && idx < overrides.Length) overrides[idx] = 1;
                    }
                }
                else if (ActiveTarget == PaintTarget.Costs)
                {
                    var mode = erasing ? 1 - _costsMode : _costsMode;
                    // _costsMode can only be 2 ("Shore") on the shore-hint
                    // block's own tab (see DrawCostsControls/the subTab-
                    // change clamp) — the branch above already handles that
                    // combination, so anything reaching here is a normal 0/1
                    // toggle. Defensive fallback to Passable just in case.
                    if (mode < 0 || mode > 1) mode = 1;
                    byte value = mode == 0 ? (byte)255 : (byte)1;
                    // Auto-managed blocks (everything but block 0 and the
                    // shore-hint-capable block) are skipped even under "All
                    // blocks at once" — they're pure computed views now (see
                    // IsAutoManagedCostsBlock/RecomputeDerivedCostsBlocks)
                    // and would just get overwritten by the resync this same
                    // stroke triggers on MouseUp anyway. Plain Impassable/
                    // Passable painting on the shore-hint block no longer
                    // pins anything itself — only "Shore" mode above does.
                    if (_allBlocks)
                    {
                        for (var bi = 0; bi < Data.CostsBlocks.Count; bi++)
                        {
                            if (IsAutoManagedCostsBlock(bi)) continue;
                            Data.CostsBlocks[bi].Navmesh[idx] = value;
                        }
                    }
                    else if (_activeBlock >= 0 && _activeBlock < Data.CostsBlocks.Count && !IsAutoManagedCostsBlock(_activeBlock))
                    {
                        Data.CostsBlocks[_activeBlock].Navmesh[idx] = value;
                    }
                }
                else
                {
                    Data.TerrainTypes[idx] = erasing ? _presets[0].Id : _presets[_activePreset].Id;
                }
            }
        }
    }

    private void RecomputeAllIslandsOnly()
    {
        var gridSize = Data.CostsGridSize;
        foreach (var block in Data.CostsBlocks)
        {
            var impassable = SC2CostsAutoGen.NavmeshToImpassable(block.Navmesh);
            SC2CostsAutoGen.ApplyImpassableToNavmesh(impassable, block.Navmesh, gridSize, gridSize);
        }
        _overlayDirty = true;
    }

    // ==================================================================
    // Overlay — a texture parameter set directly on the terrain's shared,
    // persistent chunk material (SC2TerrainRuntime.SetCostsOverlayTexture),
    // sampled every frame during normal camera rendering by
    // SC2TerrainView.shader's ApplyCostsOverlay — the same reliable approach
    // _PairColorTex/SetPairColorRT already uses. An earlier version of this
    // issued ad-hoc Graphics.DrawMesh calls from OnGUI once per frame
    // instead; that depended on exactly when OnGUI fires relative to the
    // camera's render pass, which isn't guaranteed — the overlay could
    // vanish the moment the mouse button was released (whatever consumed
    // that input Event could cause HandleCostsGameViewInput to be skipped
    // for a frame), and the Editor's "Game" tab doesn't necessarily repaint
    // every frame the same way a real Build &amp; Run player's Update loop
    // does, so it could be entirely missing there compared to a build. A
    // plain shader-texture sample has neither problem: the chunk always
    // renders every frame regardless of GUI/input events, so it always
    // shows whatever was last uploaded here.
    // ==================================================================

    /// <summary>Call every frame the Costs tab is active — keeps the overlay
    /// texture enabled/up to date. Cheap when nothing changed (early-outs
    /// unless _overlayDirty).</summary>
    public void PushOverlayToRuntime()
    {
        if (!IsReady || Runtime == null) return;
        if (_overlayDirty) RebuildOverlayTexture();
        Runtime.SetCostsOverlayTexture(_overlayTex);
        PushOverlayToImportedTerrains(_overlayTex);
        PushOverlayToDecalPreview?.Invoke(_overlayTex, true, Data.CostsGridSize);
    }

    /// <summary>Wired in SC2GameViewEditorUI (same cross-shell callback
    /// pattern as SC2TerrainShell.GetImportedTerrainsForExport) — a placed
    /// decal's own mesh would otherwise completely occlude the Costs
    /// paint overlay wherever it sat, since decals render as a separate
    /// mesh on top with no idea the overlay exists. See SC2DecalsShell.
    /// SetCostsOverlay's own doc comment for the receiving side.</summary>
    public System.Action<Texture, bool, float> PushOverlayToDecalPreview { get; set; }

    /// <summary>Call when leaving the Costs tab so other tabs (Terrain, etc.)
    /// don't keep showing the costs/terrainTypes tint.</summary>
    public void DisableOverlay()
    {
        Runtime?.SetCostsOverlayTexture(null);
        PushOverlayToImportedTerrains(null);
        PushOverlayToDecalPreview?.Invoke(null, false, Data.CostsGridSize);
    }

    /// <summary>Call when leaving the Costs tab — turns off the "Enable
    /// Painting" arm state so it doesn't stay silently active in the
    /// background (e.g. resuming painting on a stray click after switching
    /// back into Costs, or while OverlayCosts keeps the overlay visible from
    /// another tab). Purely a tool-armed toggle, not paintable data, so this
    /// is not an undoable action — same reasoning _painting's own field
    /// comment already gives.</summary>
    public void DisarmPainting() => _painting = false;

    /// <summary>Runtime.
    /// SetCostsOverlayTexture only ever applies to the live procedural
    /// terrain's shared chunk material (SC2TerrainView.shader) — invisible
    /// for a fully-imported/ImportOnlyMode map, where painting therefore had
    /// no visible feedback at all even once the underlying data was correct
    /// (see SC2LiveHeightGrid). SC2Terrain.shader (used by imported
    /// terrain.bdf materials) gained the same _CostsOverlayTex/
    /// _CostsOverlayEnabled properties + blend logic - this pushes the same
    /// texture there too. Mutates the imported object's real shared
    /// materials in place (NOT MeshRenderer.materials, which would
    /// instantiate copies and desync from SC2ImportedTerrainExportData's
    /// OriginalMaterials-based RNM-toggle/undo-restore logic elsewhere in
    /// this codebase, which already relies on mutating the same shared
    /// Material objects in place).</summary>
    private void PushOverlayToImportedTerrains(Texture overlay)
    {
        var importedTerrains = _terrainShell?.GetImportedTerrainsForExport?.Invoke();
        if (importedTerrains == null) return;
        foreach (var imported in importedTerrains)
        {
            var renderer = imported?.Transform != null ? imported.Transform.GetComponent<MeshRenderer>() : null;
            if (renderer == null) continue;
            foreach (var mat in renderer.sharedMaterials)
            {
                if (mat == null || !mat.HasProperty("_CostsOverlayTex")) continue;
                if (overlay != null) mat.SetTexture("_CostsOverlayTex", overlay);
                mat.SetFloat("_CostsOverlayEnabled", overlay != null ? 1f : 0f);
                // World-space UV, not the mesh's own (per-material-local)
                // uv2 - see SC2Terrain.shader's own Properties comment.
                if (mat.HasProperty("_CostsOverlayGridSize"))
                    mat.SetFloat("_CostsOverlayGridSize", Data.CostsGridSize);
            }
        }
    }

    private void RebuildOverlayTexture()
    {
        var gridSize = Data.CostsGridSize;
        // CostsGridSize can now change at runtime (the
        // user can pick a different grid size mid-session), so a texture
        // allocated for a previous size must be rebuilt, not just reused —
        // Texture2D.SetPixels below throws if _overlayColors.Length no longer
        // matches _overlayTex's own width*height.
        if (_overlayTex == null || _overlayTex.width != gridSize || _overlayTex.height != gridSize)
        {
            if (_overlayTex != null) UnityEngine.Object.DestroyImmediate(_overlayTex);
            _overlayTex = new Texture2D(gridSize, gridSize, TextureFormat.RGBA32, false, true)
                { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            _overlayColors = new Color[gridSize * gridSize];
        }

        if (ActiveTarget == PaintTarget.Costs)
        {
            var block = _activeBlock >= 0 && _activeBlock < Data.CostsBlocks.Count ? Data.CostsBlocks[_activeBlock] : null;
            var navmesh = block?.Navmesh;
            var tint = BlockTintFor(_activeBlock);
            // Shore-mode painting itself
            // still writes real passable/impassable Costs data (that part
            // was already right for the game) — but the EDITOR overlay
            // needs to mark a pinned cell as "shore" regardless of whether
            // it now reads passable or impassable, not just when it happens
            // to still be impassable. A pinned cell always gets the shore
            // tint; every other cell keeps the normal "only impassable gets
            // tinted" rule.
            var overrides = _activeBlock == ShoreHintBlockIndex ? Data.CostsShoreHintOverrideMask : null;
            var opened = _activeBlock == ShoreHintBlockIndex ? _shoreOpenedCells : null;
            var outline = OutlineAvailable(_activeBlock) && _outlineCells != null && _activeBlock < _outlineCells.Length ? _outlineCells[_activeBlock] : null;
            var outlineMask = outline != null ? block.OutlineOpenMask : null;
            var outlineTint = Color.Lerp(tint, Color.white, 0.45f);
            outlineTint.a = 0.5f;
            for (var i = 0; i < _overlayColors.Length; i++)
            {
                if ((overrides != null && i < overrides.Length && overrides[i] != 0)
                    || (opened != null && i < opened.Length && opened[i]))
                {
                    _overlayColors[i] = ShoreHintOverrideColor;
                    continue;
                }
                if (outline != null && i < outline.Length && outline[i])
                {
                    _overlayColors[i] = outlineMask != null && i < outlineMask.Length && outlineMask[i] != 0 ? OutlineOpenedColor : outlineTint;
                    continue;
                }
                var isImpassable = navmesh != null && i < navmesh.Length && navmesh[i] == 255;
                _overlayColors[i] = isImpassable ? tint : Color.clear;
            }
        }
        else if (ActiveTarget == PaintTarget.TerrainTypes)
        {
            // The game itself treats anything that isn't Chasm_Bottom (id 3)
            // as Default — there's no separate "unknown id" case in-game, so
            // the overlay matches that: only id 3 tints red, everything else
            // (including id 2 and any other value) is alpha 0 / natural.
            var chasmColor = _presets.First(p => p.Id == 3).Color;
            var terrainTypes = Data.TerrainTypes;
            for (var i = 0; i < _overlayColors.Length; i++)
            {
                var id = i < terrainTypes.Length ? terrainTypes[i] : (ushort)2;
                _overlayColors[i] = id == 3 ? chasmColor : Color.clear;
            }
        }

        _overlayTex.SetPixels(_overlayColors);
        _overlayTex.Apply(false);
        _overlayDirty = false;
    }

    private static readonly Color[] BlockTints =
    {
        new Color(1f, 0.1f, 0.1f, 0.55f), new Color(1f, 0.5f, 0.1f, 0.55f),
        new Color(1f, 0.9f, 0f, 0.55f), new Color(0.2f, 0.4f, 1f, 0.55f),
        new Color(0f, 0.2f, 1f, 0.55f),
    };
    private static Color BlockTintFor(int block) => BlockTints[block >= 0 && block < BlockTints.Length ? block : 0];

    // ==================================================================
    // Undo / redo
    // ==================================================================

    private void RecordUndo()
    {
        if (!IsReady) return;
        Data.EnsureCostsAndTerrainTypes();
        _undoStack.Push(Capture());
        if (_undoStack.Count > MaxUndoSteps)
        {
            // Stack.ToArray() returns elements top-first (most recent
            // first); Take(MaxUndoSteps) keeps the most recent N in that
            // same top-first order. Pushing them back in that order would
            // push the most-recent one FIRST, burying it under the rest —
            // Reverse() first so the final Push leaves the most-recent
            // entry back on top, same as before trimming.
            var tmp = _undoStack.ToArray();
            _undoStack.Clear();
            foreach (var s in tmp.Take(MaxUndoSteps).Reverse()) _undoStack.Push(s);
        }
        _redoStack.Clear();
        _undo.Record(new SC2DelegateUndoChannel(TryLegacyUndo, TryLegacyRedo));
    }

    /// <summary>Call BEFORE a terrain-types paint stroke starts (mirrors
    /// RecordUndo's own "record before, not after" convention). Degrades to
    /// the ordinary legacy RecordUndo() when no Session/BlobStore is wired,
    /// so TerrainTypes painting still has SOME undo coverage even without a
    /// map session — same degrade-not-throw pattern used elsewhere (e.g.
    /// SC2TerrainCombinedUndo.ExecuteCommand with no SC2CommandHistory).</summary>
    private void RecordTerrainTypesStrokeStart()
    {
        if (!IsReady) return;
        if (Session?.Document == null || Session.BlobStore == null)
        {
            RecordUndo();
            return;
        }

        Data.EnsureCostsAndTerrainTypes();
        _undo.Record(new SC2DelegateUndoChannel(TryCommandUndo, TryCommandRedo));
    }

    private Snapshot Capture() => new Snapshot
    {
        Target = ActiveTarget,
        CostsNavmesh = Data.CostsBlocks.Select(b => (byte[])b.Navmesh.Clone()).ToArray(),
        TerrainTypes = (ushort[])Data.TerrainTypes.Clone(),
        CostsShoreHintOverrideMask = (byte[])Data.CostsShoreHintOverrideMask.Clone(),
        CostsOutlineOpen = Data.CostsBlocks.Select(b => (byte[])(b.OutlineOpenMask ?? System.Array.Empty<byte>()).Clone()).ToArray(),
        CostsMode = _costsMode,
        AllBlocks = _allBlocks,
        ManualCostsMode = _manualCostsMode,
        ActiveBlock = _activeBlock,
        ActivePreset = _activePreset,
        BrushRadius = _brushRadius,
        PresetLabels = _presets.Select(p => p.Label).ToList(),
    };

    public void Undo()
    {
        if (!IsReady) return;
        _undo.Undo();
    }

    public void Redo()
    {
        if (!IsReady) return;
        _undo.Redo();
    }

    private bool TryCommandUndo()
    {
        if (Session?.Document == null || !Session.History.CanUndo) return false;
        Session.History.Undo(Session.Document);
        PullTerrainTypesFromDocument();
        return true;
    }

    private bool TryCommandRedo()
    {
        if (Session?.Document == null || !Session.History.CanRedo) return false;
        Session.History.Redo(Session.Document);
        PullTerrainTypesFromDocument();
        return true;
    }

    private bool TryLegacyUndo()
    {
        if (_undoStack.Count == 0) return false;
        _redoStack.Push(Capture());
        Apply(_undoStack.Pop());
        SyncDocumentFromLive();
        return true;
    }

    private bool TryLegacyRedo()
    {
        if (_redoStack.Count == 0) return false;
        _undoStack.Push(Capture());
        Apply(_redoStack.Pop());
        SyncDocumentFromLive();
        return true;
    }

    // ==================================================================
    // Document sync. Costs-target edits and all tool-config state still use
    // a one-way mirror, NOT a second undo system: SyncDocumentFromLive/
    // SyncHfieldTerrainTypesFromLive call each command's Do() directly and
    // do NOT push onto Session.History, so this shell's own legacy
    // Stack<Snapshot> above remains the sole undo/redo authority for those
    // edits (validated in the Terrain UI cutover pass's spirit: don't build
    // a second combined-undo layer where the legacy one already fully
    // covers the user-visible behavior).
    //
    // TerrainTypes-target edits are the one exception (see
    // CommitTerrainTypesStroke below): those DO go through
    // Session.History.Execute, recorded as a Command-sourced channel in
    // the shared _undo coordinator above, so a completed terrain-types
    // stroke is real, exportable, document-owned undo — not just a mirror
    // of it.
    // ==================================================================

    /// <summary>Public so SC2TerrainShell.DataChanged (fired at the exact
    /// load/create moment) can trigger this sync even when the Costs tab
    /// itself is never drawn this session — same reasoning as SC2ObjectsShell.
    /// EnsureObjectsDocumentSynced. Data.EnsureCostsAndTerrainTypes() is
    /// called here (not just relied on from DrawEmbeddedGUI) since this can
    /// now run before the Costs tab has ever drawn a single frame this
    /// session.</summary>
    public void SyncDocumentFromLive()
    {
        if (!IsReady || Session?.Document == null || Session.BlobStore == null)
            return;

        Data.EnsureCostsAndTerrainTypes();
        foreach (var block in Data.CostsBlocks)
        {
            EnsureCostsLayerDocument(block.Name);
            var command = new SC2SetCostsLayerGridCommand(block.Name, block.Navmesh, Session.BlobStore);
            Session.Document.RaiseChanged(command.Do(Session.Document));
        }

        SyncHfieldTerrainTypesFromLive();
    }

    private void EnsureCostsLayerDocument(string blockName)
    {
        var costs = Session.Document.Costs;
        if (costs.Find(blockName) == null)
            costs.Layers.Add(new SC2CostsLayerDocument { Id = blockName, FieldName = blockName, DisplayName = blockName });
    }

    private void SyncHfieldTerrainTypesFromLive()
    {
        if (!TryBuildTerrainTypesBytes(out var bytes))
            return;

        var command = new SC2SetHfieldTerrainTypesCommand(bytes, Session.BlobStore);
        Session.Document.RaiseChanged(command.Do(Session.Document));
    }

    /// <summary>Same 8-bit assumption SC2HfieldLegacyBridge already
    /// enforces — returns false (skip the sync) rather than silently wrap/
    /// corrupt a value that doesn't fit, if that assumption is ever
    /// violated.</summary>
    private bool TryBuildTerrainTypesBytes(out byte[] bytes)
    {
        bytes = null;
        var terrainTypes = Data.TerrainTypes;
        if (terrainTypes == null || terrainTypes.Length == 0)
            return false;

        var result = new byte[terrainTypes.Length];
        for (var i = 0; i < terrainTypes.Length; i++)
        {
            if (terrainTypes[i] > byte.MaxValue)
                return false;
            result[i] = (byte)terrainTypes[i];
        }
        bytes = result;
        return true;
    }

    /// <summary>Commits a COMPLETED terrain-types paint stroke as a real,
    /// undoable ISC2DocumentCommand. Unlike SyncHfieldTerrainTypesFromLive
    /// (a fire-and-forget "make the document match whatever the legacy
    /// stack/live data already settled on" call, used by Save/Recompute and
    /// by this shell's own legacy Undo/Redo replay), this is the ONLY call
    /// site that should ever push a NEW forward entry onto Session.History —
    /// RecordTerrainTypesStrokeStart already recorded the matching
    /// Command-sourced channel at MouseDown.</summary>
    private void CommitTerrainTypesStroke()
    {
        if (Session?.Document == null || Session.BlobStore == null)
        {
            // Degrades to the pre-cutover behavior — RecordTerrainTypesStrokeStart
            // fell back to a legacy RecordUndo() in this same situation, so
            // finish that stroke the same way it started.
            SyncDocumentFromLive();
            return;
        }
        if (!TryBuildTerrainTypesBytes(out var bytes))
            return;

        Session.History.Execute(Session.Document, new SC2SetHfieldTerrainTypesCommand(bytes, Session.BlobStore));
    }

    /// <summary>Pulls TerrainTypes back from the document's blob after a
    /// Command-sourced Undo/Redo, which mutated Session.Document.Hfield
    /// directly rather than through the paint tool — same reasoning as
    /// SC2EnvironmentShell.SyncFromDocument / SC2Collision2Shell.
    /// SyncOffsetFromDocument.</summary>
    private void PullTerrainTypesFromDocument()
    {
        if (Session?.Document == null || Session.BlobStore == null) return;
        var blobRef = Session.Document.Hfield.TerrainTypesUInt8;
        if (blobRef.IsEmpty) return;

        using var stream = Session.BlobStore.OpenRead(blobRef.ContentHash);
        using var ms = new System.IO.MemoryStream();
        stream.CopyTo(ms);
        var bytes = ms.ToArray();

        var terrainTypes = new ushort[bytes.Length];
        for (var i = 0; i < bytes.Length; i++)
            terrainTypes[i] = bytes[i];
        Data.TerrainTypes = terrainTypes;

        _overlayDirty = true;
        RepaintHost?.Invoke();
    }

    private void Apply(Snapshot s)
    {
        for (var i = 0; i < Data.CostsBlocks.Count && i < s.CostsNavmesh.Length; i++)
            Data.CostsBlocks[i].Navmesh = s.CostsNavmesh[i];
        Data.TerrainTypes = s.TerrainTypes;
        Data.CostsShoreHintOverrideMask = s.CostsShoreHintOverrideMask;
        if (s.CostsOutlineOpen != null)
            for (var i = 0; i < Data.CostsBlocks.Count && i < s.CostsOutlineOpen.Length; i++)
                Data.CostsBlocks[i].OutlineOpenMask = s.CostsOutlineOpen[i];
        ActiveTarget = s.Target;
        _costsMode = s.CostsMode;
        _allBlocks = s.AllBlocks;
        _manualCostsMode = s.ManualCostsMode;
        _activeBlock = s.ActiveBlock;
        _activePreset = s.ActivePreset;
        _brushRadius = s.BrushRadius;
        for (var i = 0; i < _presets.Count && i < s.PresetLabels.Count; i++)
            _presets[i].Label = s.PresetLabels[i];
        EnforceCostsModeForActiveBlock();
        _overlayDirty = true;
    }

    public void Dispose()
    {
        DisableOverlay();
        if (_overlayTex != null) { Object.DestroyImmediate(_overlayTex); _overlayTex = null; }
    }
}
