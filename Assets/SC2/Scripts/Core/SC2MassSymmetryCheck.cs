using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Whether the Mass markers of a map follow the map's symmetry: a Mass marker placed on a symmetric map has a twin on every point its
/// symmetry maps it to (the same points SC2SymmetryMath.GetSlots gives for the terrain, the Objects tab and the lobby). Pure — world
/// positions in, the twins that are missing out.
/// </summary>
public static class SC2MassSymmetryCheck
{
    /// <summary>A Mass marker whose twin is missing: where the symmetry puts it and which copy of the marker that is.</summary>
    public readonly struct MissingTwin
    {
        public readonly int MarkerId;
        public readonly string MarkerName;
        public readonly Vector3 MarkerPosition;
        public readonly int Slot;
        public readonly Vector3 Expected;

        public MissingTwin(int markerId, string markerName, Vector3 markerPosition, int slot, Vector3 expected)
        {
            MarkerId = markerId;
            MarkerName = markerName;
            MarkerPosition = markerPosition;
            Slot = slot;
            Expected = expected;
        }
    }

    public static bool IsMass(SC2ObjMarker marker) =>
        marker != null && string.Equals(marker.Type, "Mass", StringComparison.OrdinalIgnoreCase);

    /// <summary>Every point the symmetry maps a Mass marker to where no Mass marker stands (within <paramref name="tolerance"/>, in the
    /// ground plane). A marker on the symmetry axis or at the centre is its own twin there. A point two markers both miss is reported
    /// once, for the marker met first. Empty when the map has no symmetry.</summary>
    public static List<MissingTwin> FindMissingTwins(
        IReadOnlyList<SC2ObjMarker> markers, SymmetryMode mode, Vector3 center, int radialCount,
        float tolerance = SC2LobbySpawnMath.DefaultPairTolerance)
    {
        var missing = new List<MissingTwin>();
        if (markers == null || mode == SymmetryMode.None) return missing;

        var masses = new List<SC2ObjMarker>();
        foreach (var marker in markers)
            if (IsMass(marker)) masses.Add(marker);

        foreach (var mass in masses)
        {
            var slots = SC2SymmetryMath.GetSlots(mode, center, radialCount, mass.Position);
            for (var slot = 1; slot < slots.Count; slot++)
            {
                var expected = slots[slot];
                if (HasMassNear(masses, expected, tolerance)) continue;
                var alreadyReported = false;
                foreach (var reported in missing)
                    if (Distance(reported.Expected, expected) <= tolerance) { alreadyReported = true; break; }
                if (!alreadyReported)
                    missing.Add(new MissingTwin(mass.Id, mass.Name, mass.Position, slot, expected));
            }
        }
        return missing;
    }

    private static bool HasMassNear(List<SC2ObjMarker> masses, Vector3 point, float tolerance)
    {
        foreach (var mass in masses)
            if (Distance(mass.Position, point) <= tolerance) return true;
        return false;
    }

    private static float Distance(Vector3 a, Vector3 b)
    {
        var dx = a.x - b.x;
        var dz = a.z - b.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    /// <summary>The symmetry in words, for a message.</summary>
    public static string Describe(SymmetryMode mode, int radialCount)
    {
        switch (mode)
        {
            case SymmetryMode.X: return "mirrored left-right";
            case SymmetryMode.Y: return "mirrored top-bottom";
            case SymmetryMode.XY: return "mirrored both ways";
            case SymmetryMode.Radial: return $"{Mathf.Clamp(radialCount, 2, 8)}-way rotational";
            case SymmetryMode.DiagonalTLBR: return "mirrored across the top-left to bottom-right diagonal";
            case SymmetryMode.DiagonalTRBL: return "mirrored across the top-right to bottom-left diagonal";
            case SymmetryMode.DiagonalBoth: return "mirrored across both diagonals";
            default: return "none";
        }
    }
}
