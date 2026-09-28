using System.Collections.Generic;
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
        var modeLabels = new[] { "Assign Pairs", "Blend Mask" };
        _sub = (Sub)SC2TerrainShell.DrawUnifiedSegmentGrid((int)_sub, modeLabels, 2);

        EditorGUILayout.Space(4);
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

    void DrawAssign()
    {
        if (Data.Pairs.Count == 0)
        { SC2TerrainShell.DrawUnifiedNote("Create pairs in the Pairs tab first."); return; }

        // Pair selector with colour swatches
        var names = new string[Data.Pairs.Count + 1];
        names[0] = "(Unassign)";
        for (var i = 0; i < Data.Pairs.Count; i++) names[i+1] = Data.Pairs[i].Name;
        var w = SC2TerrainShell.GetPanelContentWidth();
        EditorGUILayout.BeginHorizontal(GUILayout.Width(w), GUILayout.MaxWidth(w));
        if (_assignPair >= 0 && _assignPair < Data.Pairs.Count)
        {
            var swatch = GUILayoutUtility.GetRect(SC2UiScale.Pxf(14f), SC2UiScale.Pxf(18f), GUILayout.Width(SC2UiScale.Pxf(14f)), GUILayout.Height(SC2UiScale.Pxf(18f)));
            EditorGUI.DrawRect(swatch, Data.PairColor(_assignPair));
        }
        _assignPair = EditorGUILayout.Popup("paint-assign-pair", _assignPair + 1, names, SC2UiScale.PopupStyle(), GUILayout.Width(Mathf.Max(60f, w - 20f)), GUILayout.MaxWidth(Mathf.Max(60f, w - 20f))) - 1;
        EditorGUILayout.EndHorizontal();

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
            // Fill PairMapRT with pair index colour via blit
            if (_win.ActiveRuntime != null)
            {
                var prev = RenderTexture.active;
                RenderTexture.active = _win.ActiveRuntime.PairMapRT;
                GL.Clear(false, true, new Color(_assignPair / 255f, 0, 0, 1));
                RenderTexture.active = prev;
            }
            Data.AssignAllTrianglesToPair(_assignPair);
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
            var n = Data.TrianglePairIndex.Length;
            for (var i = 0; i < n; i++) Data.TrianglePairIndex[i] = -1;
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
        _rnmBlend = SC2TerrainShell.DrawCompactSlider("Blend alpha", _rnmBlend, 0f, 1f);
        EditorGUILayout.LabelField(
            _rnmBlend < .1f ? "← D1 (base)" :
            _rnmBlend > .9f ? "→ D2 (blend)" : "↔ blending",
            EditorStyles.centeredGreyMiniLabel);
        _rnmErase = EditorGUILayout.Toggle("Erase (→0)", _rnmErase);

        // Brush shape is a shared setting, always the CORE's own _win.Data —
        // see SC2GeometryMode's own header comment for why.
        EditorGUILayout.Space(4);
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
        if (GUILayout.Button("Reset (α=0)", GUILayout.Height(SC2UiScale.Pxf(22f)), GUILayout.Width(bw)) && _win.ActiveRuntime != null)
        {
            var prev = RenderTexture.active;
            RenderTexture.active = _win.ActiveRuntime.BlendRT;
            GL.Clear(false, true, new Color(.5f, .5f, .5f, 0f));
            RenderTexture.active = prev;
            _win.ActiveRuntime.FlushActiveBlendGroup(Data);
            EditorUtility.SetDirty(Data);
        }
        if (GUILayout.Button("Fill (α=1)", GUILayout.Height(SC2UiScale.Pxf(22f)), GUILayout.Width(bw)) && _win.ActiveRuntime != null)
        {
            var prev = RenderTexture.active;
            RenderTexture.active = _win.ActiveRuntime.BlendRT;
            GL.Clear(false, true, new Color(.5f, .5f, .5f, 1f));
            RenderTexture.active = prev;
            _win.ActiveRuntime.FlushActiveBlendGroup(Data);
            EditorUtility.SetDirty(Data);
        }
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
            _customBrushCache = SC2TerrainMaterialFactory.LoadTexturePublicAny(currentPath);
            _customBrushCachedPath = currentPath;
        }
        var pathDraft = _customBrushCachedPath;
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
                // raw height.
                _win.InvalidateLiveHeightGrid();
            }
            var swAfterDirty = swMouseUp.ElapsedMilliseconds;
            _isPainting = false;
            // Non-blocking readback — async GPU request, no SceneView hang.
            var swSync = System.Diagnostics.Stopwatch.StartNew();
            var syncNow = EditorApplication.timeSinceStartup;
            if (syncNow - _lastSyncTime >= kSyncThrottle)
            {
                _lastSyncTime = syncNow;
                SyncPairMapToCPUAsync();
            }
            var swSyncMs = swSync.ElapsedMilliseconds;

            var swMarkDirty = System.Diagnostics.Stopwatch.StartNew();
            _win.MarkDirtyAndRepaint();
            var swMarkDirtyMs = swMarkDirty.ElapsedMilliseconds;

            // Same as SC2GeometryMode's own MouseUp — keeps the background's
            // seam skirt glued after a completed Paint Pair stroke on it;
            // no-op when the active surface is the core. Logs its own
            // internal breakdown separately (see its own doc comment).
            if (Data != _win.Data) _win.RefreshBackgroundRuntime();
            SC2Log.Log($"[SC2PaintMode] HandleAssign MouseUp: MarkDirty+InvalidateLiveHeightGrid={swAfterDirty}ms, SyncPairMapToCPUAsync={swSyncMs}ms, MarkDirtyAndRepaint={swMarkDirtyMs}ms, total={swMouseUp.ElapsedMilliseconds}ms");
            return;
        }
        if (down)
        {
            _isPainting = true;
            if (!_selectMode)
            {
                // GPU paint is authoritative for TrianglePairIndex during a stroke;
                // sync it to CPU before snapshotting so undo captures the true
                // pre-stroke state (not a stale value from the last throttled
                // async readback).
                SyncPairMapToCPU();
                _win.RecordUndoCheckpointFor(Data, "Paint Pair");
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
            foreach (var worldPos in _win.Data.GetSymmetricPositions(_brushPos))
                _win.ActiveRuntime.PaintPair(worldPos - offset, _radius, _strength, _assignPair);
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

            // Same as SC2GeometryMode's own MouseUp — keeps the background's
            // seam skirt glued after a completed Paint Blend stroke on it
            // (also refreshes its own RnmMaskTexture, which the chunk
            // runtime doesn't use but the seam skirt's shared material
            // does not depend on either — SC2TerrainRuntime.SharedChunkMat
            // samples BlendRT directly); no-op when active surface is core.
            // Logs its own internal breakdown separately (see its own doc
            // comment).
            if (Data != _win.Data) _win.RefreshBackgroundRuntime();
            _win.Repaint();
            SC2Log.Log($"[SC2PaintMode] HandleRnm MouseUp: FlushActiveBlendGroup={swFlushMs}ms, EditorUtility.SetDirty={swSetDirtyMs}ms, total={swMouseUp.ElapsedMilliseconds}ms");
            return;
        }
        if (down)
        {
            _isPainting = true;
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

        // A SEPARATE toggle from RandomizeBrushRotation — that one still rolls
        // once per stroke (mouse-down) and holds it fixed for the drag; this
        // one re-rolls on every single application (down AND every drag
        // frame that passes the throttle above), so continuously dragging
        // the mouse keeps stamping fresh random orientations instead of one
        // fixed one for the whole stroke.
        if (_win.Data.UseCustomBrush && _win.Data.RandomizeBrushRotation && _win.Data.RandomizeBrushRotationContinuous)
            _rnmBrushRotationRad = Random.Range(0f, Mathf.PI * 2f);

        // Holding Ctrl temporarily acts as the eraser,
        // alongside the same Ctrl-held-erase behavior for Props/Costs/
        // TerrainTypes painting — see SC2CostsShell.IsErasingHeld and
        // SC2ObjectsShell.EffectivePropsErasing) — the "Erase (→0)" checkbox
        // (_rnmErase) itself never changes, only what this stroke actually
        // paints while Ctrl is held.
        var erasing = _rnmErase || e.control;
        var customBrush = _win.Data.UseCustomBrush ? _customBrushCache : null;
        var symmetricPositions = _win.Data.GetSymmetricPositions(_brushPos);
        for (var slot = 0; slot < symmetricPositions.Count; slot++)
        {
            // A reflected symmetric copy (X/Y/diagonal mirror) needs its stamp mirrored too, or it
            // paints an identical (not mirrored) copy of the stroke — reported directly ("одна из
            // этих полосок должна отзеркалиться"). A rotation-only copy (radial, the 180-degree
            // slot of XY/DiagonalBoth) is left as before: same stamp, just moved.
            SC2SymmetryMath.GetSlotMirror(_win.Data.Symmetry, slot, out var mirror, out var swapAxes);
            _win.ActiveRuntime.PaintBlend(symmetricPositions[slot] - offset, _radius, _strength, _rnmBlend, erasing, customBrush, _rnmBrushRotationRad, mirror, swapAxes);
        }
#if UNITY_EDITOR
        SceneView.RepaintAll();
#endif
        e.Use();
    }

    void ApplySelection()
    {
        _win.RecordUndoCheckpointFor(Data, "Assign Selection");
        foreach (var ti in _sel) Data.AssignTriangleToPair(ti, _assignPair);
        _sel.Clear();
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
        public byte[] Bytes;
    }

    public object CaptureActiveBlendState()
    {
        if (_win.ActiveRuntime == null || _win.ActiveRuntime.BlendRT == null)
            return null;
        return new RnmBlendSnapshot { Data = Data, PairIndex = _rnmPair, Bytes = _win.ActiveRuntime.ReadBlendCPU() };
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
        runtime.UploadBlendCPU(snap.Bytes);
        runtime.FlushActiveBlendGroup(snap.Data);
        _win.MarkDirtyAndRepaint();
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
            ApplyPairBytes(bytes, data, hole);
            _win.MarkPairCpuDataSynced();
            _win.MarkDirtyAndRepaint();
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
    }

    public bool HandleKeyboardShortcut(UnityEngine.Event e) => false;
}
