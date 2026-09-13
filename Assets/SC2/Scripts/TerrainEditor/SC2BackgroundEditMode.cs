using System;
using UnityEngine;
using UnityEditor;

/// <summary>
/// Sculpt + pair-paint tool for the decorative background terrain
/// (SC2TerrainShell._bgEditMode, reached via the Geometry/Paint Pairs tabs
/// when the Background tab's "Editing Target" toggle is set to Background).
///
/// Deliberately NOT SC2GeometryMode reused/extended — that class is
/// hard-wired to _win.Runtime (SculptBrush/SculptSetHeight/UploadHeightsCPU),
/// the core's live GPU heightfield (chunked mesh, per-frame brush preview).
/// The background has no equivalent runtime — its only visual surface is
/// SC2BackgroundTerrainPreview's own periodically-rebuilt Mesh/MeshCollider
/// (see that class' own debounce comment). So this brush writes directly
/// into Data.Heights[]/Data.TrianglePairIndex[] on the CPU, raycasts the
/// preview's MeshCollider (SC2BackgroundTerrainPreview.TryRaycast) instead
/// of a live runtime, and only asks the preview to rebuild on stroke-end
/// (mouse-up), not every drag frame — an intentional trade-off: edits are
/// real and undoable immediately, but the viewport mesh itself catches up
/// on the preview's own ~1s cadence rather than showing live per-frame
/// deformation the way the core's brush does.
/// </summary>
public class SC2BackgroundEditMode
{
    public enum Tool { Raise, Lower, Smooth, SetHeight, PaintPair, PaintBlend }

    public SC2TerrainData Data;
    private readonly SC2TerrainShell _win;

    private Tool _tool = Tool.Raise;
    private float _radius = 128f;
    private float _strength = 0.5f;
    private float _hardness = 0.5f;
    private float _setHeight = SC2TerrainData.DefaultFlatHeight;
    private int _paintPairIndex;
    private float _blendTarget = 1f;
    private bool _blendErase;

    private bool _isPainting;
    private bool _brushVis;
    private Vector3 _brushPos;
    private double _lastApply;
    private const double kMinInterval = 0.03;

    // F interactive resize, Blender-style — see
    // SC2BrushRadiusGesture's own doc comment.
    private readonly SC2BrushRadiusGesture _radiusGesture = new();

    public SC2BackgroundEditMode(SC2TerrainShell win, SC2TerrainData data)
    {
        _win = win;
        Data = data;
    }

    // ── Panel ────────────────────────────────────────────────────────────

