using UnityEngine;

public class SC2MapRawData : ScriptableObject
{
    public BdfIO.Core.BdfPlatform Platform;

    // ---- hfield: sizes only (the heightmap itself lives in a nearby .raw file) ----
    [Header("hfield")]
    public uint HfieldMapX;
    public uint HfieldMapY;

    // ---- .bytes references to the heavy data ----
    [Header("Raw BDF data (.bytes files)")]
    public TextAsset? TerrainBytes;
    public TextAsset? TerrainOtBytes;
    public uint TerrainNumFiles = 2;
    public byte[] TerrainTrailingData = System.Array.Empty<byte>();

    public TextAsset? InfoBytes;
    public TextAsset? InfoOtBytes;

    public TextAsset? HfieldBytes;
    public TextAsset? HfieldOtBytes;

    public TextAsset? CostsBytes;
    public TextAsset? CostsOtBytes;

    public TextAsset? Collision2Bytes;
    public TextAsset? Collision2OtBytes;

    public TextAsset? DecalsBytes;
    public TextAsset? DecalsOtBytes;
    public uint DecalsNumFiles = 1;

    // ---- lua (scenario and script only — save.lua lives in .bytes) ----
    [Header("lua")]
    public string MapName = "";
    public string ScenarioLua = "";  // lightweight, needed at export time
    public string ScriptLua = "";    // lightweight

    // save.lua lives in SaveLuaBytes (can be large)
    public TextAsset? SaveLuaBytes;

    [Header("Paths from scenario.lua")]
    public string ScenarioMapPath    = "";
    public string ScenarioSavePath   = "";
    public string ScenarioScriptPath = "";

    // ---- Read helpers ----
    public byte[] GetTerrainData()     => TerrainBytes     != null ? TerrainBytes.bytes     : System.Array.Empty<byte>();
    public uint[] GetTerrainOt()       => ReadUintArray(TerrainOtBytes);
    public byte[] GetInfoData()        => InfoBytes         != null ? InfoBytes.bytes         : System.Array.Empty<byte>();
    public uint[] GetInfoOt()          => ReadUintArray(InfoOtBytes);
    public byte[] GetHfieldData()      => HfieldBytes      != null ? HfieldBytes.bytes       : System.Array.Empty<byte>();
    public uint[] GetHfieldOt()        => ReadUintArray(HfieldOtBytes);
    public byte[] GetCostsData()       => CostsBytes       != null ? CostsBytes.bytes        : System.Array.Empty<byte>();
    public uint[] GetCostsOt()         => ReadUintArray(CostsOtBytes);
    public byte[] GetCollision2Data()  => Collision2Bytes  != null ? Collision2Bytes.bytes   : System.Array.Empty<byte>();
    public uint[] GetCollision2Ot()    => ReadUintArray(Collision2OtBytes);
    public byte[] GetDecalsData()      => DecalsBytes      != null ? DecalsBytes.bytes       : System.Array.Empty<byte>();
    public uint[] GetDecalsOt()        => ReadUintArray(DecalsOtBytes);
    public string GetSaveLua()         => SaveLuaBytes     != null ? System.Text.Encoding.UTF8.GetString(SaveLuaBytes.bytes) : "";

    // The heightmap is read from a .raw file (uint16 LE, mapX*mapY values)
    public ushort[] LoadHeightmap(string assetDir)
    {
        var path = System.IO.Path.Combine(Application.dataPath, "..", assetDir, "heightmap.raw");
        if (!System.IO.File.Exists(path)) return System.Array.Empty<ushort>();
        var bytes = System.IO.File.ReadAllBytes(path);
        var result = new ushort[bytes.Length / 2];
        System.Buffer.BlockCopy(bytes, 0, result, 0, bytes.Length);
        return result;
    }

    private static uint[] ReadUintArray(TextAsset? ta)
    {
        if (ta == null) return System.Array.Empty<uint>();
        var b = ta.bytes;
        var result = new uint[b.Length / 4];
        System.Buffer.BlockCopy(b, 0, result, 0, b.Length);
        return result;
    }
}
