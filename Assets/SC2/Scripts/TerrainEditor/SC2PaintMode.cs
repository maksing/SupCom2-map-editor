using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEditor;
using SC2.MapEditor.SmartExport;

/// <summary>
/// Paint mode — two sub-modes:
///   Assign: paints pair index into PairMapRT via GPU blit
///   RNM:    paints blend alpha into BlendRT via GPU blit
/// All brush operations = one Graphics.Blit, zero CPU per-pixel work.
///
/// Shared, unmodified, between the core and the background terrain — same
/// reasoning and same ActiveRuntime/ActiveTerrainData/ActiveWorldOffset
/// pattern as SC2GeometryMode (see that class' own header comment for the
/// full rationale, including which settings deliberately stay core-only:
/// Symmetry and the brush's own shape settings, here UseCustomBrush/
/// CustomBrushTexturePath/RandomizeBrushRotation*).
/// </summary>
public class SC2PaintMode
{
    // Which core map this instance was built for — see
    // SC2GeometryMode.OwnerData's own doc comment.
    public SC2TerrainData OwnerData { get; }
    private SC2TerrainShell _win;

    /// <summary>The surface actually being read/written right now.</summary>
    public SC2TerrainData Data => _win.ActiveTerrainData;

    private enum Sub { Assign, RNM }
    private Sub   _sub      = Sub.Assign;
    private float _radius   = 20f;
    private float _strength = 1f;

    private Vector3 _brushPos;
    private bool    _brushVis, _isPainting;
    private double  _lastApply;
    private const double kInterval = 0.016;
    private double  _lastSyncTime;
    private const double kSyncThrottle = 0.25; // min seconds between PairMap GPU readbacks

    // F interactive resize, Blender-style — see
    // SC2BrushRadiusGesture's own doc comment. Shared across both
    // sub-modes since _radius itself already is.
    private readonly SC2BrushRadiusGesture _radiusGesture = new();

    // Assign
    private int  _assignPair = 0;
    private bool _selectMode = false;
    private readonly HashSet<int> _sel = new();

    // RNM
    private int   _rnmPair  = 0;
    private float _rnmBlend = 1f;
    private bool  _rnmErase = false;
    // Brush lowers the active layer's coverage instead of painting value (the layer eraser); never on the Base layer.
    private bool  _rnmRemoveFromLayer = false;
    // Structural layer edits (add/delete/move/...) are queued by the buttons and run at the next Layout event, so the
    // control tree never changes between an event and the repaint that follows it.
    private readonly Queue<System.Action> _pendingLayerActions = new();
    private bool   _layerSettingsNeedFlush;
    private double _layerSettingsChangedAt;
    // ── Auto outline (Assign Pairs) ──
    // While on, a finished stroke outlines the border of the cells it newly gave to the pair, on the group's "Outline"
    // blend mask layer, and clears outlines that are no longer along a pair border. Only cells that changed to the pair in
    // this stroke are outlined, so repainting existing areas draws nothing new.
    // Pairs the brush must not touch: one set for the Assign Pairs brush (cells of a locked pair keep their pair) and one for the
    // Blend Mask brush (nothing is painted on the cells of a locked pair). Kept for the session.
    private readonly SC2PairLockSet _assignLock = new SC2PairLockSet();
    private readonly SC2PairLockSet _blendLock = new SC2PairLockSet();
    private float[] _lockBuffer;
    private bool _assignLockFoldout, _blendLockFoldout;

    // A custom stamp brush places a stamp every _stampSpacing x the brush diameter along the stroke, not one per input event (see
    // SC2StampStroke).
    private readonly SC2StampStroke _stampStroke = new SC2StampStroke();
    private float _stampSpacing = 0.4f;
    // Off by default: the stroke dabs on every input event as before; on, it places stamps by distance.
    private bool _stampSpaced;

    private bool  _autoOutline = false;
    private float _outlineWidth = 8f;
    private float _outlineSoftness = 0.5f;
    // How far the cell-shaped region border is rounded off before the band is measured from it, in grid cells.
    private float _outlineSmoothing = 1f;
    // How far the outline continues onto the neighbouring pairs, in world units (0 = inside the pair only).
    private float _outlineSpread = 4f;
    // The blend alpha of an outline is always full: the outline is the pair's D2 itself.
    private const float _outlineBlend = 1f;
    private string _outlineStatus = "";
    // Pair assignments (per triangle) as they were when the current stroke began.
    private short[] _strokeBefore;
    // While set, the undo snapshot captures this surface and pair instead of the brush's own (see OutlinePairBorder).
    private SC2TerrainData _captureData;
    private SC2TerrainRuntime _captureRuntime;
    private int _capturePairIndex;

    // Cached CPU snapshot of BlendRT for the preview swatch (see DrawRnm) —
    // deliberately a Texture2D, never the live RenderTexture, and throttled.
    private Texture2D _blendPreviewTex;
    private double    _blendPreviewNextUpdate;

    // Optional custom stamp for the Blend
    // (RNM) brush — see SC2PaintBrush.shader/SC2TerrainRuntime.PaintBlend.
    // Data.UseCustomBrush/CustomBrushTexturePath are the persisted (saved
    // with the map) source of truth; these two fields are purely this
    // session's resolved-texture cache, same shape as
    // SC2InfoModelFieldDrawer.DrawInfoTextureField's own cache/cachedPath
    // pair — re-resolved only when the path actually changes underneath it.
    private Texture _customBrushCache;
    private string  _customBrushCachedPath = "\0";
    // Rolled once per stroke (mouse-down) in HandleRnm when
    // Data.RandomizeBrushRotation is on, held fixed for the whole drag —
    // never re-rolled per frame, or the stamp would flicker incoherently
    // instead of reading as one rotated stamp.
    private float   _rnmBrushRotationRad;

    // Bumped by every Sync*/InvalidatePendingPairSync call — lets an
    // in-flight ASYNC GPU readback (scheduled on MouseUp, see HandleAssign)
    // detect that it's now stale before it overwrites Data.TrianglePairIndex.
    // Without this, painting a stroke then immediately pressing Ctrl+Z raced
    // with that readback: Undo correctly restored the pre-stroke CPU data and
    // pushed it back to the GPU (SC2TerrainShell.ApplyRestoredSnapshot →
    // RestorePairMapFromData), but the readback that was already queued
    // BEFORE the undo still carried the painted (pre-undo) bytes and, once
    // it completed a frame or two later, silently reapplied them — Ctrl+Z
    // appeared to do nothing.
    private int _pairSyncGeneration;

    /// <summary>Called by SC2TerrainShell.ApplyRestoredSnapshot() after every
    /// Undo/Redo so a readback already in flight from before that Undo/Redo
    /// gets discarded instead of clobbering the just-restored state.</summary>
    public void InvalidatePendingPairSync() => _pairSyncGeneration++;

    public SC2PaintMode(SC2TerrainShell win, SC2TerrainData data)
    { _win = win; OwnerData = data; }

    // ── Panel ──────────────────────────────────────────────

    public void DrawPanel()
    {
        var w = SC2TerrainShell.GetPanelContentWidth();
        EditorGUILayout.BeginVertical(GUILayout.Width(w), GUILayout.MaxWidth(w));
        // Each group of controls gets its own heading and a gap above it, so the grids and sliders do not run together.
        SC2TerrainShell.DrawUnifiedSectionLabel("Paint mode");
        var modeLabels = new[] { "Assign Pairs", "Blend Mask" };
        _sub = (Sub)SC2TerrainShell.DrawUnifiedSegmentGrid((int)_sub, modeLabels, 2);

        EditorGUILayout.Space(SC2UiScale.Pxf(10f));
        SC2TerrainShell.DrawUnifiedSectionLabel("Brush settings");
        _radius   = SC2TerrainShell.DrawCompactSlider("Radius",   _radius,   1f, 1000f);
        _strength = SC2TerrainShell.DrawCompactSlider("Strength", _strength, 0.01f, 1f);
        SC2TerrainShell.DrawUnifiedNote("Shift+Scroll = Radius   Ctrl+Scroll = Strength");

        // Symmetry always follows the CORE's own setting (_win.Data), not
        // the dynamic Data — see SC2GeometryMode's own header comment.
        if (_win.Data.Symmetry != SymmetryMode.None)
        {
            EditorGUILayout.LabelField($"Symmetry: {_win.Data.Symmetry}", EditorStyles.centeredGreyMiniLabel);
            _win.Data.SymmetryCenter = DrawVector3Row("Center", _win.Data.SymmetryCenter, w);
        }

        EditorGUILayout.Space(4);
        switch (_sub)
        {
            case Sub.Assign: DrawAssign(); break;
            case Sub.RNM:    DrawRnm();    break;
        }
        EditorGUILayout.EndVertical();
    }

    /// <summary>The pair chooser (with its colour swatch) used by the Assign Pairs panel.</summary>
    /// <summary>"Lock pairs": a foldout with one toggle per pair (and one for the cells that have no pair), plus Lock all / Unlock /
    /// Invert. A locked pair is left alone by the brush.</summary>
    private void DrawPairLocks(float w, string title, SC2PairLockSet locks, ref bool foldout, string whatLockMeans)
    {
        var count = Data.Pairs.Count;
        var lockedCount = 0;
        for (var i = 0; i < count; i++) if (locks.IsLocked(i)) lockedCount++;
        if (locks.IsLocked(-1)) lockedCount++;
        if (!SC2EnvFieldGui.Foldout(ref foldout, lockedCount > 0 ? $"{title} ({lockedCount} locked)" : title)) return;

        for (var i = 0; i < count; i++)
        {
            EditorGUILayout.BeginHorizontal(GUILayout.Width(w), GUILayout.MaxWidth(w));
            var swatch = GUILayoutUtility.GetRect(SC2UiScale.Pxf(12f), SC2UiScale.Pxf(16f), GUILayout.Width(SC2UiScale.Pxf(12f)), GUILayout.Height(SC2UiScale.Pxf(16f)));
            EditorGUI.DrawRect(swatch, Data.PairColor(i));
            locks.Set(i, EditorGUILayout.ToggleLeft(Data.Pairs[i].Name, locks.IsLocked(i), GUILayout.Width(Mathf.Max(60f, w - 20f))));
            EditorGUILayout.EndHorizontal();
        }
        locks.Set(-1, EditorGUILayout.ToggleLeft("(no pair)", locks.IsLocked(-1), GUILayout.Width(w), GUILayout.MaxWidth(w)));

        var bw = Mathf.Max(40f, (w - 8f) / 3f);
        EditorGUILayout.BeginHorizontal(GUILayout.Width(w), GUILayout.MaxWidth(w));
        if (GUILayout.Button("Lock all", GUILayout.Width(bw))) locks.LockAll(count);
        if (GUILayout.Button("Unlock", GUILayout.Width(bw))) locks.UnlockAll();
        if (GUILayout.Button("Invert", GUILayout.Width(bw))) locks.Invert(count);
        EditorGUILayout.EndHorizontal();
        SC2TerrainShell.DrawUnifiedNote(whatLockMeans);
    }