    public void DrawPanel()
    {
        if (Data == null)
        {
            SC2TerrainShell.DrawUnifiedNote("Background terrain not generated yet — enable it on the Background tab first.");
            return;
        }

        EditorGUILayout.LabelField("Background Sculpt / Paint", EditorStyles.boldLabel);
        SC2TerrainShell.DrawUnifiedNote("CPU-only brush — no live GPU chunks, so the mesh you see updates about once a second (or immediately on mouse-up), not per frame. Click-drag in the viewport to apply.");

        var toolLabels = new[] { "Raise", "Lower", "Smooth", "Set Height", "Paint Pair", "Paint Blend" };
        var toolIdx = SC2TerrainShell.DrawUnifiedSegmentGrid((int)_tool, toolLabels, 3);
        _tool = (Tool)Mathf.Clamp(toolIdx, 0, toolLabels.Length - 1);

        SC2TerrainShell.BeginUnifiedPanelBox();
        _radius = SC2TerrainShell.DrawCompactSlider("Radius", _radius, 4f, 4096f, "bg-radius");
        if (_tool != Tool.PaintPair)
        {
            _strength = SC2TerrainShell.DrawCompactSlider("Strength", _strength, 0.01f, 1f, "bg-strength");
            if (_tool != Tool.PaintBlend)
                _hardness = SC2TerrainShell.DrawCompactSlider("Hardness", _hardness, 0f, 1f, "bg-hardness");
        }

        if (_tool == Tool.SetHeight)
            _setHeight = EditorGUILayout.FloatField("Target Height", _setHeight);

        if (_tool == Tool.PaintPair || _tool == Tool.PaintBlend)
        {
            var names = new string[Mathf.Max(1, Data.Pairs.Count)];
            for (var i = 0; i < Data.Pairs.Count; i++) names[i] = Data.Pairs[i].Name;
            if (Data.Pairs.Count == 0) names[0] = "(no pairs — add one on the Pairs tab)";
            _paintPairIndex = Mathf.Clamp(_paintPairIndex, 0, Mathf.Max(0, Data.Pairs.Count - 1));
            _paintPairIndex = EditorGUILayout.Popup("Pair", _paintPairIndex, names);
        }

        if (_tool == Tool.PaintPair)
        {
            SC2TerrainShell.DrawUnifiedNote("Assigns the picked pair to background triangles under the brush — never touches the carved hole against the core, only the background's own surrounding geometry.");
        }
        else if (_tool == Tool.PaintBlend)
        {
            _blendTarget = SC2TerrainShell.DrawCompactSlider("Blend alpha", _blendTarget, 0f, 1f, "bg-blend-target");
            SC2TerrainShell.DrawUnifiedNote(
                _blendErase ? "Erase (→ Diffuse1)" :
                _blendTarget < .1f ? "← Diffuse1 (base)" :
                _blendTarget > .9f ? "→ Diffuse2 (blend)" : "↔ blending");
            _blendErase = EditorGUILayout.Toggle("Erase (→0)", _blendErase);
            SC2TerrainShell.DrawUnifiedNote("Paints the picked pair's own RNM/blend mask — the same mask CreateLivePairMaterial samples for live Diffuse1↔Diffuse2 blending and export writes into terrain.bdf.");
        }
        SC2TerrainShell.EndUnifiedPanelBox();
    }

    // ── Viewport ─────────────────────────────────────────────────────────

    public void OnGameViewGUI(Camera camera, Rect viewportRect, Vector2 mousePosition)
    {
        var e = Event.current;
        if (e == null || camera == null || Data == null)
            return;
        if (!viewportRect.Contains(mousePosition) && !_isPainting)
            return;

        RaycastBackground(camera, mousePosition, viewportRect);
        // Checked BEFORE DrawGizmo/HandleInput (not after) so an already-
        // active resize draws its own preview circle and keeps responding
        // to mouse movement/Escape/click-to-confirm instead of this tool's
        // own brush cursor/sculpt-or-paint input firing underneath it.
        if (_radiusGesture.HandleInput(camera, viewportRect, mousePosition, _brushVis, _brushPos, true, ref _radius, 4f, 4096f,
            sampleHeight: (x, z) => SC2ObjectsExport.SampleTerrainHeight(Data, x, z)))
            return;
        DrawGizmo(camera, viewportRect);
        HandleInput(e);
    }

    private void RaycastBackground(Camera camera, Vector2 guiMousePosition, Rect viewportRect)
    {
        _brushVis = false;
        var ray = SC2TerrainShell.GameGuiPointToRay(camera, viewportRect, guiMousePosition);
        if (SC2BackgroundTerrainPreview.TryRaycast(ray, out var hit))
        {
            _brushPos = hit.point;
            _brushVis = true;
        }
    }

    private void DrawGizmo(Camera camera, Rect viewportRect)
    {
        if (!_brushVis) return;
        Color accent, accentSoft;
        if (_tool == Tool.PaintPair) { accent = new Color(0.35f, 0.65f, 1f, 0.95f); accentSoft = new Color(0.25f, 0.5f, 0.9f, 0.7f); }
        else if (_tool == Tool.PaintBlend) { accent = new Color(1f, 0.55f, 0.9f, 0.95f); accentSoft = new Color(0.9f, 0.4f, 0.8f, 0.7f); }
        else { accent = new Color(0.3f, 1f, 0.4f, 0.95f); accentSoft = new Color(0.2f, 0.8f, 0.3f, 0.7f); }
        SC2TerrainShell.DrawGameViewBrush(camera, viewportRect, _brushPos, _radius, accent, accentSoft, _radius * _hardness,
            sampleHeight: (x, z) => SC2ObjectsExport.SampleTerrainHeight(Data, x, z));

        // Symmetry here follows the CORE terrain's own setting (_win.Data),
        // same as the brush-apply code below — background painting has no
        // separate symmetry setting of its own.
        if (_win.Data.Symmetry != SymmetryMode.None)
            SC2TerrainShell.DrawSymmetryPointMarkers(camera, viewportRect, _brushPos, _win.Data.GetSymmetricPositions(_brushPos),
                accentSoft, sampleHeight: (x, z) => SC2ObjectsExport.SampleTerrainHeight(Data, x, z));
    }

