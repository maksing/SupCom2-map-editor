using System;
using System.Collections.Generic;

/// <summary>
/// Which object of the map casts shadows onto which in the lighting bake. An "object" is the core terrain, the background terrain
/// or one imported terrain.bdf. Everything shadows everything by default; the rules are stored as the pairs that are switched off
/// (a caster that must not shadow a receiver), so an empty list is the default and old maps need no migration. Pure, so the rules
/// can be tested.
/// </summary>
public static class SC2ShadowCasting
{
    public const string CoreId = "core";
    public const string BackgroundId = "background";

    private const char Separator = '>';

    /// <summary>The stored form of "<paramref name="casterId"/> does not shadow <paramref name="receiverId"/>".</summary>
    public static string Key(string casterId, string receiverId) => casterId + Separator + receiverId;

    /// <summary>True when <paramref name="casterId"/> shadows <paramref name="receiverId"/> (the same id means the object's own
    /// shadow on itself).</summary>
    public static bool Casts(ICollection<string> exclusions, string casterId, string receiverId) =>
        exclusions == null || !exclusions.Contains(Key(casterId, receiverId));

    /// <summary>Switches one caster/receiver pair on or off in <paramref name="exclusions"/>. Returns whether the list changed.</summary>
    public static bool SetCasts(List<string> exclusions, string casterId, string receiverId, bool casts)
    {
        var key = Key(casterId, receiverId);
        if (casts) return exclusions.RemoveAll(entry => entry == key) > 0;
        if (exclusions.Contains(key)) return false;
        exclusions.Add(key);
        return true;
    }

    /// <summary>Switches a caster on or off for every receiver at once. Returns whether the list changed.</summary>
    public static bool SetCastsOnAll(List<string> exclusions, string casterId, IEnumerable<string> receiverIds, bool casts)
    {
        var changed = false;
        foreach (var receiverId in receiverIds) changed |= SetCasts(exclusions, casterId, receiverId, casts);
        return changed;
    }

    /// <summary>The casters that shadow <paramref name="receiverId"/>, in the order of <paramref name="casterIds"/>.</summary>
    public static List<string> CastersOnto(IEnumerable<string> casterIds, ICollection<string> exclusions, string receiverId)
    {
        var result = new List<string>();
        foreach (var casterId in casterIds)
            if (Casts(exclusions, casterId, receiverId)) result.Add(casterId);
        return result;
    }

    /// <summary>One string for a set of casters, so receivers that are shadowed by the same set can share one height map.</summary>
    public static string SetKey(IEnumerable<string> casterIds)
    {
        var sorted = new List<string>(casterIds);
        sorted.Sort(StringComparer.Ordinal);
        return string.Join("|", sorted);
    }
}
