/// <summary>
/// Real vanilla Environment defaults, sourced from a real map's own info.bdf
/// (parsed via BdfIO.Formats.Info.InfoBdf.Parse), not invented. Two fields
/// are patched from that file's own raw values: WaterElevation/AirElevation
/// are forced to 0 (both were already 0 in the source, so this is a no-op,
/// kept only as a documented invariant), and WaterEnabled is forced to true
/// regardless of what the source file had (it had it false). Every other
/// byte is the source file's own value, unmodified. Its 5 texture-path
/// strings were all empty, so no separate string-default table is needed —
/// SC2TerrainData's existing "" field initializers already match.
/// </summary>
public static class SC2EnvironmentDefaults
{
    public static byte[] CloneDefaultHeader() => (byte[])DefaultHeader.Clone();

    private static readonly byte[] DefaultHeader = { 17, 0, 0, 0, 160, 83, 254, 62, 0, 0, 0, 63, 170, 155, 53, 63, 233, 232, 104, 63, 220, 219, 91, 63, 
        177, 176, 48, 63, 0, 0, 128, 63, 165, 164, 164, 62, 219, 218, 218, 62, 133, 132, 4, 63, 0, 0, 128, 63, 
        1, 0, 0, 0, 10, 215, 163, 59, 111, 18, 131, 58, 111, 18, 131, 58, 111, 18, 131, 186, 10, 215, 163, 59, 
        111, 18, 131, 58, 111, 18, 131, 58, 111, 18, 131, 186, 111, 18, 131, 186, 111, 18, 131, 186, 10, 215, 163, 187, 
        111, 18, 131, 58, 185, 184, 56, 62, 140, 139, 11, 63, 175, 174, 174, 62, 0, 0, 128, 63, 0, 0, 22, 67, 
        0, 0, 22, 67, 0, 0, 22, 67, 0, 0, 22, 67, 0, 0, 22, 67, 0, 0, 22, 67, 205, 204, 76, 63, 
        205, 204, 76, 63, 205, 204, 76, 62, 0, 0, 160, 64, 0, 0, 72, 67, 0, 0, 192, 63, 0, 0, 240, 65, 
        205, 204, 76, 62, 246, 40, 124, 64, 0, 0, 32, 65, 10, 215, 35, 60, 0, 0, 32, 65, 0, 0, 160, 63, 
        0, 0, 128, 63, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 128, 68, 0, 0, 128, 68, 0, 0, 160, 64, 
        0, 0, 0, 0, 0, 0, 128, 191, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 
        252, 1, 0, 0, 0, 2, 0, 0, 252, 1, 0, 0, 252, 1, 0, 0, 252, 1, 0, 0, 0, 0, 128, 63, 
        0, 0, 128, 63, 0, 0, 128, 63, 0, 0, 128, 63, 0, 0, 128, 63, 0, 0, 128, 63, 0, 0, 128, 63, 
        0, 0, 128, 63, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 128, 84, 67, 
        0, 0, 48, 65, 0, 128, 99, 67, 0, 192, 25, 68, 0, 0, 80, 66, 0, 192, 20, 68, 1, 0, 0, 0, 
        51, 51, 179, 63, 102, 102, 230, 63, 0, 0, 128, 63, 1, 0, 0, 0, 0, 0, 128, 63, 0, 0, 128, 63, 
        0, 0, 0, 0, 0, 0, 0, 0, 4, 0, 0, 0, 31, 133, 27, 64, 205, 204, 76, 63, 51, 51, 51, 63, 
        123, 20, 110, 63, 0, 0, 0, 64, 3, 0, 0, 0, 205, 204, 204, 61, 0, 0, 0, 68, 0, 0, 0, 196, 
        0, 0, 32, 65, 0, 0, 0, 68, 0, 0, 0, 0, 0, 0, 32, 65, 0, 0, 0, 0, 0, 0, 122, 67, 
        0, 128, 187, 68, 0, 0, 0, 0, 0, 0, 122, 67, 0, 0, 128, 63, 0, 0, 128, 62, 0, 0, 0, 0, 
        1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 0, 0, 160, 65, 0, 0, 128, 63, 0, 0, 0, 0, 
        154, 153, 153, 62 };
}