    private void HandleInput(Event e)
    {
        var down = e.type == EventType.MouseDown && e.button == 0 && !e.alt;
        var drag = e.type == EventType.MouseDrag && e.button == 0 && !e.alt;

        if (e.type == EventType.MouseUp)
        {
            if (_isPainting)
                SC2BackgroundTerrainPreview.MarkDirty();
            _isPainting = false;
            return;
        }

        if (!_brushVis) return;

        // Same Blender/Photoshop-style eyedropper as the core's
        // own SC2PaintMode.HandleAssign, for parity: Alt+click samples the
        // background pair under the cursor into _paintPairIndex instead of
        // painting with it. Alt already fully excludes down/drag above
        // from ever firing here, so this never conflicts with painting.
        if (_tool == Tool.PaintPair && e.type == EventType.MouseDown && e.button == 0 && e.alt)
        {
            var offset = SC2BackgroundTerrain.GetWorldOffset(_win.Data);
            var localX = _brushPos.x - offset.x;
            var localZ = _brushPos.z - offset.z;
            var cell = Mathf.Max(0.0001f, Data.CellSize);
            var gx = Mathf.Clamp(Mathf.FloorToInt(localX / cell), 0, Data.GridWidth - 1);
            var gz = Mathf.Clamp(Mathf.FloorToInt(localZ / cell), 0, Data.GridHeight - 1);
            var ti = (gz * Data.GridWidth + gx) * 2;
            if (Data.TrianglePairIndex != null && ti >= 0 && ti < Data.TrianglePairIndex.Length)
            {
                var sampled = Data.TrianglePairIndex[ti];
                if (sampled >= 0 && sampled < Data.Pairs.Count)
                {
                    _paintPairIndex = sampled;
                    _win.Repaint();
                }
            }
            e.Use();
            return;
        }

        if (down)
        {
            _isPainting = true;
            var label = _tool == Tool.PaintPair ? "Paint Background Pair"
                : _tool == Tool.PaintBlend ? "Paint Background Blend"
                : "Sculpt Background";
            _win.RecordUndoCheckpointFor(Data, label);
        }

        if (!_isPainting || (!down && !drag)) return;
        var now = EditorApplication.timeSinceStartup;
        if (!down && now - _lastApply < kMinInterval) return;
        _lastApply = now;

        // This class never had any symmetry handling at all —
        // unlike SC2GeometryMode/SC2PaintMode (core), which both loop over
        // Data.GetSymmetricPositions before applying a brush. There's only
        // ONE symmetry config for the whole map (Terrain tab's own
        // Symmetry buttons, always editing the CORE's own SC2TerrainData —
        // background has no separate symmetry setting of its own), so this
        // reads it off _win.Data (core), not Data (which is the
        // background's own instance when this class is active) —
        // GetSymmetricPositions is pure world-space math (mirrors X/Z
        // around SymmetryCenter, Y untouched), so it works identically
        // whether the input position happens to be over the core or the
        // background.
        foreach (var pos in _win.Data.GetSymmetricPositions(_brushPos))
        {
            if (_tool == Tool.PaintPair) ApplyPaintPair(pos);
            else if (_tool == Tool.PaintBlend) ApplyPaintBlend(pos);
            else ApplySculpt(pos);
        }

        e.Use();
        _win.Repaint();
    }

    // ── CPU brush ────────────────────────────────────────────────────────

