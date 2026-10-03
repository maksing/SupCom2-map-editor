using System;
using System.Globalization;
using UnityEngine;

/// <summary>The axis a rotate gesture is limited to (R, then X / Y / Z, like Blender). None = the gesture's default turn about the
/// object's up axis.</summary>
public enum SC2RotationAxis { None, X, Y, Z }

/// <summary>
/// Rules of the interactive rotate gesture (R, then optionally X / Y / Z and a typed angle): which axis is active after a key press,
/// the rotation an angle gives about it, which way the mouse should turn it, and the typed number. Pure, so it can be tested.
/// </summary>
public static class SC2AxisRotation
{
    /// <summary>The constraint after <paramref name="pressed"/> is pressed while <paramref name="axis"/>/<paramref name="local"/> is
    /// active: a new axis is global; the same axis again turns it local; a third press drops the constraint.</summary>
    public static (SC2RotationAxis Axis, bool Local) Press(SC2RotationAxis axis, bool local, SC2RotationAxis pressed)
    {
        if (pressed == SC2RotationAxis.None) return (axis, local);
        if (pressed != axis) return (pressed, false);
        return local ? (SC2RotationAxis.None, false) : (axis, true);
    }

    public static Vector3 Direction(SC2RotationAxis axis) => axis switch
    {
        SC2RotationAxis.X => Vector3.right,
        SC2RotationAxis.Y => Vector3.up,
        SC2RotationAxis.Z => Vector3.forward,
        _ => Vector3.up,
    };

    /// <summary>The turn of <paramref name="degrees"/> about <paramref name="axis"/> (Unity's Quaternion.AngleAxis, written out so the rules
    /// do not depend on the engine's native code and can be tested).</summary>
    public static Quaternion AngleAxis(float degrees, Vector3 axis)
    {
        var half = degrees * Mathf.Deg2Rad * 0.5f;
        var sin = Mathf.Sin(half);
        axis.Normalize();
        return new Quaternion(axis.x * sin, axis.y * sin, axis.z * sin, Mathf.Cos(half));
    }

    /// <summary><paramref name="original"/> turned by <paramref name="degrees"/>. Without an axis: about its own up axis (the
    /// gesture's default). A global axis turns about the world X / Y / Z, a local one about the object's own.</summary>
    public static Quaternion Apply(Quaternion original, SC2RotationAxis axis, bool local, float degrees)
    {
        if (axis == SC2RotationAxis.None) return original * AngleAxis(degrees, Vector3.up);
        var direction = Direction(axis);
        return local ? original * AngleAxis(degrees, direction) : AngleAxis(degrees, direction) * original;
    }

    /// <summary>+1 when the axis points towards the viewer, -1 when away from it: a positive turn about an axis pointing at the
    /// viewer is the one the mouse circling the pivot the same way produces, so the mouse angle is multiplied by this for an
    /// axis that is not the default one.</summary>
    public static float ViewSign(SC2RotationAxis axis, bool local, Quaternion original, Vector3 cameraForward)
    {
        if (axis == SC2RotationAxis.None) return 1f;
        var direction = local ? original * Direction(axis) : Direction(axis);
        return Vector3.Dot(direction, -cameraForward) < 0f ? -1f : 1f;
    }

    /// <summary>The angle typed so far ("15", "-90", "22.5"); false while it is empty or not a number yet.</summary>
    public static bool TryParseTyped(string typed, out float degrees)
    {
        degrees = 0f;
        if (string.IsNullOrEmpty(typed)) return false;
        return float.TryParse(typed, NumberStyles.Float, CultureInfo.InvariantCulture, out degrees);
    }

    /// <summary>Appends a typed character if it belongs to a number; "-" at the start toggles the sign like Blender's minus key.</summary>
    public static string AppendTyped(string typed, char c)
    {
        typed ??= "";
        if (c == '-') return typed.StartsWith("-", StringComparison.Ordinal) ? typed.Substring(1) : "-" + typed;
        if (c == '.' || c == ',') return typed.Contains('.') ? typed : typed + ".";
        if (c >= '0' && c <= '9') return typed + c;
        return typed;
    }

    public static string DropLastTyped(string typed) => string.IsNullOrEmpty(typed) ? "" : typed.Substring(0, typed.Length - 1);

    public static string Describe(SC2RotationAxis axis, bool local) => axis == SC2RotationAxis.None ? "" : axis + (local ? " (local)" : " (global)");
}
