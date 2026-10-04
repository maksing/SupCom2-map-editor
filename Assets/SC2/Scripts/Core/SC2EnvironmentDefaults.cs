/// <summary>
/// The Environment (info.bdf) a new map starts with: the header and the sky texture of the real vanilla map Marine_Barrier
/// (parsed via BdfIO.Formats.Info.InfoBdf.Parse), not invented. Two header fields are set on top of that file's values:
/// WaterEnabled is 1 (it already is in the source) and WaterElevation is 0 (the source has 96). Every other byte is the source
/// file's own value, unmodified. Of its 5 texture-path strings only the sky is set (<see cref="SkyMap"/>); the rest were empty,
/// so SC2TerrainData's "" field initializers already match.
/// </summary>
public static class SC2EnvironmentDefaults
{
    /// <summary>The SkyMap texture path a new map starts with.</summary>
    public const string SkyMap = "/textures/environment/I02_sky.dds";

    public static byte[] CloneDefaultHeader() => (byte[])DefaultHeader.Clone();

    private static readonly byte[] DefaultHeader =
    {
        17, 0, 0, 0, 201, 118, 254, 62, 0, 0, 0, 63, 6, 129, 53, 63,
        0, 0, 128, 63, 0, 0, 128, 63, 0, 0, 128, 63, 102, 102, 166, 63,
        153, 152, 152, 62, 153, 152, 152, 62, 153, 152, 152, 62, 0, 0, 128, 63,
        1, 0, 0, 0, 10, 215, 163, 59, 111, 18, 131, 58, 111, 18, 131, 58,
        111, 18, 131, 186, 10, 215, 163, 59, 111, 18, 131, 58, 111, 18, 131, 58,
        111, 18, 131, 186, 111, 18, 131, 186, 111, 18, 131, 186, 10, 215, 163, 187,
        111, 18, 131, 58, 205, 204, 204, 61, 205, 204, 204, 61, 205, 204, 76, 62,
        0, 0, 128, 63, 0, 0, 22, 67, 0, 0, 22, 67, 0, 0, 22, 67,
        0, 0, 22, 67, 0, 0, 22, 67, 0, 0, 22, 67, 115, 104, 145, 61,
        205, 204, 76, 63, 205, 204, 76, 62, 0, 0, 160, 64, 133, 187, 243, 67,
        0, 0, 0, 0, 0, 0, 240, 65, 205, 204, 76, 62, 246, 40, 124, 64,
        246, 40, 124, 64, 10, 215, 35, 60, 0, 0, 32, 65, 0, 0, 160, 63,
        0, 0, 128, 63, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 128, 68,
        0, 0, 128, 68, 0, 0, 160, 64, 0, 0, 0, 0, 0, 0, 128, 191,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        252, 1, 0, 0, 0, 2, 0, 0, 252, 1, 0, 0, 252, 1, 0, 0,
        252, 1, 0, 0, 0, 0, 128, 63, 0, 0, 128, 63, 0, 0, 128, 63,
        0, 0, 128, 63, 0, 0, 128, 63, 0, 0, 128, 63, 0, 0, 128, 63,
        0, 0, 128, 63, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 128, 84, 67,
        0, 0, 48, 65, 0, 128, 99, 67, 0, 192, 25, 68, 0, 0, 80, 66,
        0, 192, 20, 68, 1, 0, 0, 0, 51, 51, 179, 63, 102, 102, 230, 63,
        0, 0, 128, 63, 1, 0, 0, 0, 0, 0, 128, 63, 0, 0, 128, 63,
        0, 0, 0, 0, 0, 0, 0, 0, 4, 0, 0, 0, 31, 133, 27, 64,
        205, 204, 76, 63, 51, 51, 51, 63, 123, 20, 110, 63, 0, 0, 0, 64,
        3, 0, 0, 0, 205, 204, 204, 61, 0, 0, 0, 68, 0, 0, 0, 196,
        0, 0, 32, 65, 0, 0, 0, 68, 0, 0, 0, 0, 0, 0, 32, 65,
        0, 0, 0, 0, 0, 0, 122, 67, 0, 128, 187, 68, 0, 0, 0, 0,
        0, 0, 122, 67, 0, 0, 128, 63, 0, 0, 128, 62, 0, 0, 0, 0,
        1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 0, 0, 160, 65,
        0, 0, 128, 63, 0, 0, 0, 0, 154, 153, 153, 62
    };
}