    /// <summary>The lock table for the brush shader, or null when nothing is locked.</summary>
    private float[] LockTableOrNull(SC2PairLockSet locks)
    {
        if (!locks.Any) return null;
        _lockBuffer = locks.ToShaderArray(_lockBuffer);
        return _lockBuffer;
    }

    void DrawAssignPairPopup(float w)
    {
        // Pair selector with colour swatches
        var names = new string[Data.Pairs.Count + 1];
        names[0] = "(Unassign)";
        for (var i = 0; i < Data.Pairs.Count; i++) names[i+1] = Data.Pairs[i].Name;
        EditorGUILayout.BeginHorizontal(GUILayout.Width(w), GUILayout.MaxWidth(w));
        if (_assignPair >= 0 && _assignPair < Data.Pairs.Count)
        {
            var swatch = GUILayoutUtility.GetRect(SC2UiScale.Pxf(14f), SC2UiScale.Pxf(18f), GUILayout.Width(SC2UiScale.Pxf(14f)), GUILayout.Height(SC2UiScale.Pxf(18f)));
            EditorGUI.DrawRect(swatch, Data.PairColor(_assignPair));
        }
        _assignPair = EditorGUILayout.Popup("paint-assign-pair", _assignPair + 1, names, SC2UiScale.PopupStyle(), GUILayout.Width(Mathf.Max(60f, w - 20f)), GUILayout.MaxWidth(Mathf.Max(60f, w - 20f))) - 1;
        EditorGUILayout.EndHorizontal();
    }

    void DrawAssign()
    {
        if (Data.Pairs.Count == 0)
        { SC2TerrainShell.DrawUnifiedNote("Create pairs in the Pairs tab first."); return; }

        var w = SC2TerrainShell.GetPanelContentWidth();
        DrawAssignPairPopup(w);
        DrawPairLocks(w, "Lock pairs", _assignLock, ref _assignLockFoldout,
            "A locked pair keeps its cells: the brush, Select+Assign and Assign ALL do not overwrite them. To paint only over some pairs, press Lock all and untick those pairs.");

        _autoOutline = EditorGUILayout.Toggle("Auto outline (D2)", _autoOutline, GUILayout.Width(w), GUILayout.MaxWidth(w));
        if (_autoOutline)
        {
            GetOutlineMinimums(Data, out var minWidth, out var minSpread, out var minSmoothing);
            _outlineWidth = SC2TerrainShell.DrawCompactSlider("Outline width (units)", _outlineWidth, minWidth, 64f, "auto-outline");
            _outlineSoftness = SC2TerrainShell.DrawCompactSlider("Outline softness", _outlineSoftness, 0f, 1f, "auto-outline");
            _outlineSmoothing = SC2TerrainShell.DrawCompactSlider("Outline smoothing (cells)", _outlineSmoothing, 0f, SC2PairOutlineUpdater.MaximumSmoothingCells, "auto-outline");
            _outlineSpread = SC2TerrainShell.DrawCompactSlider("Spread onto neighbors (units)", _outlineSpread, 0f, 32f, "auto-outline");
            // Below the smallest values the outline is thinner than the mask can show: a spread or smoothing above zero is raised to it.
            if (_outlineSpread > 0f && _outlineSpread < minSpread) _outlineSpread = minSpread;
            if (_outlineSmoothing > 0f && _outlineSmoothing < minSmoothing) _outlineSmoothing = minSmoothing;
            SC2TerrainShell.DrawUnifiedNote($"Smallest values for this map: width {minWidth:0.##} units, spread {minSpread:0.##} units, smoothing {minSmoothing:0.##} cells (two mask pixels; spread and smoothing can also be 0).");
            SC2TerrainShell.DrawUnifiedNote("After each stroke, the closed region of this pair that the stroke touched is outlined with the pair's D2, inside the region and (Spread) a little onto the neighbouring pairs. All outlines go on one Blend Mask layer, \"Outline\"; the region's previous outline is replaced, and outlines that are no longer along a pair border are removed.");
            if (!string.IsNullOrEmpty(_outlineStatus)) SC2TerrainShell.DrawUnifiedNote(_outlineStatus);
        }

        _selectMode = EditorGUILayout.Toggle("Select+Assign", _selectMode, GUILayout.Width(w), GUILayout.MaxWidth(w));
        if (_selectMode)
        {
            EditorGUILayout.LabelField($"Selected: {_sel.Count} tris", GUILayout.Width(w), GUILayout.MaxWidth(w));
            EditorGUILayout.BeginHorizontal(GUILayout.Width(w), GUILayout.MaxWidth(w));
            var bw = Mathf.Max(48f, (w - 4f) * 0.5f);
            if (GUILayout.Button("Apply", GUILayout.Width(bw))) ApplySelection();
            if (GUILayout.Button("Deselect", GUILayout.Width(bw)))     _sel.Clear();
            EditorGUILayout.EndHorizontal();
        }

        EditorGUILayout.Space(4);

        if (GUILayout.Button("Assign ALL to this pair", GUILayout.Height(SC2UiScale.Pxf(26f)), GUILayout.Width(w), GUILayout.MaxWidth(w)) && _assignPair >= 0
            && EditorUtility.DisplayDialog("Assign All",
               $"All triangles → '{Data.Pairs[_assignPair].Name}'?", "Yes", "Cancel"))
        {
            _win.RecordUndoCheckpointFor(Data, "Assign All");
            short[] assignAllBefore;
            if (_assignLock.Any)
            {
                // Locked pairs keep their cells: assign on the CPU array, skipping them, then push it to the GPU map.
                SyncPairMapToCPU();
                assignAllBefore = (short[])Data.TrianglePairIndex.Clone();
                for (var ti = 0; ti < Data.TrianglePairIndex.Length; ti++)
                    if (!_assignLock.IsLocked(Data.TrianglePairIndex[ti])) Data.AssignTriangleToPair(ti, _assignPair);
                _win.ActiveRuntime?.RestorePairMapFromData(Data);
            }
            else
            {
                // Fill PairMapRT with pair index colour via blit
                if (_win.ActiveRuntime != null)
                {
                    var prev = RenderTexture.active;
                    RenderTexture.active = _win.ActiveRuntime.PairMapRT;
                    GL.Clear(false, true, new Color(_assignPair / 255f, 0, 0, 1));
                    RenderTexture.active = prev;
                }
                assignAllBefore = (short[])Data.TrianglePairIndex.Clone();
                Data.AssignAllTrianglesToPair(_assignPair);
            }
            OutlineNewlyPaintedCells(Data, _assignPair, assignAllBefore, syncFirst: false);
            _win.MarkDirtyAndRepaint();
        }

        if (GUILayout.Button("Clear All", GUILayout.Height(SC2UiScale.Pxf(22f)), GUILayout.Width(w), GUILayout.MaxWidth(w))
            && EditorUtility.DisplayDialog("Clear", "Reset all assignments?", "Yes", "Cancel"))
        {
            _win.RecordUndoCheckpointFor(Data, "Clear All");
            if (_win.ActiveRuntime != null)
            {
                var prev = RenderTexture.active;
                RenderTexture.active = _win.ActiveRuntime.PairMapRT;
                GL.Clear(false, true, new Color(255f/255f, 0, 0, 1)); // 255 = unassigned (-1)
                RenderTexture.active = prev;
            }
            var clearBefore = (short[])Data.TrianglePairIndex.Clone();
            var n = Data.TrianglePairIndex.Length;
            for (var i = 0; i < n; i++) Data.TrianglePairIndex[i] = -1;
            OutlineNewlyPaintedCells(Data, -1, clearBefore, syncFirst: false);
            foreach (var p in Data.Pairs) p.InvalidateTriCount();
            _win.MarkDirtyAndRepaint();
        }
    }

