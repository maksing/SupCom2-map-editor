using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

/// <summary>Symmetry slot math shared by the terrain painter, the Objects tab
/// and the lobby spawn editor. Slot 0 is the point itself; the other slots
/// come out in the order every domain already relies on (see
/// SC2TerrainData.GetSymmetricPositions, which delegates here).</summary>
public static class SC2SymmetryMath
{
    /// <summary>True when the copy in <paramref name="slot"/> (see <see cref="GetSlots"/>) is a
    /// reflection of slot 0 rather than a rotation of it. A reflected copy turns the opposite way
    /// when the original is rotated.</summary>
    public static bool SlotReflects(SymmetryMode mode, int slot)
    {
        if (slot <= 0) return false;
        switch (mode)
        {
            case SymmetryMode.X:
            case SymmetryMode.Y:
            case SymmetryMode.DiagonalTLBR:
            case SymmetryMode.DiagonalTRBL:
                return true;
            case SymmetryMode.XY:
            case SymmetryMode.DiagonalBoth:
                return slot == 1 || slot == 2; // slot 3 is the 180-degree rotation
            default:
                return false; // radial copies are rotations
        }
    }

    /// <summary>Whether turning the object in slot <paramref name="from"/> by some angle turns the
    /// object in slot <paramref name="to"/> the opposite way.</summary>
    public static bool TurnsOpposite(SymmetryMode mode, int from, int to) =>
        SlotReflects(mode, from) != SlotReflects(mode, to);

    public static List<Vector3> GetSlots(SymmetryMode mode, Vector3 center, int radialCount, Vector3 pos)
    {
        var c = center; var rel = pos - c;
        var list = new List<Vector3> { pos };
        switch (mode)
        {
            case SymmetryMode.X: list.Add(c + new Vector3(-rel.x, rel.y, rel.z)); break;
            case SymmetryMode.Y: list.Add(c + new Vector3(rel.x, rel.y, -rel.z)); break;
            case SymmetryMode.XY:
                list.Add(c + new Vector3(-rel.x, rel.y, rel.z));
                list.Add(c + new Vector3(rel.x, rel.y, -rel.z));
                list.Add(c + new Vector3(-rel.x, rel.y, -rel.z)); break;
            // Reflect across relX==relZ (top-left/bottom-right) — swaps the
            // two horizontal components, Y untouched same as every other
            // mode here.
            case SymmetryMode.DiagonalTLBR: list.Add(c + new Vector3(rel.z, rel.y, rel.x)); break;
            // Reflect across relX==-relZ (top-right/bottom-left).
            case SymmetryMode.DiagonalTRBL: list.Add(c + new Vector3(-rel.z, rel.y, -rel.x)); break;
            case SymmetryMode.DiagonalBoth:
                list.Add(c + new Vector3(rel.z, rel.y, rel.x));
                list.Add(c + new Vector3(-rel.z, rel.y, -rel.x));
                list.Add(c + new Vector3(-rel.x, rel.y, -rel.z)); break;
            case SymmetryMode.Radial:
                // SC2RadialSymmetryMath.RotationAngleSteps is the shared
                // source of truth for N-way angles (also used by Costs and
                // Objects), so every domain agrees on what "N-way" means.
                var ang = Mathf.Atan2(rel.z, rel.x); var r = new Vector2(rel.x, rel.z).magnitude;
                foreach (var deg in SC2RadialSymmetryMath.RotationAngleSteps(radialCount))
                {
                    var a = ang + deg * Mathf.Deg2Rad;
                    list.Add(c + new Vector3(r * Mathf.Cos(a), rel.y, r * Mathf.Sin(a)));
                }
                break;
        }
        return list;
    }
}

/// <summary>Pure logic behind the lobby spawn editor and the
/// _scenario.lua StartPositions block: which armies mirror each other, and
/// how the block is written. Positions are world units (x, y, z); only x and
/// z matter for placement.</summary>
public static class SC2LobbySpawnMath
{
    /// <summary>Default distance (world units) within which an army counts as
    /// standing on another army's mirrored point. Vanilla positions sit on
    /// cell centres, so mirrored pairs are at most a cell or so apart.</summary>
    public const float DefaultPairTolerance = 3f;

