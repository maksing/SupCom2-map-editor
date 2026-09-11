using UnityEngine;

/// <summary>
/// Shared state + helpers for the "Top" view's screen-orientation mirror.
///
/// The real game's own top-down screen convention has its coordinate grid
/// origin at the top-left, with +Z increasing DOWNWARD on screen and +X
/// increasing rightward (confirmed
/// against an actual in-game screenshot and a direct raw-vertex-vs-HUD-
/// coordinate match — this is a genuine display-convention difference, NOT
/// a coordinate-data bug: exported position/UV data is already correct,
/// see TerrainMeshBuilder.cs's tested winding-order claim). Unity's own
/// default Top-view convention has +Z pointing UP on screen instead.
///
/// A camera looking straight down can only reach 4 possible (screen-right,
/// screen-up) pairings via rotation: (+X,+Z), (+Z,-X), (-X,-Z), (-Z,+X) —
/// none of them is (+X,-Z), since that combination has determinant -1, a
/// genuine reflection. Matching the game's convention therefore needs an
/// actual mirror (a projection-matrix flip), not a Transform change.
///
/// That mirror is applied ONLY around the camera's own render call
/// (SC2GameViewEditorUI's Camera.onPreRender/onPostRender hooks), never
/// held persistently — camera.projectionMatrix is a piece of shared state
/// that cam.ScreenPointToRay/WorldToScreenPoint (every click-to-place,
/// paint brush, mouse-coordinate readout and world-space UI overlay in
/// this editor) also read, and leaving it mirrored for anything beyond the
/// render itself would silently break every one of them. Instead, every
/// call site that turns a mouse position into a world ray (or a world
/// position into a screen/GUI position) routes through
/// <see cref="ScreenPointToRay"/>/<see cref="WorldToGui"/> below, which
/// keep the camera's OWN projection matrix untouched and instead
/// compensate by skipping the usual GUI-vs-Camera Y-axis flip whenever the
/// mirror is active — since the render is now vertically mirrored on
/// screen, that flip and this one cancel out exactly (worked out
/// algebraically, not by trial and error: for GUI space with top-left
/// origin and Camera-space with bottom-left origin, mirroring the render
/// vertically is itself an additional Y flip, and composing two Y flips is
/// the identity).
/// </summary>
public static class SC2TopViewMirror
{
    /// <summary>True while the camera is pinned to the "Top" orientation
    /// (LookAlong's "Top" case, or ResetView) — see SC2GameViewEditorUI for
    /// what sets/clears this. Deliberately NOT derived by checking the
    /// camera's current forward vector every frame: that reactive check
    /// (an earlier, reverted attempt) made the mirror snap on/off mid-drag
    /// any time free orbit swept through (or near) straight-down, which is
    /// disorienting — a deliberate action should own this state, not
    /// incidentally passing through an angle during unrelated camera
    /// manipulation.</summary>
    public static bool Active { get; set; }

    /// <summary>Builds the ray for a GUI-space mouse position (top-left
    /// origin, as reported by Event.mousePosition / IMGUI), replacing every
    /// <c>cam.ScreenPointToRay(new Vector3(x, Screen.height - y, 0))</c>
    /// call site in this codebase. camera.projectionMatrix is never
    /// touched here — only which Y this passes to the real
    /// Camera.ScreenPointToRay changes.</summary>
    public static Ray ScreenPointToRay(Camera cam, Vector2 guiMousePosition)
    {
        var y = Active ? guiMousePosition.y : Screen.height - guiMousePosition.y;
        return cam.ScreenPointToRay(new Vector3(guiMousePosition.x, y, 0f));
    }

    /// <summary>Converts a world position to a GUI-space pixel (top-left
    /// origin), replacing every
    /// <c>var p = cam.WorldToScreenPoint(world); gui = new Vector2(p.x,
    /// Screen.height - p.y);</c> pattern in this codebase. The returned
    /// Vector3's .z is the same camera-space depth WorldToScreenPoint
    /// itself returns (positive = in front of the camera) — callers that
    /// need it (visibility checks) keep reading .z exactly as before.</summary>
    public static Vector3 WorldToGui(Camera cam, Vector3 world)
    {
        var pixel = cam.WorldToScreenPoint(world);
        var y = Active ? pixel.y : Screen.height - pixel.y;
        return new Vector3(pixel.x, y, pixel.z);
    }
}