    void DrawRnm()
    {
        if (Data.Pairs.Count == 0)
        { SC2TerrainShell.DrawUnifiedNote("Create pairs first."); return; }

        var names = new string[Data.Pairs.Count];
        for (var i = 0; i < Data.Pairs.Count; i++) names[i] = Data.Pairs[i].Name;
        var w = SC2TerrainShell.GetPanelContentWidth();
        if (Event.current.type == EventType.Layout) RunPendingLayerActions();
        EditorGUILayout.LabelField("Pair", EditorStyles.miniLabel);
        var nextRnmPair = EditorGUILayout.Popup("paint-blend-pair", _rnmPair, names, SC2UiScale.PopupStyle(), GUILayout.Width(w), GUILayout.MaxWidth(w));
        if (nextRnmPair != _rnmPair)
        {
            _rnmPair = nextRnmPair;
            _win.ActiveRuntime?.SetActivePairForBlend(_rnmPair, Data);
        }
        else
        {
            _win.ActiveRuntime?.SetActivePairForBlend(_rnmPair, Data);
        }
        DrawLayerPanel(w);
        DrawPairLocks(w, "Lock pairs", _blendLock, ref _blendLockFoldout,
            "The brush does not paint on the cells of a locked pair. To paint only on some pairs, press Lock all and untick those pairs. Reset and Fill still act on the whole layer.");
        _rnmBlend = SC2TerrainShell.DrawCompactSlider("Blend alpha", _rnmBlend, 0f, 1f);
        EditorGUILayout.LabelField(
            _rnmBlend < .1f ? "← D1 (base)" :
            _rnmBlend > .9f ? "→ D2 (blend)" : "↔ blending",
            EditorStyles.centeredGreyMiniLabel);
        _rnmErase = EditorGUILayout.Toggle("Erase (→0)", _rnmErase);
        var layerRuntime = _win.ActiveRuntime;
        var canRemoveFromLayer = layerRuntime != null && layerRuntime.HasActiveLayers && layerRuntime.ActiveBlendStack.ActiveIndex > 0;
        if (canRemoveFromLayer)
            _rnmRemoveFromLayer = EditorGUILayout.Toggle("Remove from layer", _rnmRemoveFromLayer);
        else
            _rnmRemoveFromLayer = false;

        // Brush shape is a shared setting, always the CORE's own _win.Data —
        // see SC2GeometryMode's own header comment for why.
        EditorGUILayout.Space(SC2UiScale.Pxf(10f));
        SC2TerrainShell.DrawUnifiedSectionLabel("Brush shape");
        var brushShapeLabels = new[] { "Round", "Custom" };
        var nextBrushShape = SC2TerrainShell.DrawUnifiedSegmentGrid(_win.Data.UseCustomBrush ? 1 : 0, brushShapeLabels, 2) == 1;
        if (nextBrushShape != _win.Data.UseCustomBrush)
        {
            _win.RecordUndoCheckpoint("Change Paint Brush Shape");
            _win.Data.UseCustomBrush = nextBrushShape;
            EditorUtility.SetDirty(_win.Data);
        }
        if (_win.Data.UseCustomBrush)
        {
            DrawCustomBrushPicker();
            _stampSpaced = EditorGUILayout.Toggle("Place stamps by distance", _stampSpaced);
            if (_stampSpaced)
                _stampSpacing = SC2TerrainShell.DrawCompactSlider("Stamp spacing (% of diameter)", _stampSpacing * 100f, 10f, 200f, "blend-stamp") / 100f;
            var nextRandomize = EditorGUILayout.Toggle("Randomize Rotation", _win.Data.RandomizeBrushRotation);
            if (nextRandomize != _win.Data.RandomizeBrushRotation)
            {
                _win.RecordUndoCheckpoint("Change Paint Brush Randomize Rotation");
                _win.Data.RandomizeBrushRotation = nextRandomize;
                EditorUtility.SetDirty(_win.Data);
            }
            if (_win.Data.RandomizeBrushRotation)
            {
                var nextContinuous = EditorGUILayout.Toggle("Randomize While Dragging", _win.Data.RandomizeBrushRotationContinuous);
                if (nextContinuous != _win.Data.RandomizeBrushRotationContinuous)
                {
                    _win.RecordUndoCheckpoint("Change Paint Brush Randomize Continuous");
                    _win.Data.RandomizeBrushRotationContinuous = nextContinuous;
                    EditorUtility.SetDirty(_win.Data);
                }
            }
        }

        // Preview of BlendRT. GUI.DrawTexture sampling a RenderTexture can leave
        // RenderTexture.active pointed at that RT as a side effect (observed to
        // be worse/more consistent in a standalone Build & Run player than in
        // the Editor) — if left unrestored, every control drawn AFTER this
        // preview for the rest of the current OnGUI frame (Reset/Fill buttons,
        // everything below) would render straight into BlendRT instead of the
        // screen, continuously re-corrupting it every single repaint. This is
        // what caused exported RNM masks to contain a captured frame of the
        // editor's own UI, and why it survived a manual Reset.
        if (_win.ActiveRuntime != null && _win.ActiveRuntime.BlendRT != null)
        {
            // Full square (was a fixed 60px-tall strip regardless of panel
            // width) — BlendRT's own snapshot is square, so a wide-short
            // rect just squeezed it down to a thin, hard-to-read band via
            // ScaleToFit's letterboxing instead of showing it at a real,
            // legible size.
            var r = EditorGUILayout.GetControlRect(GUILayout.Height(w), GUILayout.Width(w), GUILayout.MaxWidth(w));
            // Draw a CPU-owned Texture2D SNAPSHOT of BlendRT, never the live
            // BlendRT RenderTexture itself. IMGUI batches and defers its actual
            // GPU draws to the end of the frame; drawing a live RenderTexture
            // that way behaves differently in a standalone player than in the
            // Editor and could bind BlendRT as the active render target during
            // that deferred flush, compositing the whole UI frame straight into
            // the blend mask — which is exactly how a screenshot of the editor
            // interface ended up baked into the terrain in Build & Run, and why
            // an immediate save/restore of RenderTexture.active around this draw
            // had no effect (the real draw happened later). A plain Texture2D
            // has no such feedback path. Throttled so the readback isn't every
            // frame. The grey backdrop still shows through alpha=0 (unpainted)
            // areas.
            EditorGUI.DrawRect(r, new Color(0.5f, 0.5f, 0.5f, 1f));
            var now = EditorApplication.timeSinceStartup;
            if (_blendPreviewTex == null || now >= _blendPreviewNextUpdate)
            {
                _blendPreviewTex = _win.ActiveRuntime.GetBlendPreviewSnapshot();
                _blendPreviewNextUpdate = now + 0.2;
            }
            if (_blendPreviewTex != null)
                GUI.DrawTexture(r, _blendPreviewTex, ScaleMode.ScaleToFit, true);
        }

        EditorGUILayout.BeginHorizontal(GUILayout.Width(w), GUILayout.MaxWidth(w));
        var bw = Mathf.Max(48f, (w - 4f) * 0.5f);
        var fillRuntime = _win.ActiveRuntime;
        if (GUILayout.Button("Reset (α=0)", GUILayout.Height(SC2UiScale.Pxf(22f)), GUILayout.Width(bw)) && fillRuntime != null)
        {
            if (fillRuntime.HasActiveLayers)
                // The Base layer resets to value 0 (fully covering), any other layer becomes fully transparent.
                RunLayerEdit("Reset Blend Layer", stack => fillRuntime.FillActiveLayer(0f, stack.ActiveIndex == 0 ? 1f : 0f), rebuild: false);
            else
                ClearBlendRT(fillRuntime, 0f);
        }
        if (GUILayout.Button("Fill (α=1)", GUILayout.Height(SC2UiScale.Pxf(22f)), GUILayout.Width(bw)) && fillRuntime != null)
        {
            if (fillRuntime.HasActiveLayers)
                RunLayerEdit("Fill Blend Layer", stack => fillRuntime.FillActiveLayer(1f, 1f), rebuild: false);
            else
                ClearBlendRT(fillRuntime, 1f);
        }
        EditorGUILayout.EndHorizontal();
    }

    private void ClearBlendRT(SC2TerrainRuntime runtime, float alpha)
    {
        var prev = RenderTexture.active;
        RenderTexture.active = runtime.BlendRT;
        GL.Clear(false, true, new Color(.5f, .5f, .5f, alpha));
        RenderTexture.active = prev;
        runtime.FlushActiveBlendGroup(Data);
        EditorUtility.SetDirty(Data);
    }

    /// <summary>Records the undo checkpoint for a layer edit on whichever surface (core or background) is being painted.</summary>
    private void RecordLayerCheckpoint(string label)
    {
        if (Data == _win.Data) _win.RecordRnmBlendCheckpoint();
        else _win.RecordUndoCheckpointFor(Data, label);
    }

    /// <summary>One undoable edit of the active mask group's layers: checkpoint, pending paint committed, the edit, then
    /// the render textures rebuilt (structure changed) or just re-flattened, and the composite written back to the pairs.</summary>
    private void RunLayerEdit(string label, System.Action<SC2BlendMaskLayerStack> edit, bool rebuild)
    {
        var runtime = _win.ActiveRuntime;
        var stack = runtime?.ActiveBlendStack;
        if (runtime == null || stack == null) return;
        RecordLayerCheckpoint(label);
        runtime.CommitLayerEdits();
        edit(stack);
        if (rebuild) runtime.RefreshLayersFromStack();
        else runtime.RecompositeBlend();
        FinishLayerEdit(runtime);
    }

    private void FinishLayerEdit(SC2TerrainRuntime runtime) => FinishLayerEdit(runtime, Data);

    private void FinishLayerEdit(SC2TerrainRuntime runtime, SC2TerrainData data, bool flushComposite = true)
    {
        runtime.CommitLayerEdits();
        // The flush is a full-size GPU readback; an outline update leaves it for later (see TryFlushPendingComposite).
        if (flushComposite) runtime.FlushActiveBlendGroup(data);
        else runtime.MarkCompositeUnflushed();
        _layerSettingsNeedFlush = false;
        _win.Session?.MarkDirty(SC2DirtyFlags.TerrainMaterials);
        EditorUtility.SetDirty(data);
        if (data != _win.Data) _win.RefreshBackgroundRuntime();
        _win.MarkDirtyAndRepaint(geometryChanged: false);
    }

    /// <summary>Writes pending composites to the pairs once painting has paused (see SC2TerrainRuntime.TryFlushPendingComposite).</summary>
    private void FlushPendingComposites()
    {
        if (_isPainting) return;
        PollOutlineJob();
        if (_win.Runtime != null && _win.Data != null) _win.Runtime.TryFlushPendingComposite(_win.Data, 0.6f);
        var background = _win.Data != null ? _win.Data.BackgroundTerrainData : null;
        if (_win.BackgroundRuntime != null && background != null) _win.BackgroundRuntime.TryFlushPendingComposite(background, 0.6f);
    }

    /// <summary>Id of the blend mask layer that holds every pair outline of a mask group.</summary>
    private const string OutlineLayerId = "pair-outline";

    private static SC2BlendMaskLayer FindOutlineLayer(SC2BlendMaskLayerStack stack) => stack.Layers.Find(l => l.Id == OutlineLayerId);

    /// <summary>The group's Outline layer, created (empty, on top of the stack) the first time an outline is drawn.</summary>
    private static SC2BlendMaskLayer GetOrCreateOutlineLayer(SC2BlendMaskLayerStack stack)
    {
        var layer = FindOutlineLayer(stack);
        if (layer != null) return layer;
        layer = new SC2BlendMaskLayer { Id = OutlineLayerId, Name = "Outline" };
        stack.Layers.Add(layer);
        return layer;
    }

    // The largest outline settings used so far this session. Outlines drawn earlier may be wider than the sliders say now, so
    // finding and judging the outline that is left behind uses these, not the current slider values.
    private static float s_widthUsed = 8f, s_spreadUsed = 4f, s_smoothingUsed = 1f;

    private static void NoteOutlineExtents(float widthWorld, float spreadWorld, float smoothingCells)
    {
        s_widthUsed = System.Math.Max(s_widthUsed, widthWorld);
        s_spreadUsed = System.Math.Max(s_spreadUsed, spreadWorld);
        s_smoothingUsed = System.Math.Max(s_smoothingUsed, smoothingCells);
    }

    private static bool HasAnyOutlineLayer(SC2TerrainData data)
    {
        foreach (var stack in data.BlendLayerStacks)
            if (stack != null && FindOutlineLayer(stack) != null) return true;
        return false;
    }

    /// <summary>The smallest outline Width and Spread (world units) and Smoothing (cells) that still show on this map's mask:
    /// see SC2PairOutlineUpdater.Minimum. Rounded up to the two decimals the sliders show.</summary>
    public void GetOutlineMinimums(SC2TerrainData data, out float width, out float spread, out float smoothing)
    {
        width = 0.5f; spread = 0f; smoothing = 0f;
        if (data == null || data.GridWidth < 1 || data.CellSize <= 0f) return;
        var runtime = data == _win.Data ? _win.Runtime : _win.BackgroundRuntime;
        var maskWidth = runtime != null && runtime.BlendRT != null ? runtime.BlendRT.width : 2048;
        var minimum = SC2PairOutlineUpdater.Minimum(new SC2PairOutlineUpdater.Geometry(maskWidth, maskWidth, data.GridWidth, data.GridHeight, data.CellSize));
        float Up(float v) => Mathf.Ceil(v * 100f) / 100f;
        width = Up(minimum.Width);
        spread = Up(minimum.Spread);
        smoothing = Up(minimum.Smoothing);
    }

    private SC2PairOutlineUpdater.Settings CurrentOutlineSettings() =>
        new SC2PairOutlineUpdater.Settings(_outlineWidth, _outlineSpread, _outlineSmoothing, _outlineSoftness, _outlineBlend);