    private void ApplySculpt(Vector3 worldHit)
    {
        var offset = SC2BackgroundTerrain.GetWorldOffset(_win.Data);
        var localX = worldHit.x - offset.x;
        var localZ = worldHit.z - offset.z;
        var cell = Mathf.Max(0.0001f, Data.CellSize);
        var gw = Data.GridWidth;
        var gh = Data.GridHeight;
        var vw = gw + 1;
        var r2 = _radius * _radius;
        var x0 = Mathf.Max(0, Mathf.FloorToInt((localX - _radius) / cell));
        var x1 = Mathf.Min(gw, Mathf.CeilToInt((localX + _radius) / cell));
        var z0 = Mathf.Max(0, Mathf.FloorToInt((localZ - _radius) / cell));
        var z1 = Mathf.Min(gh, Mathf.CeilToInt((localZ + _radius) / cell));
        // Tuned for a per-application (not per-GPU-frame) brush: applied at
        // most ~33x/sec (kMinInterval) while dragging, once on mouse-down.
        var dt = _strength * 6f;

        for (var gz = z0; gz <= z1; gz++)
        for (var gx = x0; gx <= x1; gx++)
        {
            var wx = gx * cell;
            var wz = gz * cell;
            var dx = wx - localX;
            var dz = wz - localZ;
            var d2 = dx * dx + dz * dz;
            if (d2 > r2) continue;
            var falloff = HardnessFalloff(Mathf.Sqrt(d2) / _radius, _hardness);
            if (falloff <= 0f) continue;
            var idx = gz * vw + gx;
            switch (_tool)
            {
                case Tool.Raise:
                    Data.Heights[idx] += falloff * dt;
                    break;
                case Tool.Lower:
                    Data.Heights[idx] -= falloff * dt;
                    break;
                case Tool.Smooth:
                    Data.Heights[idx] = Mathf.Lerp(Data.Heights[idx], AvgNeighbor(gx, gz, gw, gh, vw), falloff * 0.5f);
                    break;
                case Tool.SetHeight:
                    Data.Heights[idx] = Mathf.Lerp(Data.Heights[idx], _setHeight, Mathf.Clamp01(falloff * Mathf.Max(0.01f, _strength)));
                    break;
            }
            Data.Heights[idx] = SC2TerrainData.ClampHeight(Data.Heights[idx]);
        }
        Data.InvalidateGeometryCache();
    }

    private float AvgNeighbor(int gx, int gz, int gw, int gh, int vw)
    {
        var h = Data.Heights;
        var sum = h[gz * vw + gx];
        var count = 1;
        if (gx > 0)  { sum += h[gz * vw + gx - 1]; count++; }
        if (gx < gw) { sum += h[gz * vw + gx + 1]; count++; }
        if (gz > 0)  { sum += h[(gz - 1) * vw + gx]; count++; }
        if (gz < gh) { sum += h[(gz + 1) * vw + gx]; count++; }
        return sum / count;
    }

    // Falloff: hardness=0 -> cosine, hardness=1 -> full strength with hard
    // edge. Same formula as SC2TerrainRuntime.HardnessFalloff (private
    // there) — kept as its own small copy rather than exposing that one,
    // since this is the only other consumer.
    private static float HardnessFalloff(float t, float hardness)
    {
        if (hardness >= 0.999f) return 1f;
        var soft = Mathf.Cos(t * Mathf.PI * 0.5f);
        float hard;
        if (t <= hardness) hard = 1f;
        else hard = Mathf.Cos((t - hardness) / Mathf.Max(1f - hardness, 0.001f) * Mathf.PI * 0.5f);
        return Mathf.Lerp(soft, hard, hardness);
    }