    /// <summary>Orders "ARMY_2" before "ARMY_10"; non-numeric names fall back
    /// to ordinal comparison after the numbered ones.</summary>
    public static int CompareArmyNames(string a, string b)
    {
        var an = TryArmyNumber(a, out var na);
        var bn = TryArmyNumber(b, out var nb);
        if (an && bn) return na.CompareTo(nb);
        if (an != bn) return an ? -1 : 1;
        return string.CompareOrdinal(a, b);
    }

    private static bool TryArmyNumber(string name, out int number)
    {
        number = 0;
        const string prefix = "ARMY_";
        return name != null && name.StartsWith(prefix, StringComparison.Ordinal) &&
               int.TryParse(name.Substring(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out number);
    }

    /// <summary>The armies standing on the mirrored points of
    /// <paramref name="army"/>'s in-game position (the SAME slot order
    /// SC2SymmetryMath.GetSlots produces — the returned Slot is the index
    /// into it). One partner per slot, nearest within tolerance, each army
    /// used at most once. Found from game positions, so it works on imported
    /// maps that have no symmetry groups.</summary>
    public static List<KeyValuePair<string, int>> FindPartners(
        string army, IReadOnlyDictionary<string, Vector3> gamePositions,
        SymmetryMode mode, Vector3 center, int radialCount, float tolerance = DefaultPairTolerance)
    {
        var partners = new List<KeyValuePair<string, int>>();
        if (army == null || gamePositions == null || !gamePositions.TryGetValue(army, out var origin)) return partners;

        var slots = SC2SymmetryMath.GetSlots(mode, center, radialCount, origin);
        var used = new HashSet<string> { army };
        for (var slot = 1; slot < slots.Count; slot++)
        {
            string best = null; var bestDistance = tolerance;
            foreach (var other in gamePositions)
            {
                if (used.Contains(other.Key)) continue;
                var dx = other.Value.x - slots[slot].x; var dz = other.Value.z - slots[slot].z;
                var distance = Mathf.Sqrt(dx * dx + dz * dz);
                if (distance <= bestDistance) { best = other.Key; bestDistance = distance; }
            }
            if (best == null) continue;
            used.Add(best);
            partners.Add(new KeyValuePair<string, int>(best, slot));
        }
        return partners;
    }

    /// <summary>New lobby positions for the partners of a moved army: army
    /// goes to <paramref name="newPosition"/>, each partner to its slot of
    /// that point. A partner keeps its own current Y.</summary>
    public static Dictionary<string, Vector3> MoveWithPartners(
        Vector3 newPosition, IReadOnlyList<KeyValuePair<string, int>> partners,
        IReadOnlyDictionary<string, Vector3> currentLobby,
        SymmetryMode mode, Vector3 center, int radialCount)
    {
        var moved = new Dictionary<string, Vector3>();
        var slots = SC2SymmetryMath.GetSlots(mode, center, radialCount, newPosition);
        foreach (var partner in partners)
        {
            if (partner.Value < 0 || partner.Value >= slots.Count) continue;
            var y = currentLobby != null && currentLobby.TryGetValue(partner.Key, out var current) ? current.y : slots[partner.Value].y;
            moved[partner.Key] = new Vector3(slots[partner.Value].x, y, slots[partner.Value].z);
        }
        return moved;
    }

    /// <summary>The StartPositions block body (the text between the braces)
    /// in the order the armies are given — an army with an entry in
    /// <paramref name="lobbyOverrides"/> is written at that lobby position,
    /// every other at its in-game position. Entries for armies not in
    /// <paramref name="armies"/> are ignored (an army that no longer
    /// exists is not written).</summary>
    public static string BuildStartPositionsBody(
        IEnumerable<KeyValuePair<string, Vector3>> armies, IReadOnlyDictionary<string, Vector3> lobbyOverrides)
    {
        var sb = new StringBuilder();
        sb.Append("\r\n");
        foreach (var army in armies)
        {
            var position = lobbyOverrides != null && lobbyOverrides.TryGetValue(army.Key, out var lobby) ? lobby : army.Value;
            sb.Append("        ['").Append(army.Key).Append("'] = { ")
              .Append(Fmt(position.x)).Append(", ").Append(Fmt(position.y)).Append(", ").Append(Fmt(position.z))
              .Append(" },\r\n");
        }
        sb.Append("    ");
        return sb.ToString();
    }

    private static string Fmt(float v) => v.ToString("0.######", CultureInfo.InvariantCulture);
}
