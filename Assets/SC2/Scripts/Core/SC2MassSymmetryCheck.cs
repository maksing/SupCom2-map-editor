using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Whether the Mass markers of a map follow the map's symmetry: a Mass marker placed on a symmetric map has a twin on every point its
/// symmetry maps it to (the same points SC2SymmetryMath.GetSlots gives for the terrain, the Objects tab and the lobby). Pure — world
/// positions in, the twins that are missing out. The army start markers are checked the same way (see IsArmyStart).
/// </summary>
public static class SC2MassSymmetryCheck
{
    /// <summary>A marker whose twin is missing: where the symmetry puts it and which copy of the marker that is.</summary>
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

    /// <summary>An army start position: the ARMY_n marker.</summary>
    public static bool IsArmyStart(SC2ObjMarker marker) =>
        marker != null && SC2MarkerDeduplication.IsArmyStartMarker(marker.Name);

    /// <summary>Every point the symmetry maps a Mass marker (or a marker <paramref name="belongs"/> selects) to where no such marker stands (within <paramref name="tolerance"/>, in the
    /// ground plane). A marker on the symmetry axis or at the centre is its own twin there. A point two markers both miss is reported
    /// once, for the marker met first. Empty when the map has no symmetry.</summary>
    public static List<MissingTwin> FindMissingTwins(
        IReadOnlyList<SC2ObjMarker> markers, SymmetryMode mode, Vector3 center, int radialCount,
        float tolerance = SC2LobbySpawnMath.DefaultPairTolerance, Func<SC2ObjMarker, bool> belongs = null)
    {
        var missing = new List<MissingTwin>();
        if (markers == null || mode == SymmetryMode.None) return missing;

        belongs ??= IsMass;
        var masses = new List<SC2ObjMarker>();
        foreach (var marker in markers)
            if (belongs(marker)) masses.Add(marker);

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

    // The symmetries a map without a symmetry setting is measured against.
    private static readonly (SymmetryMode Mode, int RadialCount)[] CandidateSymmetries =
    {
        (SymmetryMode.X, 2), (SymmetryMode.Y, 2), (SymmetryMode.XY, 2),
        (SymmetryMode.DiagonalTLBR, 2), (SymmetryMode.DiagonalTRBL, 2), (SymmetryMode.DiagonalBoth, 2),
        (SymmetryMode.Radial, 2), (SymmetryMode.Radial, 3), (SymmetryMode.Radial, 4),
        (SymmetryMode.Radial, 5), (SymmetryMode.Radial, 6), (SymmetryMode.Radial, 8),
    };

    /// <summary>For a map whose symmetry is None: the Mass (or <paramref name="belongs"/>) markers that no standard symmetry about <paramref name="center"/> pairs with
    /// the other Mass markers, i.e. for every symmetry at least one of the points it maps the marker to has no Mass marker.</summary>
    public static List<SC2ObjMarker> FindUnpairedWithoutSymmetry(
        IReadOnlyList<SC2ObjMarker> markers, Vector3 center, float tolerance = SC2LobbySpawnMath.DefaultPairTolerance,
        Func<SC2ObjMarker, bool> belongs = null)
    {
        var unpaired = new List<SC2ObjMarker>();
        if (markers == null) return unpaired;

        belongs ??= IsMass;
        var masses = new List<SC2ObjMarker>();
        foreach (var marker in markers)
            if (belongs(marker)) masses.Add(marker);

        foreach (var mass in masses)
        {
            var paired = false;
            foreach (var (mode, radialCount) in CandidateSymmetries)
            {
                var slots = SC2SymmetryMath.GetSlots(mode, center, radialCount, mass.Position);
                var complete = true;
                for (var slot = 1; slot < slots.Count && complete; slot++)
                    complete = HasMassNear(masses, slots[slot], tolerance);
                if (!complete) continue;
                paired = true;
                break;
            }
            if (!paired) unpaired.Add(mass);
        }
        return unpaired;
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