    private void ApplyPaintPair(Vector3 worldHit)
    {
        if (Data.TrianglePairIndex == null || Data.Pairs == null || Data.Pairs.Count == 0) return;

        var offset = SC2BackgroundTerrain.GetWorldOffset(_win.Data);
        var localX = worldHit.x - offset.x;
        var localZ = worldHit.z - offset.z;
        var cell = Mathf.Max(0.0001f, Data.CellSize);
        var gw = Data.GridWidth;
        var gh = Data.GridHeight;
        var r2 = _radius * _radius;
        var x0 = Mathf.Max(0, Mathf.FloorToInt((localX - _radius) / cell));
        var x1 = Mathf.Min(gw - 1, Mathf.CeilToInt((localX + _radius) / cell));
        var z0 = Mathf.Max(0, Mathf.FloorToInt((localZ - _radius) / cell));
        var z1 = Mathf.Min(gh - 1, Mathf.CeilToInt((localZ + _radius) / cell));
        var pairIdx = (short)Mathf.Clamp(_paintPairIndex, 0, Data.Pairs.Count - 1);

        for (var gz = z0; gz <= z1; gz++)
        for (var gx = x0; gx <= x1; gx++)
        {
            var cx = (gx + 0.5f) * cell;
            var cz = (gz + 0.5f) * cell;
            var dx = cx - localX;
            var dz = cz - localZ;
            if (dx * dx + dz * dz > r2) continue;
            var t = (gz * gw + gx) * 2;
            if (t < 0 || t + 1 >= Data.TrianglePairIndex.Length) continue;
            // Never paint over the carved hole (-1 cells) — those
            // triangles don't exist against the core's own footprint.
            if (Data.TrianglePairIndex[t] >= 0) Data.TrianglePairIndex[t] = pairIdx;
            if (Data.TrianglePairIndex[t + 1] >= 0) Data.TrianglePairIndex[t + 1] = pairIdx;
        }

        foreach (var p in Data.Pairs) p?.InvalidateTriCount();
        Data.InvalidateGeometryCache();
    }

    // ── RNM/blend mask brush ────────────────────────────────────────────

    private void ApplyPaintBlend(Vector3 worldHit)
    {
        if (Data.Pairs == null || Data.Pairs.Count == 0) return;
        var pairIdx = Mathf.Clamp(_paintPairIndex, 0, Data.Pairs.Count - 1);
        var pair = Data.Pairs[pairIdx];
        if (pair == null) return;
        pair.EnsureRnmTexture();
        var w = pair.MaskWidth;
        var h = pair.MaskHeight;
        var bytes = pair.RnmMaskBytes;
        if (bytes == null || bytes.Length != w * h * 4) return;

        SC2BackgroundTerrain.ComputeBounds(_win.Data, _win.Data.BackgroundTerrainSize, out var minX, out var minZ, out var maxX, out var maxZ, out _);
        var spanX = Mathf.Max(1f, maxX - minX);
        var spanZ = Mathf.Max(1f, maxZ - minZ);

        var centerU = (worldHit.x - minX) / spanX;
        var centerV = (worldHit.z - minZ) / spanZ;
        var radiusU = _radius / spanX;
        var radiusV = _radius / spanZ;
        if (radiusU <= 0f || radiusV <= 0f) return;

        var px0 = Mathf.Clamp(Mathf.FloorToInt((centerU - radiusU) * w), 0, w - 1);
        var px1 = Mathf.Clamp(Mathf.CeilToInt((centerU + radiusU) * w), 0, w - 1);
        var py0 = Mathf.Clamp(Mathf.FloorToInt((centerV - radiusV) * h), 0, h - 1);
        var py1 = Mathf.Clamp(Mathf.CeilToInt((centerV + radiusV) * h), 0, h - 1);
        if (px1 < px0 || py1 < py0) return;

        var blockW = px1 - px0 + 1;
        var blockH = py1 - py0 + 1;
        var target = (_blendErase ? 0f : _blendTarget) * 255f;
        var block = new Color32[blockW * blockH];

        for (var py = py0; py <= py1; py++)
        {
            var v = (py + 0.5f) / h;
            var offY = (v - centerV) / radiusV;
            for (var px = px0; px <= px1; px++)
            {
                var u = (px + 0.5f) / w;
                var offX = (u - centerU) / radiusU;
                var byteIdx = (py * w + px) * 4;
                var bi = (py - py0) * blockW + (px - px0);
                var d = Mathf.Sqrt(offX * offX + offY * offY);
                if (d >= 1f)
                {
                    block[bi] = new Color32(bytes[byteIdx], bytes[byteIdx + 1], bytes[byteIdx + 2], bytes[byteIdx + 3]);
                    continue;
                }
                // Cosine falloff, same shape SC2PaintBrush.shader's own
                // round-brush RNM path uses (_PaintMode==2) — no hardness
                // parameter there either.
                var falloff = Mathf.Cos(d * Mathf.PI * 0.5f);
                var amt = Mathf.Clamp01(falloff * _strength);
                // RGB drifts toward neutral 128 alongside alpha, matching
                // the shader's own _PaintColor=(.5,.5,.5,target) target —
                // RGB is otherwise unused by CreateLivePairMaterial/export,
                // this just keeps a painted mask's RGB consistent with what
                // the core's own GPU brush would have produced.
                var r = (byte)Mathf.RoundToInt(Mathf.Lerp(bytes[byteIdx], 128f, amt));
                var g = (byte)Mathf.RoundToInt(Mathf.Lerp(bytes[byteIdx + 1], 128f, amt));
                var b = (byte)Mathf.RoundToInt(Mathf.Lerp(bytes[byteIdx + 2], 128f, amt));
                var a = (byte)Mathf.RoundToInt(Mathf.Lerp(bytes[byteIdx + 3], target, amt));
                bytes[byteIdx] = r; bytes[byteIdx + 1] = g; bytes[byteIdx + 2] = b; bytes[byteIdx + 3] = a;
                block[bi] = new Color32(r, g, b, a);
            }
        }

        pair.RnmMaskTexture.SetPixels32(px0, py0, blockW, blockH, block);
        pair.RnmMaskTexture.Apply(false);
    }