    private static SC2PairOutlineUpdater.Settings UsedOutlineSettings() =>
        new SC2PairOutlineUpdater.Settings(s_widthUsed, s_spreadUsed, s_smoothingUsed, 0f, 1f);

    private static SC2PairOutlineUpdater.Geometry OutlineGeometry(SC2TerrainData data, SC2BlendMaskLayerStack stack) =>
        new SC2PairOutlineUpdater.Geometry(stack.Width, stack.Height, data.GridWidth, data.GridHeight, data.CellSize);

    /// <summary>Lets <paramref name="edit"/> change a private copy of the group's Outline layer (creating the layer when it does
    /// not exist yet) and, when it reports a change, stores the copy as one undo step. Only the Outline layer's texture is
    /// re-uploaded, and the composite is not read back into the pairs yet. Returns whether anything changed.</summary>
    private bool UpdateOutlineLayer(SC2TerrainData data, SC2TerrainRuntime runtime, SC2BlendMaskLayerStack stack, int pairIndex,
        System.Func<byte[], bool> edit)
    {
        runtime.CommitLayerEdits();
        var existing = FindOutlineLayer(stack);
        var length = stack.PixelCount * SC2BlendMaskLayerMath.BytesPerPixel;
        // A private copy: layer arrays are shared with undo snapshots and are never changed in place.
        var edited = existing != null && existing.Data != null && existing.Data.Length == length ? (byte[])existing.Data.Clone() : new byte[length];
        if (!edit(edited)) return false;

        _captureData = data;
        _captureRuntime = runtime;
        _capturePairIndex = System.Math.Max(0, pairIndex);
        try
        {
            if (data == _win.Data) _win.RecordRnmBlendCheckpoint();
            else _win.RecordUndoCheckpointFor(data, "Outline Pair Border");
        }
        finally
        {
            _captureData = null;
            _captureRuntime = null;
        }

        var created = existing == null;
        var layer = GetOrCreateOutlineLayer(stack);
        layer.Data = edited;
        if (created) runtime.RefreshLayersFromStack();   // a new layer needs its texture (and the others are rebuilt with it, once)
        else runtime.RefreshLayer(stack.Layers.IndexOf(layer));
        FinishLayerEdit(runtime, data, flushComposite: false);
        return true;
    }

    /// <summary>One mask group's share of an outline update: the layer it started from, and what the background thread made of it.</summary>
    private sealed class OutlineWork
    {
        public SC2BlendMaskLayerStack Stack;
        public byte[] Input;            // the Outline layer's data when the job started (null: no such layer yet)
        public int PairIndex;           // the pair to outline, or -1 for only removing what is stale
        public bool Draw;
        public SC2PairOutlineUpdater.Geometry Geometry;
        public byte[] Result;
        public bool Changed;
    }

    /// <summary>An outline update running on a background thread (the computation is pure): the heavy part of finishing a
    /// stroke does not block the editor. It is applied on the main thread by <see cref="PollOutlineJob"/> when done, or
    /// <see cref="FlushOutlineJob"/> waits for it before anything that reads or changes the layers.</summary>
    private sealed class OutlineJob
    {
        public SC2TerrainData Data;
        public SC2TerrainRuntime Runtime;
        public readonly List<OutlineWork> Works = new List<OutlineWork>();
        public Task Task;
        public long StartedMs;          // main-thread time spent starting it
        public long ThreadMs;           // time the thread took
    }

    private OutlineJob _outlineJob;

    /// <summary>A finished stroke waiting for its pair-map readback before its follow-up work (see CompleteStroke) can run.</summary>
    private sealed class StrokeRequest
    {
        public SC2TerrainData Data;
        public int PairIndex;
        public short[] Before;
    }

    private StrokeRequest _strokeRequest;

    /// <summary>Reads the GPU pair map back without stalling the GPU (a synchronous readback waits for all the painting still queued),
    /// then runs <see cref="CompleteStroke"/>.</summary>
    private void RequestStrokeCompletion(SC2TerrainData data, int pairIndex, short[] before)
    {
        FlushOutlineJob();
        var runtime = data == _win.Data ? _win.Runtime : _win.BackgroundRuntime;
        if (data == null || before == null || runtime == null || runtime.BlendRT == null) return;
        var request = new StrokeRequest { Data = data, PairIndex = pairIndex, Before = before };
        _strokeRequest = request;
        var hole = runtime.HoleTriPairIndex;
        var generation = ++_pairSyncGeneration;
        runtime.ReadPairIndexCPUAsync(bytes =>
        {
            // Something else (the next stroke, an undo, a save) already needed the request and ran it.
            if (_strokeRequest != request) return;
            // Another readback since this one was issued has brought the CPU copy up to date already.
            if (generation == _pairSyncGeneration)
            {
                var swCallback = System.Diagnostics.Stopwatch.StartNew();
                ApplyPairBytes(bytes, data, hole);
                _win.MarkPairCpuDataSynced();
                SC2Log.Log($"[SC2PaintMode] Pair map readback applied in {swCallback.ElapsedMilliseconds}ms.");
            }
            _strokeRequest = null;
            CompleteStroke(request);
        });
    }

    /// <summary>Follow-up of a finished paint stroke, once the CPU pair map is current: tells the height projection about it when the
    /// change involves a water pair (the only way a pair assignment moves it), and starts the outline update.</summary>
    private void CompleteStroke(StrokeRequest request)
    {
        var data = request.Data;
        var waterInvolved = WaterPairChanged(data, request.Before);
        _win.MarkDirtyAndRepaint(geometryChanged: waterInvolved);
        if (waterInvolved) _win.InvalidateLiveHeightGrid();
        OutlineNewlyPaintedCells(data, request.PairIndex, request.Before, syncFirst: false);
    }

    /// <summary>Whether a cell that changed between <paramref name="before"/> and the current assignments was, or is now, of a water
    /// pair (SC2HfieldProjectionService.IsWaterMaterial applied to the pair): the height projection leaves those out.</summary>
    private static bool WaterPairChanged(SC2TerrainData data, short[] before)
    {
        var after = data.TrianglePairIndex;
        if (after == null || before == null || after.Length != before.Length) return true;   // unknown: assume it did
        var water = new bool[data.Pairs.Count];
        var anyWater = false;
        for (var i = 0; i < water.Length; i++)
        {
            var pair = data.Pairs[i];
            water[i] = (pair.ShaderFile?.IndexOf("water", System.StringComparison.OrdinalIgnoreCase) ?? -1) >= 0
                || (pair.Name?.IndexOf("water", System.StringComparison.OrdinalIgnoreCase) ?? -1) >= 0;
            anyWater |= water[i];
        }
        if (!anyWater) return false;
        for (var i = 0; i < after.Length; i += 2)
        {
            if (after[i] == before[i]) continue;
            if ((before[i] >= 0 && before[i] < water.Length && water[before[i]]) || (after[i] >= 0 && after[i] < water.Length && water[after[i]]))
                return true;
        }
        return false;
    }

    /// <summary>Applies the running outline update (waiting for it to finish) so the layers are up to date. Cheap when there is none.</summary>
    public void FlushOutlineJob()
    {
        if (_strokeRequest != null)
        {
            // The stroke's readback has not come back yet: do it now, synchronously, and finish the stroke.
            var request = _strokeRequest;
            _strokeRequest = null;
            var runtime = request.Data == _win.Data ? _win.Runtime : _win.BackgroundRuntime;
            if (runtime != null && runtime.BlendRT != null) SyncPairMapToCPU(runtime, request.Data);
            CompleteStroke(request);
        }
        if (_outlineJob == null) return;
        try { _outlineJob.Task.Wait(); } catch (System.AggregateException) { /* reported in ApplyOutlineJob */ }
        ApplyOutlineJob();
    }

    /// <summary>Applies the outline update once the background thread has finished; asks for another frame while it runs.</summary>
    private void PollOutlineJob()
    {
        if (_outlineJob == null || _isPainting) return;
        if (_outlineJob.Task.IsCompleted) ApplyOutlineJob();
        else _win.Repaint();
    }

    private void ApplyOutlineJob()
    {
        var job = _outlineJob;
        _outlineJob = null;
        if (job == null) return;
        if (job.Task.IsFaulted)
        {
            SC2Log.Log($"[SC2PaintMode] Outline update failed: {job.Task.Exception?.GetBaseException().Message}");
            return;
        }
        var data = job.Data;
        var runtime = job.Runtime;
        if (data == null || runtime == null || runtime.BlendRT == null) return;

        var swApply = System.Diagnostics.Stopwatch.StartNew();
        var changedAny = false;
        foreach (var work in job.Works)
        {
            if (!work.Changed || !data.BlendLayerStacks.Contains(work.Stack)) continue;
            runtime.SetActiveRnmMaskGroup(work.Stack.MaskName, data);
            var stack = runtime.ActiveBlendStack;
            if (stack != work.Stack || !runtime.HasActiveLayers) continue;
            // The layer was edited by something else meanwhile: this result no longer fits it.
            var existing = FindOutlineLayer(stack);
            if ((existing != null ? existing.Data : null) != work.Input) continue;
            var result = work.Result;
            changedAny |= UpdateOutlineLayer(data, runtime, stack, work.PairIndex, layer => { System.Array.Copy(result, layer, result.Length); return true; });
        }
        _outlineStatus = changedAny ? "Outline layer updated." : "";
        SC2Log.Log($"[SC2PaintMode] Outline update: start on the main thread {job.StartedMs}ms, computed on the thread in {job.ThreadMs}ms, applied on the main thread in {swApply.ElapsedMilliseconds}ms.");
    }

