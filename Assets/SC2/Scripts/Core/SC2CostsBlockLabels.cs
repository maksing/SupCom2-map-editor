/// <summary>What each Costs block is for, by its place in the block set: the file names (Land_Small_water_Surface_ShoreLine and
/// the like) say nothing about it, so the editor shows the kind of unit the block is made for.</summary>
public static class SC2CostsBlockLabels
{
    private static readonly string[] LandOnly = { "Tanks", "Buildings, engineers", "ACU, experimentals" };
    private static readonly string[] PlayableWater = { "Tanks", "Buildings", "Megaliths", "ACU, Fatboy", "Aeon tanks" };

    /// <summary>The label of block <paramref name="index"/> in a set of <paramref name="count"/> blocks (3 without playable water,
    /// 5 with it); the file name for a set of any other size, which has no known meaning.</summary>
    public static string Role(int index, int count, string fileName)
    {
        var roles = count == LandOnly.Length ? LandOnly : count == PlayableWater.Length ? PlayableWater : null;
        return roles != null && index >= 0 && index < roles.Length ? roles[index] : fileName;
    }

    /// <summary>The label with the file name after it, where there is room: "Tanks (Land_Small_water_Surface_ShoreLine)".</summary>
    public static string WithFileName(int index, int count, string fileName)
    {
        var role = Role(index, count, fileName);
        return role == fileName ? fileName : $"{role} ({fileName})";
    }
}