    // ── RNM mask undo (opaque "Extra" channel on _win's background
    // SC2TerrainUndoStack — see SC2TerrainShell.EnsureBackgroundSubEditors)
    // ─────────────────────────────────────────────────────────────────────
    //
    // RnmMaskBytes is deliberately shared by reference (not deep-cloned) by
    // SC2TerrainUndoStack's own generic Pairs snapshot — see that class'
    // doc comment — since ApplyPaintBlend mutates the SAME array in place
    // every stroke frame, a plain checkpoint would silently corrupt its own
    // "before" snapshot. Mirrors the core's separate RNM-blend undo channel
    // (SC2TerrainCombinedUndo.RecordRnmBlendCheckpoint/CaptureActiveBlend
    // State), just CPU-byte-based instead of a GPU RenderTexture capture,
    // and hooked through SC2TerrainUndoStack.CaptureExtra/ApplyExtra
    // instead of a second parallel undo stack, since a plain byte[] clone
    // is cheap enough to piggyback on every checkpoint (gated to only
    // actually clone when the active tool is PaintBlend).

    public object CaptureRnmBlendExtra()
    {
        if (_tool != Tool.PaintBlend) return null;
        if (Data?.Pairs == null || _paintPairIndex < 0 || _paintPairIndex >= Data.Pairs.Count) return null;
        var pair = Data.Pairs[_paintPairIndex];
        if (pair?.RnmMaskBytes == null || pair.RnmMaskBytes.Length == 0) return null;
        return (_paintPairIndex, (byte[])pair.RnmMaskBytes.Clone());
    }

    public void ApplyRnmBlendExtra(object extra)
    {
        if (extra is not ValueTuple<int, byte[]> t) return;
        if (Data?.Pairs == null || t.Item1 < 0 || t.Item1 >= Data.Pairs.Count) return;
        var pair = Data.Pairs[t.Item1];
        if (pair == null) return;
        pair.RnmMaskBytes = t.Item2;
        if (pair.RnmMaskTexture != null && pair.RnmMaskBytes.Length == pair.MaskWidth * pair.MaskHeight * 4)
        {
            pair.RnmMaskTexture.LoadRawTextureData(pair.RnmMaskBytes);
            pair.RnmMaskTexture.Apply(false);
        }
    }
}