    /// <summary>After an assignment: outlines the closed region(s) of <paramref name="pairIndex"/> that the cells changed since
    /// <paramref name="before"/> belong to afresh (the region's previous outline is removed first) and clears every outline that
    /// is no longer along a pair border (see SC2PairOutlineUpdater). The outlines of all pairs live on one layer per mask group,
    /// "Outline".
    /// <paramref name="pairIndex"/> below 0 only removes stale outlines. <paramref name="syncFirst"/> reads the pair map back
    /// from the GPU first (needed right after a brush stroke).
    ///
    /// The computation runs on a background thread; the result is applied a moment later (see OutlineJob).</summary>
    private void OutlineNewlyPaintedCells(SC2TerrainData data, int pairIndex, short[] before, bool syncFirst)
    {
        FlushOutlineJob();
        var swStart = System.Diagnostics.Stopwatch.StartNew();
        if (data == null || before == null) return;
        // Without Auto outline nothing is drawn, but outlines that stopped being borders are still removed.
        var draw = _autoOutline;
        if (!draw && !HasAnyOutlineLayer(data)) return;
        var runtime = data == _win.Data ? _win.Runtime : _win.BackgroundRuntime;
        if (runtime == null || runtime.BlendRT == null || pairIndex >= data.Pairs.Count) return;
        if (syncFirst) SyncPairMapToCPU(runtime, data);
        if (data.TrianglePairIndex == null || data.TrianglePairIndex.Length != before.Length) return;
        var after = (short[])data.TrianglePairIndex.Clone();   // the thread must not see later changes

        NoteOutlineExtents(_outlineWidth, _outlineSpread, _outlineSmoothing);
        var current = CurrentOutlineSettings();
        var used = UsedOutlineSettings();
        var job = new OutlineJob { Data = data, Runtime = runtime };
        string handledMask = null;
        if (draw && pairIndex >= 0)
        {
            runtime.SetActiveRnmMaskGroup(data.Pairs[pairIndex].RnmMaskName, data);
            var stack = runtime.ActiveBlendStack;
            if (stack != null && runtime.HasActiveLayers)
            {
                handledMask = stack.MaskName;
                job.Works.Add(new OutlineWork { Stack = stack, Input = FindOutlineLayer(stack)?.Data, PairIndex = pairIndex, Draw = true, Geometry = OutlineGeometry(data, stack) });
            }
        }
        // Every other mask group that has an Outline layer loses the outlines that are no longer along a border.
        foreach (var other in data.BlendLayerStacks)
        {
            var outline = other != null ? FindOutlineLayer(other) : null;
            if (outline == null || other.MaskName == handledMask) continue;
            job.Works.Add(new OutlineWork { Stack = other, Input = outline.Data, PairIndex = -1, Draw = draw, Geometry = OutlineGeometry(data, other) });
        }
        if (job.Works.Count == 0) return;

        job.Task = Task.Run(() =>
        {
            var swThread = System.Diagnostics.Stopwatch.StartNew();
            foreach (var work in job.Works)
            {
                var length = work.Geometry.MaskWidth * work.Geometry.MaskHeight * SC2BlendMaskLayerMath.BytesPerPixel;
                var edited = work.Input != null && work.Input.Length == length ? (byte[])work.Input.Clone() : new byte[length];
                work.Changed = SC2PairOutlineUpdater.Update(edited, work.Geometry, before, after, work.PairIndex, work.Draw, current, used);
                work.Result = edited;
            }
            job.ThreadMs = swThread.ElapsedMilliseconds;
        });
        job.StartedMs = swStart.ElapsedMilliseconds;
        _outlineJob = job;
        _win.MarkDirtyAndRepaint(geometryChanged: false);
    }

    /// <summary>Removes the outlines that are no longer along a pair border after the assignments changed from
    /// <paramref name="before"/> (same cell layout as TrianglePairIndex) to the current ones.</summary>
    public void PruneOutlines(SC2TerrainData data, short[] before)
        => OutlineNewlyPaintedCells(data, -1, before, syncFirst: false);

    /// <summary>Paints a band of the pair's second material (blend alpha <paramref name="blendValue"/>) along the border of pair
    /// <paramref name="pairIndex"/> of <paramref name="data"/>, inside the pair and (<paramref name="spreadWorld"/>) onto its
    /// neighbours, into the "Outline" layer of the pair's mask group (the pair's own surface, core or background), and clears the
    /// outlines on that layer that are no longer along a pair border. One undo step. Returns a one-line result for the panel.</summary>
    public string OutlinePairBorder(SC2TerrainData data, int pairIndex, float widthWorld, float softness, float blendValue, float smoothingCells = 1f, float spreadWorld = 0f)
    {
        FlushOutlineJob();
        var runtime = data == _win.Data ? _win.Runtime : _win.BackgroundRuntime;
        if (data == null || runtime == null || runtime.BlendRT == null || pairIndex < 0 || pairIndex >= data.Pairs.Count)
            return "Outline not applied: the terrain is not ready.";

        NoteOutlineExtents(widthWorld, spreadWorld, smoothingCells);
        // The assignments on screen live on the GPU; bring the CPU copy up to date before reading it.
        SyncPairMapToCPU(runtime, data);
        var pair = data.Pairs[pairIndex];
        runtime.SetActiveRnmMaskGroup(pair.RnmMaskName, data);
        var stack = runtime.ActiveBlendStack;
        if (stack == null || !runtime.HasActiveLayers) return "Outline not applied: the blend mask has no layers yet.";

        var assignments = data.TrianglePairIndex;
        var any = false;
        for (var i = 0; i < assignments.Length && !any; i++) any = assignments[i] == pairIndex;
        if (!any) return $"Outline not applied: '{pair.Name}' has no triangles assigned.";

        // The whole pair is outlined, so the work area is the whole mask and every border counts as unchanged.
        var geometry = OutlineGeometry(data, stack);
        var current = new SC2PairOutlineUpdater.Settings(widthWorld, spreadWorld, smoothingCells, softness, blendValue);
        var support = SC2PairOutlineUpdater.SupportReachPixels(geometry, s_widthUsed, s_spreadUsed, s_smoothingUsed);
        var ring = new List<int>();
        for (var cell = 0; cell * 2 < assignments.Length; cell++) if (assignments[cell * 2] == pairIndex) ring.Add(cell);
        UpdateOutlineLayer(data, runtime, stack, pairIndex,
            layer => SC2PairOutlineUpdater.UpdateIn(layer, geometry, SC2PairOutlineUpdater.Work.Whole(geometry), null, assignments, pairIndex, ring, null, current, support));
        return $"Outlined '{pair.Name}' on layer 'Outline'.";
    }

    private void RunPendingLayerActions()
    {
        if (_pendingLayerActions.Count > 0) FlushOutlineJob();
        while (_pendingLayerActions.Count > 0) _pendingLayerActions.Dequeue().Invoke();
        // An opacity drag re-flattens on the GPU every change but only writes the composite back to the pairs once the
        // value has stopped changing (the readback is a full 2048x2048 copy).
        if (_layerSettingsNeedFlush && EditorApplication.timeSinceStartup - _layerSettingsChangedAt > 0.35 && _win.ActiveRuntime != null)
            FinishLayerEdit(_win.ActiveRuntime);
    }

    /// <summary>The layers list of the active mask group, top layer first: visibility, select (click the name),
    /// move up/down, duplicate, delete; below it the selected layer's name and opacity, New layer and Merge down.</summary>
    private void DrawLayerPanel(float w)
    {
        var runtime = _win.ActiveRuntime;
        if (runtime == null || !runtime.HasActiveLayers) return;
        var stack = runtime.ActiveBlendStack;

        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("Layers", EditorStyles.miniLabel);
        var smallButton = Mathf.Max(24f, SC2UiScale.Pxf(26f));
        var rowHeight = SC2UiScale.Pxf(20f);
        for (var i = stack.Layers.Count - 1; i >= 0; i--)
        {
            var index = i;
            var layer = stack.Layers[i];
            EditorGUILayout.BeginHorizontal(GUILayout.Width(w), GUILayout.MaxWidth(w));
            // A button, not a bare checkbox: it names what a click does (Hide / Show) and a hidden layer's name is greyed.
            var wasVisible = layer.Visible;
            if (GUILayout.Button(wasVisible ? "Hide" : "Show", GUILayout.Width(SC2UiScale.Pxf(40f)), GUILayout.Height(rowHeight)))
                _pendingLayerActions.Enqueue(() => RunLayerEdit(wasVisible ? "Hide Blend Layer" : "Show Blend Layer", st => st.Layers[index].Visible = !wasVisible, rebuild: false));
            var selected = index == stack.ActiveIndex;
            var previousColor = GUI.backgroundColor;
            var previousContent = GUI.contentColor;
            if (selected) GUI.backgroundColor = new Color(0.45f, 0.7f, 1f, 1f);
            if (!wasVisible) GUI.contentColor = new Color(1f, 1f, 1f, 0.45f);
            if (GUILayout.Button(layer.Name, GUILayout.Height(rowHeight), GUILayout.ExpandWidth(true)) && !selected)
                _pendingLayerActions.Enqueue(() => { runtime.SelectBlendLayer(index); _win.MarkDirtyAndRepaint(geometryChanged: false); });
            GUI.backgroundColor = previousColor;
            GUI.contentColor = previousContent;
            if (GUILayout.Button("Up", GUILayout.Width(smallButton), GUILayout.Height(rowHeight)) && index < stack.Layers.Count - 1)
                _pendingLayerActions.Enqueue(() => RunLayerEdit("Move Blend Layer", st => st.MoveLayer(index, 1), rebuild: true));
            if (GUILayout.Button("Dn", GUILayout.Width(smallButton), GUILayout.Height(rowHeight)) && index > 0)
                _pendingLayerActions.Enqueue(() => RunLayerEdit("Move Blend Layer", st => st.MoveLayer(index, -1), rebuild: true));
            if (GUILayout.Button("Dup", GUILayout.Width(smallButton + 6f), GUILayout.Height(rowHeight)))
                _pendingLayerActions.Enqueue(() => RunLayerEdit("Duplicate Blend Layer", st => st.DuplicateLayer(index), rebuild: true));
            if (GUILayout.Button("X", GUILayout.Width(smallButton - 4f), GUILayout.Height(rowHeight)) && stack.Layers.Count > 1)
                _pendingLayerActions.Enqueue(() => RunLayerEdit("Delete Blend Layer", st => st.RemoveLayer(index), rebuild: true));
            EditorGUILayout.EndHorizontal();
        }

        var active = stack.Active;
        if (active != null)
        {
            var newName = EditorGUILayout.TextField("Name", active.Name);
            if (newName != active.Name)
            {
                // Not an undo step of its own (a step per keystroke would flood the undo stack); it is written back to
                // the document with the next flush.
                active.Name = newName;
                _layerSettingsChangedAt = EditorApplication.timeSinceStartup;
                _layerSettingsNeedFlush = true;
                EditorUtility.SetDirty(Data);
            }
            var opacity = SC2TerrainShell.DrawCompactSlider("Opacity", active.Opacity, 0f, 1f, active.Id,
                onValueChangeStart: () => RecordLayerCheckpoint("Change Blend Layer Opacity"));
            if (!Mathf.Approximately(opacity, active.Opacity))
            {
                active.Opacity = opacity;
                runtime.RecompositeBlend();
                _layerSettingsChangedAt = EditorApplication.timeSinceStartup;
                _layerSettingsNeedFlush = true;
                _win.MarkDirtyAndRepaint(geometryChanged: false);
            }
        }

        EditorGUILayout.BeginHorizontal(GUILayout.Width(w), GUILayout.MaxWidth(w));
        var half = Mathf.Max(48f, (w - 4f) * 0.5f);
        if (GUILayout.Button("+ New layer", GUILayout.Height(SC2UiScale.Pxf(22f)), GUILayout.Width(half)))
            _pendingLayerActions.Enqueue(() => RunLayerEdit("Add Blend Layer", st => st.AddLayerAboveActive(), rebuild: true));
        if (GUILayout.Button("Merge down", GUILayout.Height(SC2UiScale.Pxf(22f)), GUILayout.Width(half)) && stack.ActiveIndex > 0)
            _pendingLayerActions.Enqueue(() => RunLayerEdit("Merge Blend Layer", st => st.MergeDown(st.ActiveIndex), rebuild: true));
        EditorGUILayout.EndHorizontal();
    }

