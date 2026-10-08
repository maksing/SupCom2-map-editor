using System;
using UnityEngine;

/// <summary>
/// A marker's QUATERNION in a _save.lua is written (x, y, z, w): the identity is QUATERNION( 0, 0, 0, 1 ), and a turn about Y by
/// 26 degrees reads QUATERNION( 0, 0.225079, 0, -0.974341 ) in the game's own maps. Maps saved by earlier versions wrote and read
/// (w, x, y, z) instead, so the stored Euler angles of a marker in an older document were decoded with that wrong order.
/// </summary>
public static class SC2MarkerOrientation
{
    /// <summary>The version of how a marker's orientation is stored in a map document: 2 = Euler angles of the real rotation.
    /// A document without it holds Euler angles decoded as (w, x, y, z).</summary>
    public const int StorageFormat = 2;

    /// <summary>The save.lua text of <paramref name="q"/>: QUATERNION( x, y, z, w ).</summary>
    public static string FormatQuaternion(Quaternion q, Func<float, string> format) =>
        $"QUATERNION( {format(q.x)}, {format(q.y)}, {format(q.z)}, {format(q.w)} )";

    /// <summary>Converts the Euler angles an older document stored for a marker to the real rotation's. A marker still at zero was
    /// placed by the editor, never decoded from a file (a game map never stores QUATERNION( 1, 0, 0, 0 )), so it is the identity as
    /// it should be; any other angles were decoded from a file as (w, x, y, z), so the real quaternion is that one with its components
    /// shifted back by one place.</summary>
    public static Vector3 MigrateLegacyEuler(Vector3 legacyEuler)
    {
        if (NearZero(legacyEuler.x) && NearZero(legacyEuler.y) && NearZero(legacyEuler.z)) return Vector3.zero;
        var decoded = EulerToQuaternion(legacyEuler);
        return QuaternionToEuler(new Quaternion(decoded.w, decoded.x, decoded.y, decoded.z));
    }

    private static bool NearZero(float degrees)
    {
        var wrapped = Mathf.Repeat(degrees + 180f, 360f) - 180f;
        return Mathf.Abs(wrapped) < 0.01f;
    }

    // Unity's Quaternion.Euler / eulerAngles (rotate about Z, then X, then Y), written out so the rules above can be tested outside
    // a running Unity process, where the native versions are not available.

    /// <summary>The same rotation as Quaternion.Euler(<paramref name="degrees"/>).</summary>
    public static Quaternion EulerToQuaternion(Vector3 degrees)
    {
        var hx = degrees.x * Mathf.Deg2Rad * 0.5f; var hy = degrees.y * Mathf.Deg2Rad * 0.5f; var hz = degrees.z * Mathf.Deg2Rad * 0.5f;
        float sx = Mathf.Sin(hx), cx = Mathf.Cos(hx), sy = Mathf.Sin(hy), cy = Mathf.Cos(hy), sz = Mathf.Sin(hz), cz = Mathf.Cos(hz);
        // q = qY * qX * qZ
        return new Quaternion(
            cy * sx * cz + sy * cx * sz,
            sy * cx * cz - cy * sx * sz,
            cy * cx * sz - sy * sx * cz,
            cy * cx * cz + sy * sx * sz);
    }

    /// <summary>Euler angles in [0, 360) that give the same rotation as <paramref name="q"/> (what eulerAngles returns).</summary>
    public static Vector3 QuaternionToEuler(Quaternion q)
    {
        float x = q.x, y = q.y, z = q.z, w = q.w;
        var sinX = -2f * (y * z - x * w); // -R12
        float ex, ey, ez;
        if (Mathf.Abs(sinX) >= 0.999999f)
        {
            // Looking straight up or down: Y and Z turn about the same axis, so all of it goes into Y.
            ex = Mathf.Sign(sinX) * 90f;
            ey = Mathf.Atan2(-2f * (x * z - y * w), 1f - 2f * (y * y + z * z)) * Mathf.Rad2Deg;
            ez = 0f;
        }
        else
        {
            ex = Mathf.Asin(sinX) * Mathf.Rad2Deg;
            ey = Mathf.Atan2(2f * (x * z + y * w), 1f - 2f * (x * x + y * y)) * Mathf.Rad2Deg;
            ez = Mathf.Atan2(2f * (x * y + z * w), 1f - 2f * (x * x + z * z)) * Mathf.Rad2Deg;
        }
        return new Vector3(Mathf.Repeat(ex, 360f), Mathf.Repeat(ey, 360f), Mathf.Repeat(ez, 360f));
    }
}
