using UnityEngine;

/// <summary>
/// The press-drag-release gesture of a stamp brush, like Blender's anchored stroke: pressing anchors the stamp on the ground, dragging
/// away from the anchor sets how big it is (the distance) and which way it points (the direction), and releasing places it once. Pure,
/// so the rules can be tested; the brush modes own the placement itself.
///
/// <see cref="AngleRad"/> is the world angle of the stamp's own +X axis, counter-clockwise seen from above (0 = along world +X, pi/2 =
/// along world +Z). The brush shader and the sculpt sampler turn the stamp content by the opposite angle, see <see cref="BrushRotation"/>.
/// </summary>
public sealed class SC2StampGesture
{
    public bool Active { get; private set; }
    public Vector3 Anchor { get; private set; }
    public float Radius { get; private set; }
    public float AngleRad { get; private set; }
    /// <summary>True once the mouse has moved past the dead zone, i.e. the size and angle come from the drag, not from the defaults.</summary>
    public bool Dragged { get; private set; }

    private float _baseRadius;
    private float _baseAngle;

    /// <summary>Starts a gesture anchored at <paramref name="anchor"/>. A press that is released without being dragged places the stamp
    /// at <paramref name="baseRadius"/> and <paramref name="baseAngleRad"/>.</summary>
    public void Begin(Vector3 anchor, float baseRadius, float baseAngleRad)
    {
        Active = true;
        Anchor = anchor;
        _baseRadius = baseRadius;
        _baseAngle = baseAngleRad;
        Radius = baseRadius;
        AngleRad = baseAngleRad;
        Dragged = false;
    }

    /// <summary>Follows the mouse, which is on the ground at <paramref name="ground"/>. Inside the dead zone nothing changes, so a click
    /// does not shrink the stamp to nothing. Beyond it, <paramref name="sizeFromDrag"/> makes the distance the radius and
    /// <paramref name="angleFromDrag"/> adds the direction of the drag to the base angle, snapped to <paramref name="snapStepRad"/>
    /// when that is positive.</summary>
    public void Drag(Vector3 ground, bool sizeFromDrag, bool angleFromDrag, float deadZone, float minRadius, float maxRadius, float snapStepRad = 0f)
    {
        if (!Active) return;
        var dx = ground.x - Anchor.x;
        var dz = ground.z - Anchor.z;
        var distance = Mathf.Sqrt(dx * dx + dz * dz);
        if (distance < deadZone && !Dragged) return;
        Dragged = true;
        Radius = sizeFromDrag ? Mathf.Clamp(distance, minRadius, maxRadius) : _baseRadius;
        if (angleFromDrag && distance > 1e-4f)
        {
            var angle = Mathf.Atan2(dz, dx);
            if (snapStepRad > 0f) angle = Mathf.Round(angle / snapStepRad) * snapStepRad;
            AngleRad = _baseAngle + angle;
        }
        else AngleRad = _baseAngle;
    }

    public void Cancel() => Active = false;

    /// <summary>The rotation the brush shader and the sculpt sampler take for a stamp whose +X axis points along
    /// <paramref name="angleRad"/> in the world: they rotate the sample offset, which turns the content the opposite way.</summary>
    public static float BrushRotation(float angleRad) => -angleRad;
}