    /// <summary>Editable path + thumbnail for Data.CustomBrushTexturePath —
    /// same shared widget/pattern SC2InfoModelFieldDrawer.DrawInfoTextureField
    /// and the Materials/Imported-terrain texture-path fields already use
    /// this session. SC2PaintBrush.shader samples luminance*alpha (see
    /// PaintMode==2's custom-brush branch) — a plain grayscale mask (no real
    /// alpha channel, the common World Machine/Gaea stamp-export convention)
    /// and a real alpha-transparency brush image both work correctly. Always
    /// the CORE's own _win.Data — see SC2GeometryMode's own header
    /// comment.</summary>
    private void DrawCustomBrushPicker()
    {
        var currentPath = _win.Data.CustomBrushTexturePath ?? "";
        if (currentPath != _customBrushCachedPath)
        {
            _customBrushCache = SC2BrushLibrary.Resolve(currentPath);
            _customBrushCachedPath = currentPath;
        }
        var pathDraft = _customBrushCachedPath;
        // The built-in brushes: one click sets the path and the texture.
        var preset = SC2BrushLibrary.DrawPresetGrid(pathDraft, SC2TerrainShell.GetPanelContentWidth());
        if (preset != null) { pathDraft = preset; _customBrushCache = SC2BrushLibrary.Load(preset); }
        var picked = SC2TerrainShell.DrawTexturePickerWithPath("Brush Image", _customBrushCache, "PaintBlend:CustomBrush", ref pathDraft, out var pickedViaBrowser);
        if (picked != _customBrushCache)
        {
            _customBrushCache = picked;
            if (pickedViaBrowser)
                pathDraft = picked != null ? (SC2TerrainMaterialFactory.TryGetResourcePath(picked) ?? "") : "";
        }
        if (pathDraft != (_win.Data.CustomBrushTexturePath ?? ""))
        {
            _win.RecordUndoCheckpoint("Change Custom Brush Texture");
            _win.Data.CustomBrushTexturePath = pathDraft;
            _customBrushCachedPath = pathDraft;
            EditorUtility.SetDirty(_win.Data);
        }
    }

    private static Vector3 DrawVector3Row(string label, Vector3 value, float width)
    {
        EditorGUILayout.LabelField(label, EditorStyles.miniLabel, GUILayout.Width(width), GUILayout.MaxWidth(width));
        var fieldWidth = Mathf.Max(42f, (width - 8f) / 3f);
        EditorGUILayout.BeginHorizontal(GUILayout.Width(width), GUILayout.MaxWidth(width));
        value.x = EditorGUILayout.FloatField("X", value.x, GUILayout.Width(fieldWidth));
        value.y = EditorGUILayout.FloatField("Y", value.y, GUILayout.Width(fieldWidth));
        value.z = EditorGUILayout.FloatField("Z", value.z, GUILayout.Width(fieldWidth));
        EditorGUILayout.EndHorizontal();
        return value;
    }

    // ── SceneView ──────────────────────────────────────────────

#if UNITY_EDITOR
    public void OnSceneGUI(SceneView sv)
    {
        FlushPendingComposites();
        HandleScroll(Event.current);
        RaycastTerrain();
        DrawGizmo();
        switch (_sub)
        {
            case Sub.Assign: HandleAssign(Event.current); break;
            case Sub.RNM:    HandleRnm(Event.current);    break;
        }
    }
#endif

    public void OnGameViewGUI(Camera camera, Rect viewportRect, Vector2 mousePosition)
    {
        var e = Event.current;
        if (e == null || camera == null)
            return;
        FlushPendingComposites();
        if (!viewportRect.Contains(mousePosition) && !_isPainting)
            return;

        HandleScroll(e);
        RaycastTerrain(camera, mousePosition, viewportRect);
        // Checked BEFORE DrawGameViewGizmo/the sub-mode dispatch (not
        // after) so an already-active resize draws its own preview circle
        // and keeps responding to mouse movement/Escape/click-to-confirm
        // instead of this tool's own brush cursor/paint input firing
        // underneath it.
        if (_radiusGesture.HandleInput(camera, viewportRect, mousePosition, _brushVis, _brushPos, true, ref _radius, 1f, 1000f,
            sampleHeight: SampleActiveHeight))
            return;
        DrawGameViewGizmo(camera, viewportRect);
        switch (_sub)
        {
            case Sub.Assign: HandleAssign(e); break;
            case Sub.RNM:    HandleRnm(e);    break;
        }
    }

    /// <summary>See SC2GeometryMode.SampleActiveHeight's own doc comment —
    /// identical pattern.</summary>
    private float SampleActiveHeight(float x, float z) =>
        _win.ActiveTerrainData == _win.Data
            ? SC2ObjectsExport.SampleTerrainHeight(_win.Data, x, z)
            : _win.SampleLiveBackgroundHeight(x, z);

    void HandleScroll(Event e)
    {
        if (!e.isScrollWheel) return;
        // Radius has its own dedicated F-hold resize gesture (SC2BrushRadiusGesture)
        // — no scroll modifier for it any more, that was a straight duplicate.
        // Some mice/trackpads report an occasional much-larger-than-usual
        // delta for a single tick, which made Strength jump hard instead of
        // stepping smoothly — clamped to a single tick's worth. Windows also
        // reports Shift+wheel as a horizontal-scroll message, delta.y coming
        // back 0 with the tick magnitude in delta.x instead; the fallback
        // below covers that for any modifier, not just Shift.
        var delta = Mathf.Clamp(e.delta.y != 0f ? e.delta.y : e.delta.x, -3f, 3f);
        if (SC2Hotkeys.ModifierHeld("Brush.StrengthScrollModifier", e)) { _strength = Mathf.Clamp01(_strength - delta * .01f); e.Use(); _win.Repaint(); }
    }

    void RaycastTerrain()
    {
        _brushVis = false;
        var ray = HandleUtility.GUIPointToWorldRay(Event.current.mousePosition);
        var offset = _win.ActiveWorldOffset;
        var localRay = new Ray(ray.origin - offset, ray.direction);
        if (SC2TerrainShell.RaycastTerrainHeight(_win.ActiveRuntime, localRay, out var hit))
        {
            _brushPos = hit + offset;
            _brushVis = true;
            return;
        }

        if (ray.direction.y < -1e-5f)
        { _brushPos = ray.GetPoint((_brushPos.y - ray.origin.y) / ray.direction.y); _brushVis = true; }
        if (!_brushVis && ray.direction.y < -1e-10f)
        { _brushPos = ray.GetPoint(-ray.origin.y / ray.direction.y); _brushVis = true; }
    }

    void RaycastTerrain(Camera camera, Vector2 guiMousePosition, Rect viewportRect)
    {
        _brushVis = false;
        var ray = SC2TerrainShell.GameGuiPointToRay(camera, viewportRect, guiMousePosition);
        var offset = _win.ActiveWorldOffset;
        var localRay = new Ray(ray.origin - offset, ray.direction);
        if (SC2TerrainShell.RaycastTerrainHeight(_win.ActiveRuntime, localRay, out var hit))
        {
            _brushPos = hit + offset;
            _brushVis = true;
            return;
        }

        if (ray.direction.y < -1e-5f)
        {
            _brushPos = ray.GetPoint((_brushPos.y - ray.origin.y) / ray.direction.y);
            _brushVis = true;
        }
        if (!_brushVis && ray.direction.y < -1e-10f)
        {
            _brushPos = ray.GetPoint(-ray.origin.y / ray.direction.y);
            _brushVis = true;
        }
    }

    void DrawGizmo()
    {
        if (!_brushVis) return;
        var col = _sub == Sub.Assign ? new Color(1f,.5f,0f,.7f) : new Color(0f,.8f,1f,.7f);
        Handles.color = col;
        Handles.DrawWireDisc(_brushPos, Vector3.up, _radius);
        // Symmetry always follows the CORE's own setting — see
        // SC2GeometryMode's own header comment.
        if (_win.Data.Symmetry != SymmetryMode.None)
        {
            Handles.color = new Color(.4f,.8f,1f,.5f);
            var syms = _win.Data.GetSymmetricPositions(_brushPos);
            for (var i = 1; i < syms.Count; i++)
                Handles.DrawWireDisc(syms[i], Vector3.up, _radius);
        }
    }

    void DrawGameViewGizmo(Camera camera, Rect viewportRect)
    {
        if (!_brushVis) return;
        Color col;
        if (_sub == Sub.Assign) col = new Color(1f, .55f, 0f, .95f);
        // Held-Ctrl erase preview (see HandleRnm) — same "cursor color is
        // the erase-active feedback" treatment SC2CostsShell's brush cursor
        // already uses.
        else if (_rnmErase || (Event.current != null && Event.current.control)) col = new Color(1f, .3f, .25f, .95f);
        else col = new Color(0f, .85f, 1f, .95f);
        SC2TerrainShell.DrawGameViewBrush(camera, viewportRect, _brushPos, _radius, col, Color.clear,
            sampleHeight: SampleActiveHeight);

        // Symmetry always follows the CORE's own setting — see
        // SC2GeometryMode's own header comment.
        if (_win.Data.Symmetry != SymmetryMode.None)
            SC2TerrainShell.DrawSymmetryPointMarkers(camera, viewportRect, _brushPos, _win.Data.GetSymmetricPositions(_brushPos),
                new Color(0.4f, 0.8f, 1f, 0.9f),
                sampleHeight: SampleActiveHeight);
    }

