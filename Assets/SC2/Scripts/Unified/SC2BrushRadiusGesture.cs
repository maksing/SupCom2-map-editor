using System;
using UnityEngine;

/// <summary>
/// Blender-style interactive brush-radius resize gesture, bound by default
/// to plain F (rebindable — see SC2Hotkeys, action id "Brush.RadiusResizeGesture")
/// — real Blender uses unmodified F, not Ctrl+F. This action's default key
/// happens to match SC2GameViewEditorUI.HandleHotkeys' own default
/// "Global.FocusCamera" binding, but that's safe by construction regardless
/// of what either action is currently bound to, not a real conflict:
/// HandleTerrainGameViewInput (which calls into whichever tool owns the
/// active brush, and thus this gesture) runs BEFORE HandleHotkeys in the
/// same OnGUI, and HandleHotkeys itself already guards on
/// `e.type == EventType.KeyDown` — once this gesture calls e.Use() below,
/// the event's type becomes Used, so HandleHotkeys' own check is skipped
/// automatically that frame. The gesture only starts when a brush tool is
/// actually hovering valid terrain (hasHover) in the first place; with no
/// brush hovering, the event reaches HandleHotkeys untouched and its bound
/// action still fires exactly as before. The brush circle stays anchored at wherever it was
/// hovering the moment the key was pressed (it does NOT chase the cursor
/// around during the resize, matching Blender's own "F" gesture exactly),
/// and the distance from that anchor to the current mouse position drives
/// the radius live. Left-click (or Enter) confirms the new radius; Escape
/// cancels and restores the radius captured at the start of the gesture.
///
/// Right-click is deliberately NOT bound to cancel, unlike Blender's own
/// convention — this project's own camera controls already use right-drag
/// for "fly" navigation (SC2GameViewEditorUI's _flying), so repurposing it
/// here would silently fight that instead of adding a redundant cancel
/// path Escape already covers.
///
/// One instance per brush tool (SC2CostsShell/SC2ObjectsShell each own
/// their own) — the gesture is inherently stateful across frames (must
/// remember whether it's active, the anchor, and the pre-resize radius),
/// so it can't be a static/shared-singleton the way DrawGameViewBrush is.
/// </summary>
public sealed class SC2BrushRadiusGesture
{
    private bool _active;
    private float _startRadius;
    private Vector3 _anchorWorld;

    public bool Active => _active;

    /// <summary>Call once per frame, every frame the owning brush tool is
    /// armed — before that tool's own cursor-drawing and click/drag
    /// handling, since an active (or just-started) resize must own the
    /// click and draw its own preview circle instead of a paint tick or the
    /// tool's normal cursor firing underneath it. Returns true when the
    /// gesture consumed this frame (caller should skip its own handling for
    /// this frame entirely); radius is updated in place.</summary>
    public bool HandleInput(Camera camera, Rect viewportRect, Vector2 mousePosition, bool hasHover, Vector3 hoverWorld,
        bool allowInput, ref float radius, float min, float max, Func<float, float, float> sampleHeight = null)
    {
        var e = Event.current;

        if (!_active)
        {
            if (!allowInput || !hasHover || e == null || e.type != EventType.KeyDown || !SC2Hotkeys.Matches("Brush.RadiusResizeGesture", e))
                return false;
            _active = true;
            _startRadius = radius;
            _anchorWorld = hoverWorld;
            e.Use();
            return true;
        }

        // Same world-radius <-> screen-pixel relationship DrawGameViewBrush
        // itself uses (projecting the anchor and one world-unit-away from it,
        // measuring the pixel gap) — inverted here so the live circle's edge
        // always lands exactly under the mouse cursor, regardless of camera
        // zoom/distance/angle.
        var anchorGui = SC2TerrainShell.WorldToGameGui(camera, viewportRect, _anchorWorld);
        var unitGui = SC2TerrainShell.WorldToGameGui(camera, viewportRect, _anchorWorld + Vector3.right);
        var pixelsPerWorldUnit = Mathf.Max(0.0001f, Vector2.Distance(anchorGui, unitGui));
        radius = Mathf.Clamp(Vector2.Distance(anchorGui, mousePosition) / pixelsPerWorldUnit, min, max);

        SC2TerrainShell.DrawGameViewBrush(camera, viewportRect, _anchorWorld, radius, new Color(1f, .95f, .25f, .95f), Color.clear, sampleHeight: sampleHeight);

        if (e != null && e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
        {
            radius = _startRadius;
            _active = false;
            e.Use();
        }
        else if (e != null && e.type == EventType.KeyDown && (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter))
        {
            _active = false;
            e.Use();
        }
        else if (e != null && e.type == EventType.MouseDown && e.button == 0)
        {
            _active = false;
            e.Use();
        }

        return true;
    }
}
