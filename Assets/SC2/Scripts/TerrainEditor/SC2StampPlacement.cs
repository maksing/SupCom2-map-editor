using System;
using UnityEditor;
using UnityEngine;

/// <summary>
/// The "place as stamp" mode of a custom brush, shared by Paint Blend and Geometry: instead of dabbing along the mouse path the brush lays
/// the stamp down once. Press anchors it, dragging sets its size (the distance from the anchor) and angle (the direction), the brush
/// Strength is the alpha it is placed with, and releasing places it. The owner mode applies the stamp itself (it replays an ordinary first
/// dab at the anchor with the chosen size and angle, so symmetry, undo, locks and the background all behave as for a normal dab).
/// </summary>
public sealed class SC2StampPlacement
{
    public const float MinRadius = 1f;
    public const float MaxRadius = 1000f;
    /// <summary>Holding Shift while dragging snaps the angle to this step.</summary>
    public const float SnapStepRad = Mathf.PI / 12f;

    public readonly SC2StampGesture Gesture = new SC2StampGesture();

    public bool Enabled;
    public bool DragSetsSize = true;
    public bool DragSetsAngle = true;
    public float AngleOffsetDegrees;

    /// <summary>True while the owner mode replays the placed stamp as a dab; it must then use <see cref="AppliedRotation"/> as the stamp
    /// rotation and not roll a random one.</summary>
    public bool Applying { get; private set; }
    /// <summary>The brush rotation (shader / sampler convention) of the stamp being applied.</summary>
    public float AppliedRotation { get; private set; }

    public bool Active => Gesture.Active;

    /// <summary>Handles the press-drag-release of the stamp. Returns true when the event was consumed (the caller returns). On release
    /// <paramref name="place"/> is called with the anchor, radius and angle of the stamp, which the caller applies by replaying a dab.</summary>
    public bool HandleEvent(Event e, Vector3 ground, float baseRadius, float cellSize, bool randomizeAngle, Action<Vector3, float, float> place)
    {
        var left = e.button == 0;
        if (e.type == EventType.MouseDown && left && !e.alt)
        {
            var baseAngle = randomizeAngle ? UnityEngine.Random.Range(0f, Mathf.PI * 2f) : AngleOffsetDegrees * Mathf.Deg2Rad;
            Gesture.Begin(ground, baseRadius, baseAngle);
            e.Use();
            return true;
        }
        if (!Gesture.Active) return false;
        if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
        {
            Gesture.Cancel();
            SC2StampPreview.Hide();
            e.Use();
            return true;
        }
        if (e.type == EventType.MouseDrag && left)
        {
            Gesture.Drag(ground, DragSetsSize, DragSetsAngle, Mathf.Max(cellSize * 2f, 3f), MinRadius, MaxRadius, e.shift ? SnapStepRad : 0f);
            e.Use();
            return true;
        }
        if (e.type == EventType.MouseUp && left)
        {
            var anchor = Gesture.Anchor; var radius = Gesture.Radius; var angle = Gesture.AngleRad;
            Gesture.Cancel();
            SC2StampPreview.Hide();
            place(anchor, radius, angle);
            return true;
        }
        return false;
    }

    /// <summary>Runs <paramref name="apply"/> with <see cref="Applying"/> set and the rotation of the placed stamp available.</summary>
    public void Replay(float angleRad, Action apply)
    {
        AppliedRotation = SC2StampGesture.BrushRotation(angleRad);
        Applying = true;
        try { apply(); }
        finally { Applying = false; }
    }

    /// <summary>A synthetic left press carrying the modifiers of <paramref name="source"/>, used to replay the placed stamp through the
    /// mode's ordinary stroke-start-and-first-dab path.</summary>
    public static Event SyntheticPress(Event source) =>
        new Event { type = EventType.MouseDown, button = 0, modifiers = source.modifiers, mousePosition = source.mousePosition };

    /// <summary>The panel controls. Call while the custom brush is selected.</summary>
    public void DrawOptions(string keySuffix)
    {
        Enabled = EditorGUILayout.Toggle("Place as stamp", Enabled);
        if (!Enabled) return;
        DragSetsSize = EditorGUILayout.Toggle("Drag sets size", DragSetsSize);
        DragSetsAngle = EditorGUILayout.Toggle("Drag sets angle", DragSetsAngle);
        AngleOffsetDegrees = SC2TerrainShell.DrawCompactSlider("Angle (deg)", AngleOffsetDegrees, 0f, 360f, "stamp-angle-" + keySuffix);
        SC2TerrainShell.DrawUnifiedNote(
            "Press to anchor the stamp, drag to set its size (distance) and angle (direction), release to place it. " +
            "Shift while dragging snaps the angle to 15 degrees; Esc cancels. Strength is the alpha the stamp is placed with: " +
            $"{SC2Hotkeys.Label("Brush.StrengthScrollModifier")}+Scroll changes it while you drag.");
    }

    /// <summary>The preview text while placing: size, angle and alpha.</summary>
    public string Readout(float alpha) =>
        $"Size {Gesture.Radius:0}  Angle {Mathf.Repeat(Gesture.AngleRad * Mathf.Rad2Deg, 360f):0} deg  Alpha {alpha:0.00}";
}