    void HandleAssign(Event e)
    {
        if (!_brushVis || _win.ActiveRuntime == null) return;
        var offset = _win.ActiveWorldOffset;

        // Blender/Photoshop-style eyedropper — Alt+click samples
        // the pair under the cursor into _assignPair instead of painting
        // with it. Alt already fully excludes the down/drag checks below
        // from ever firing (see !e.alt in both), so this never conflicts
        // with painting or Select mode. GPU paint is authoritative for
        // TrianglePairIndex mid-stroke (see the down branch's own comment
        // below) — sync CPU first so a click right after painting samples
        // the just-painted pair, not a stale pre-stroke value.
        if (e.type == EventType.MouseDown && e.button == 0 && e.alt)
        {
            SyncPairMapToCPU();
            var cell = Mathf.Max(0.0001f, Data.CellSize);
            var localX = _brushPos.x - offset.x;
            var localZ = _brushPos.z - offset.z;
            var gx = Mathf.Clamp(Mathf.FloorToInt(localX / cell), 0, Data.GridWidth - 1);
            var gz = Mathf.Clamp(Mathf.FloorToInt(localZ / cell), 0, Data.GridHeight - 1);
            var ti = (gz * Data.GridWidth + gx) * 2;
            if (Data.TrianglePairIndex != null && ti >= 0 && ti < Data.TrianglePairIndex.Length)
            {
                var sampled = Data.TrianglePairIndex[ti];
                if (sampled >= 0 && sampled < Data.Pairs.Count)
                {
                    _assignPair = sampled;
                    _win.MarkDirtyAndRepaint();
                }
            }
            e.Use();
            return;
        }

        var down = e.type == EventType.MouseDown && e.button == 0 && !e.alt;
        var drag = e.type == EventType.MouseDrag  && e.button == 0 && !e.alt;
        if (e.type == EventType.MouseUp)
        {
            // Same real bug as SC2GeometryMode's own matching MouseUp fix:
            // everything below used to run on EVERY MouseUp — including a
            // middle/right-mouse camera orbit/pan/fly release, since
            // SC2GameViewEditorUI's own navigation MouseUp handler never
            // calls e.Use() — not just a real completed stroke. _isPainting
            // is only ever set true from a real left-click-no-alt
            // MouseDown, never a camera drag, so bailing out here when it's
            // false is the correct gate for the WHOLE block, not just the
            // dirty-tracking calls further down.
            if (!_isPainting) return;
            var swMouseUp = System.Diagnostics.Stopwatch.StartNew();
            // Real Smart Export dirty-
            // tracking for terrain materials — see SC2GeometryMode's own
            // matching comment for the exact Costs/Hfield/Collision2 gap
            // this closes. Only when !_selectMode: this same
            // MouseUp also fires after a pure Select-mode drag (no PaintPair
            // call anywhere in that path — see the down/(!_selectMode) guard
            // below and the select-vs-paint branch further down), which
            // changes zero exported data.
            if (!_selectMode)
            {
                _win.Session?.MarkDirty(SC2DirtyFlags.TerrainMaterials);
                // Repainting which pair/material covers a
                // triangle can move it in or out of the "Water" exclusion
                // SC2HfieldProjectionService.Project applies (e.g. painting
                // a Water-shaded pair over previously-dry ground, or vice
                // versa) — same staleness class as SC2GeometryMode's own
                // matching fix, just for the material/pair axis instead of
                // raw height. Decided once the stroke's cells are known (CompleteStroke): only a change that involves a water pair
                // can move the projection.
            }
            var swAfterDirty = swMouseUp.ElapsedMilliseconds;
            _isPainting = false;
            // Non-blocking readback — async GPU request, no SceneView hang. A paint stroke has its own (see CompleteStroke).
            var swSync = System.Diagnostics.Stopwatch.StartNew();
            var strokeBefore = _strokeBefore;
            if (strokeBefore == null)
            {
                var syncNow = EditorApplication.timeSinceStartup;
                if (syncNow - _lastSyncTime >= kSyncThrottle)
                {
                    _lastSyncTime = syncNow;
                    SyncPairMapToCPUAsync();
                }
            }
            var swSyncMs = swSync.ElapsedMilliseconds;

            // The stroke is finished: once the pair map has been read back from the GPU (without stalling it) the water check,
            // the outline and the refresh follow.
            var swOutline = System.Diagnostics.Stopwatch.StartNew();
            if (!_selectMode && strokeBefore != null)
                RequestStrokeCompletion(Data, _assignPair, strokeBefore);
            _strokeBefore = null;
            var swOutlineMs = swOutline.ElapsedMilliseconds;

            var swMarkDirty = System.Diagnostics.Stopwatch.StartNew();
            _win.MarkDirtyAndRepaint(geometryChanged: false);
            var swMarkDirtyMs = swMarkDirty.ElapsedMilliseconds;

            // Same as SC2GeometryMode's own MouseUp - refreshes the background
            // after a completed Paint Pair stroke on it;
            // no-op when the active surface is the core. Logs its own
            // internal breakdown separately (see its own doc comment).
            if (Data != _win.Data) _win.RefreshBackgroundRuntime();
            SC2Log.Log($"[SC2PaintMode] HandleAssign MouseUp: MarkDirty+InvalidateLiveHeightGrid={swAfterDirty}ms, SyncPairMapToCPUAsync={swSyncMs}ms, MarkDirtyAndRepaint={swMarkDirtyMs}ms, outlineRequest={swOutlineMs}ms, total={swMouseUp.ElapsedMilliseconds}ms");
            return;
        }
        if (down)
        {
            _isPainting = true;
            FlushOutlineJob();
            if (!_selectMode)
            {
                // GPU paint is authoritative for TrianglePairIndex during a stroke;
                // sync it to CPU before snapshotting so undo captures the true
                // pre-stroke state (not a stale value from the last throttled
                // async readback).
                var swDown = System.Diagnostics.Stopwatch.StartNew();
                SyncPairMapToCPU();
                var swDownSync = swDown.ElapsedMilliseconds;
                _win.RecordUndoCheckpointFor(Data, "Paint Pair");
                SC2Log.Log($"[SC2PaintMode] HandleAssign MouseDown: SyncPairMapToCPU={swDownSync}ms, RecordUndoCheckpointFor={swDown.ElapsedMilliseconds - swDownSync}ms");
                _strokeBefore = Data.TrianglePairIndex != null ? (short[])Data.TrianglePairIndex.Clone() : null;
            }
        }
        if (!_isPainting || (!down && !drag)) return;
        var now = EditorApplication.timeSinceStartup;
        if (!down && now - _lastApply < kInterval) return;
        _lastApply = now;

        // Symmetry always follows the CORE's own setting — see
        // SC2GeometryMode's own header comment.
        if (_selectMode)
        {
            // Select mode uses CPU spatial query (only active for small grids)
            foreach (var worldPos in _win.Data.GetSymmetricPositions(_brushPos))
            {
                var pos = worldPos - offset;
                var cell = Data.CellSize; var r2 = _radius * _radius;
                var x0 = Mathf.Max(0, Mathf.FloorToInt((pos.x-_radius)/cell));
                var x1 = Mathf.Min(Data.GridWidth-1, Mathf.CeilToInt((pos.x+_radius)/cell));
                var z0 = Mathf.Max(0, Mathf.FloorToInt((pos.z-_radius)/cell));
                var z1 = Mathf.Min(Data.GridHeight-1, Mathf.CeilToInt((pos.z+_radius)/cell));
                for (var gz=z0; gz<=z1; gz++) for (var gx=x0; gx<=x1; gx++)
                {
                    var cx=gx*cell+cell*.5f; var cz=gz*cell+cell*.5f;
                    var dx=cx-pos.x; var dz=cz-pos.z;
                    if (dx*dx+dz*dz <= r2)
                    { var ti=(gz*Data.GridWidth+gx)*2; _sel.Add(ti); _sel.Add(ti+1); }
                }
            }
        }
        else
        {
            // GPU paint — single blit per symmetric position
            _win.ActiveRuntime.SetBrushPairLock(LockTableOrNull(_assignLock));
            foreach (var worldPos in _win.Data.GetSymmetricPositions(_brushPos))
                _win.ActiveRuntime.PaintPair(worldPos - offset, _radius, _strength, _assignPair);
            _win.ActiveRuntime.SetBrushPairLock(null);
            _win.MarkPairCpuDataStale();
            // Immediately update PairColor RT for real-time preview — same
            // call SC2TerrainShell.RepaintPairColorRTPublic makes for the
            // core (_win.Runtime.ApplyPairColourView(_win.Data, false)),
            // generalized to whichever surface is active.
            _win.ActiveRuntime.ApplyPairColourView(Data, false);
#if UNITY_EDITOR
            SceneView.RepaintAll();
#endif
        }
        e.Use();
    }

    void HandleRnm(Event e)
    {
        if (!_brushVis || _win.ActiveRuntime == null) return;
        var offset = _win.ActiveWorldOffset;
        var down = e.type == EventType.MouseDown && e.button == 0 && !e.alt;
        var drag = e.type == EventType.MouseDrag  && e.button == 0 && !e.alt;
        if (e.type == EventType.MouseUp)
        {
            // Same real bug/fix as HandleAssign's matching MouseUp above —
            // this whole block used to run on every MouseUp, including a
            // camera orbit/pan/fly release (SC2GameViewEditorUI's own
            // navigation MouseUp handler never calls e.Use()). No
            // _selectMode concept here, every completed Blend stroke is a
            // real paint, so this gate alone is enough.
            if (!_isPainting) return;
            // Ad-hoc timing instrumentation (SC2Log) — see SC2GeometryMode's
            // own matching MouseUp comment. Logs.../SC2TerrainEditor.log.
            var swMouseUp = System.Diagnostics.Stopwatch.StartNew();
            _win.Session?.MarkDirty(SC2DirtyFlags.TerrainMaterials);
            _isPainting = false;

            var swFlush = System.Diagnostics.Stopwatch.StartNew();
            if (_win.ActiveRuntime != null)
                _win.ActiveRuntime.FlushActiveBlendGroup(Data);
            var swFlushMs = swFlush.ElapsedMilliseconds;

            var swSetDirty = System.Diagnostics.Stopwatch.StartNew();
            EditorUtility.SetDirty(Data);
            var swSetDirtyMs = swSetDirty.ElapsedMilliseconds;

            // Same as SC2GeometryMode's own MouseUp - refreshes the background
            // after a completed Paint Blend stroke on it
            // (SC2TerrainRuntime.SharedChunkMat samples BlendRT directly);
            // no-op when active surface is core.
            // Logs its own internal breakdown separately (see its own doc
            // comment).
            if (Data != _win.Data) _win.RefreshBackgroundRuntime();
            _win.Repaint();
            SC2Log.Log($"[SC2PaintMode] HandleRnm MouseUp: FlushActiveBlendGroup={swFlushMs}ms, EditorUtility.SetDirty={swSetDirtyMs}ms, total={swMouseUp.ElapsedMilliseconds}ms");
            return;
        }
        if (down)
        {
            FlushOutlineJob();
            _isPainting = true;
            _stampStroke.End();
            _win.ActiveRuntime?.BeginBlendStroke();
            // Roll a fresh stamp rotation for this stroke only —
            // see _rnmBrushRotationRad's own doc comment.
            _rnmBrushRotationRad = (_win.Data.UseCustomBrush && _win.Data.RandomizeBrushRotation)
                ? Random.Range(0f, Mathf.PI * 2f) : 0f;
            // Ensure correct pair's blend RT is active before painting.
            // Core routes through CombinedUndo's own dedicated RNM-blend
            // channel; the background has no such channel (its own snapshot
            // is folded into _bgUndoStack.CaptureExtra/ApplyExtra instead —
            // see SC2TerrainShell.EnsureBackgroundSubEditors), so only
            // record the core's checkpoint here when the core is actually
            // the active surface.
            if (Data == _win.Data) _win.RecordRnmBlendCheckpoint();
            else _win.RecordUndoCheckpointFor(Data, "Paint Background Blend");
        }
        if (!_isPainting || (!down && !drag)) return;
        var now = EditorApplication.timeSinceStartup;
        if (!down && now - _lastApply < kInterval) return;
        _lastApply = now;

        // Holding Ctrl temporarily acts as the eraser,
        // alongside the same Ctrl-held-erase behavior for Props/Costs/
        // TerrainTypes painting — see SC2CostsShell.IsErasingHeld and
        // SC2ObjectsShell.EffectivePropsErasing) — the "Erase (→0)" checkbox
        // (_rnmErase) itself never changes, only what this stroke actually
        // paints while Ctrl is held.
        var erasing = _rnmErase || e.control;
        var customBrush = _win.Data.UseCustomBrush ? _customBrushCache : null;

        // By default every input event dabs once (a round brush, and a custom stamp too). With "Place stamps by distance" a custom stamp dabs
        // by distance instead: one stamp where the stroke starts, then one every spacing along the path (none while the mouse has hardly
        // moved), so the stamps stay distinct instead of piling up into a blob.
        List<Vector3> dabs;
        var dabStrength = _strength;
        if (customBrush != null && _stampSpaced)
        {
            dabs = down ? _stampStroke.Begin(_brushPos) : _stampStroke.Move(_brushPos, SC2StampStroke.Spacing(_radius, _stampSpacing, _win.Data.CellSize));
            if (dabs.Count == 0) { e.Use(); return; }
        }
        else dabs = new List<Vector3> { _brushPos };

        foreach (var dab in dabs)
        {
            // A SEPARATE toggle from RandomizeBrushRotation — that one still rolls once per stroke (mouse-down) and holds it fixed for
            // the drag; this one re-rolls on every single dab, so a stroke keeps stamping fresh random orientations.
            if (_win.Data.UseCustomBrush && _win.Data.RandomizeBrushRotation && _win.Data.RandomizeBrushRotationContinuous)
                _rnmBrushRotationRad = Random.Range(0f, Mathf.PI * 2f);

            var symmetricPositions = _win.Data.GetSymmetricPositions(dab);
            for (var slot = 0; slot < symmetricPositions.Count; slot++)
            {
                // A reflected symmetric copy (X/Y/diagonal mirror) needs its stamp mirrored too, or it
                // paints an identical, unmirrored copy of the stroke instead of one turned the other way.
                SC2SymmetryMath.GetSlotMirror(_win.Data.Symmetry, slot, out var mirror, out var swapAxes);
                // A rotation-only copy (Radial, or the 180-degree slot of XY/DiagonalBoth) instead needs
                // its stamp turned to match, or it paints an unrotated copy. GetSlotRotationDegrees
                // returns the CONTENT rotation; negated here because _BrushRotation rotates the sample
                // offset, the inverse operation (see that method's own doc comment).
                var extraRotationRad = -SC2SymmetryMath.GetSlotRotationDegrees(_win.Data.Symmetry, _win.Data.RadialCount, slot) * Mathf.Deg2Rad;
                _win.ActiveRuntime.SetBrushPairLock(LockTableOrNull(_blendLock));
                _win.ActiveRuntime.PaintBlend(symmetricPositions[slot] - offset, _radius, dabStrength, _rnmBlend, erasing, customBrush, _rnmBrushRotationRad + extraRotationRad, mirror, swapAxes, _rnmRemoveFromLayer);
                _win.ActiveRuntime.SetBrushPairLock(null);
            }
        }
        // With editing layers the dabs above went into the active layer; flatten them into the mask once per tick.
        _win.ActiveRuntime.RecompositeBlendIfDirty();
#if UNITY_EDITOR
        SceneView.RepaintAll();
#endif
        e.Use();
    }

