using UnityEngine;

/// <summary>The world axis a move gesture (G, then X / Y / Z, as in Blender) is limited to.</summary>
public enum SC2MoveAxis { None, X, Y, Z }

public static class SC2AxisMove
{
    public static Vector3 Direction(SC2MoveAxis axis) => axis switch
    {
        SC2MoveAxis.X => new Vector3(1f, 0f, 0f),
        SC2MoveAxis.Y => new Vector3(0f, 1f, 0f),
        SC2MoveAxis.Z => new Vector3(0f, 0f, 1f),
        _ => Vector3.zero,
    };

    /// <summary>The constraint after <paramref name="pressed"/> is pressed while <paramref name="current"/> is active: a new axis
    /// limits the move to it, the same axis again drops the limit.</summary>
    public static SC2MoveAxis Press(SC2MoveAxis current, SC2MoveAxis pressed) =>
        pressed == SC2MoveAxis.None ? current : pressed == current ? SC2MoveAxis.None : pressed;

    /// <summary>Where along the axis (the line through <paramref name="axisOrigin"/> in direction <paramref name="axisDirection"/>) the
    /// mouse ray passes closest to it, as a distance from the axis origin in world units. That is how a mouse movement becomes a move
    /// along an axis: the difference of this value between two mouse positions. False when the ray runs parallel to the axis (the
    /// view looks straight along it), where the mouse cannot say how far along the axis it is.</summary>
    public static bool TryParam(Vector3 axisOrigin, Vector3 axisDirection, Vector3 rayOrigin, Vector3 rayDirection, out float distance)
    {
        distance = 0f;
        var d = axisDirection.normalized;
        var v = rayDirection.normalized;
        var w = axisOrigin - rayOrigin;
        var b = Vector3.Dot(d, v);
        var denominator = 1f - b * b; // |d| = |v| = 1
        if (denominator < 1e-5f) return false;
        var dw = Vector3.Dot(d, w);
        var vw = Vector3.Dot(v, w);
        distance = (b * vw - dw) / denominator;
        return true;
    }

    public static string Describe(SC2MoveAxis axis) => axis switch
    {
        SC2MoveAxis.X => "along X",
        SC2MoveAxis.Y => "along Y",
        SC2MoveAxis.Z => "along Z",
        _ => "",
    };
}