    void ApplySelection()
    {
        _win.RecordUndoCheckpointFor(Data, "Assign Selection");
        var selectionBefore = (short[])Data.TrianglePairIndex.Clone();
        foreach (var ti in _sel)
        {
            if (_assignLock.Any && ti >= 0 && ti < Data.TrianglePairIndex.Length && _assignLock.IsLocked(Data.TrianglePairIndex[ti])) continue;
            Data.AssignTriangleToPair(ti, _assignPair);
        }
        _sel.Clear();
        OutlineNewlyPaintedCells(Data, _assignPair, selectionBefore, syncFirst: false);
        _win.MarkDirtyAndRepaint();
    }

    // ── RNM blend undo ──────────────────────────────────────────────
    // Wired into SC2TerrainCombinedUndo.CaptureRnmBlend/ApplyRnmBlend by
    // SC2TerrainShell for the core, and directly into the background's own
    // _bgUndoStack.CaptureExtra/ApplyExtra (see
    // SC2TerrainShell.EnsureBackgroundSubEditors) — same capture/apply
    // shape either way, just targeting whichever surface was active when
    // CaptureActiveBlendState was called (RecordRnmBlendCheckpoint/
    // RecordUndoCheckpointFor above already route to the matching stack).

    private sealed class RnmBlendSnapshot
    {
        public SC2TerrainData Data;
        public int PairIndex;
        // The flattened mask. Only kept for a surface without editing layers; with layers the stack below reproduces it.
        public byte[] Bytes;
        // The whole layer stack (settings, order, active layer, and each layer's pixel array by reference).
        public SC2BlendMaskLayerStack Stack;
    }

    public object CaptureActiveBlendState()
    {
        var runtime = _captureRuntime ?? _win.ActiveRuntime;
        if (runtime == null || runtime.BlendRT == null)
            return null;
        // Copies the active layer's pending paint into the stack first, so the snapshot has its current pixels.
        runtime.CommitLayerEdits();
        var stack = runtime.ActiveBlendStack;
        return new RnmBlendSnapshot
        {
            Data = _captureData ?? Data,
            PairIndex = _captureData != null ? _capturePairIndex : _rnmPair,
            Stack = stack?.Clone(),
            Bytes = stack == null ? runtime.ReadBlendCPU() : null,
        };
    }

    public void ApplyActiveBlendState(object state)
    {
        if (state is not RnmBlendSnapshot snap) return;
        var runtime = snap.Data == _win.Data ? _win.Runtime : _win.BackgroundRuntime;
        if (runtime == null || snap.Data == null) return;

        _rnmPair = snap.PairIndex;
        // Ensures the correct mask group is the one actually active before
        // uploading — SetActivePairForBlend may load stale saved bytes for
        // the group first (if it wasn't already active), but the
        // UploadBlendCPU right after immediately overwrites BlendRT with the
        // exact restored bytes, so that intermediate load is harmless, just
        // slightly redundant work (undo/redo aren't a hot per-frame path).
        runtime.SetActivePairForBlend(_rnmPair, snap.Data);
        if (snap.Stack != null) runtime.ReplaceActiveBlendStack(snap.Data, snap.Stack);
        else runtime.UploadBlendCPU(snap.Bytes);
        runtime.FlushActiveBlendGroup(snap.Data);
        _win.MarkDirtyAndRepaint(geometryChanged: false);
    }

    // ── CPU sync ──────────────────────────────────────────────

    /// <summary>Synchronous readback — blocks the calling thread. Use only
    /// where a blocking call is acceptable (e.g. right before Export). Syncs
    /// whichever surface is currently ACTIVE (the brush's own target) — use
    /// the explicit-target overload below instead for a call site that
    /// specifically needs the CORE synced regardless of what the brush
    /// happens to be pointing at (SaveState/EnsurePairSync both do: an
    /// export or periodic background sync must never accidentally skip
    /// syncing the core's own pair data just because the user was last
    /// painting the background).</summary>
    public void SyncPairMapToCPU() => SyncPairMapToCPU(_win.ActiveRuntime, Data);

    public void SyncPairMapToCPU(SC2TerrainRuntime runtime, SC2TerrainData data)
    {
        if (runtime == null || data == null) return;
        _pairSyncGeneration++;
        ApplyPairBytes(runtime.ReadPairIndexCPU(), data, runtime.HoleTriPairIndex);
        _win.MarkPairCpuDataSynced();
    }

    /// <summary>
    /// Non-blocking readback via AsyncGPUReadback — does not stall the main thread.
    /// Use for interactive painting (MouseUp) where a sync stall hangs the editor.
    /// </summary>
    public void SyncPairMapToCPUAsync()
    {
        var runtime = _win.ActiveRuntime;
        var data = Data;
        var hole = runtime?.HoleTriPairIndex;
        if (runtime == null) return;
        var generation = ++_pairSyncGeneration;
        runtime.ReadPairIndexCPUAsync(bytes =>
        {
            // Stale — an Undo/Redo (or a newer sync) already superseded this
            // readback by the time it completed. Applying it now would
            // silently reintroduce whatever it was that got undone.
            if (generation != _pairSyncGeneration)
                return;
            var swCallback = System.Diagnostics.Stopwatch.StartNew();
            ApplyPairBytes(bytes, data, hole);
            _win.MarkPairCpuDataSynced();
            _win.MarkDirtyAndRepaint();
            SC2Log.Log($"[SC2PaintMode] Pair map readback applied in {swCallback.ElapsedMilliseconds}ms.");
        });
    }

    /// <summary>Takes an explicit target Data (and its runtime's own
    /// HoleTriPairIndex) rather than always reading the live Data property —
    /// the async overload above captures which surface was active when the
    /// readback was REQUESTED, not whichever happens to be active when it
    /// completes a frame or two later (the active surface can legitimately
    /// change in between, e.g. the user moved the cursor off the terrain
    /// entirely). hole is null for the core (which never carves cells) and
    /// the background's own carved-out core footprint otherwise — cells it
    /// marks are forced back to -1 regardless of what the raw GPU blit
    /// painted there, since PaintPair itself has no concept of the hole
    /// (same fix as the old SC2BackgroundEditMode.SyncPairMapFromRuntime,
    /// now shared here instead of duplicated).</summary>
    private void ApplyPairBytes(byte[] bytes, SC2TerrainData data, short[] hole = null)
    {
        if (data == null) return;
        var w = data.GridWidth; var h = data.GridHeight;
        var n = w * h;
        if (data.TrianglePairIndex.Length != n * 2)
            data.TrianglePairIndex = new short[n * 2];

        // Build cached pair tri counts (used by UI) in the same pass as the index sync
        var counts = new int[data.Pairs.Count];
        for (var gz = 0; gz < h; gz++)
        for (var gx = 0; gx < w; gx++)
        {
            var ti = (gz*w+gx)*2;
            if (hole != null && ti + 1 < hole.Length && hole[ti] < 0)
            {
                data.TrianglePairIndex[ti] = -1;
                data.TrianglePairIndex[ti+1] = -1;
                continue;
            }
            var raw  = bytes[gz*w+gx];
            var pair = (raw >= 255) ? (short)-1 : (short)raw;
            data.TrianglePairIndex[ti]   = pair;
            data.TrianglePairIndex[ti+1] = pair;
            if (pair >= 0 && pair < counts.Length) counts[pair] += 2;
        }
        data.PairTriCounts = counts;
        // The pairs' own cached counts (SC2MaterialPair.GetTriangleCount) are stale now too.
        foreach (var pair in data.Pairs) pair?.InvalidateTriCount();
    }

    public bool HandleKeyboardShortcut(UnityEngine.Event e) => false;
}
