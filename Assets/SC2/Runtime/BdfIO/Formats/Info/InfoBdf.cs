using System;
using System.Buffers.Binary;
using BdfIO.Core;

namespace BdfIO.Formats.Info
{
/// <summary>
/// info.bdf: EnvironmentHeader (508 bytes) + a string block (5 texture paths).
///
/// Field names and offsets follow the real EnvironmentHeader structure
/// exactly. Fields without confirmed semantics are named Unknown&lt;N&gt; —
/// with the same number N used in that reference, without renaming or
/// guessing at their purpose.
/// </summary>
public sealed class InfoModel
{
    public byte[] Header { get; } = new byte[508];
    public BdfPlatform Platform { get; set; } = BdfPlatform.Pc;

    // ---- String paths (OffsetTable[0..4], pointers at @240..256) ----
    public string EnvironmentMap         = "";  // @240 EnvironmentMapPtr
    public string SkyMap                 = "";  // @244 SkyMapPtr
    public string ReservedTexturePath    = "";  // @248 — confirmed empty across the 56-map PC corpus
    public string ColorCorrectionCube    = "";  // @252 ColorCorrectionCubePtr
    public string FogOfWarCorrectionCube = "";  // @256 FogOfWarCorrectionCubePtr

    // ================================================================
    // EnvironmentHeader fields — names and offsets 1:1 with the real structure
    // ================================================================

    public int   Version          { get => RF_I(0);   set => WF_I(0,   value); } // @0

    public float SunDirectionX    { get => RF_F(4);   set => WF_F(4,   value); } // @4  SunDirection[0]
    public float SunDirectionY    { get => RF_F(8);   set => WF_F(8,   value); } // @8  SunDirection[1]
    public float SunDirectionZ    { get => RF_F(12);  set => WF_F(12,  value); } // @12 SunDirection[2]
    public float SunColorR        { get => RF_F(16);  set => WF_F(16,  value); } // @16 SunColor[0]
    public float SunColorG        { get => RF_F(20);  set => WF_F(20,  value); } // @20 SunColor[1]
    public float SunColorB        { get => RF_F(24);  set => WF_F(24,  value); } // @24 SunColor[2]
    public float SunIntensity     { get => RF_F(28);  set => WF_F(28,  value); } // @28

    public float ShadowColorR     { get => RF_F(32);  set => WF_F(32,  value); } // @32 ShadowColor[0]
    public float ShadowColorG     { get => RF_F(36);  set => WF_F(36,  value); } // @36 ShadowColor[1]
    public float ShadowColorB     { get => RF_F(40);  set => WF_F(40,  value); } // @40 ShadowColor[2]
    public float ShadowColorA     { get => RF_F(44);  set => WF_F(44,  value); } // @44 ShadowColor[3]

    public int   WaterEnabled     { get => RF_I(48);  set => WF_I(48,  value); } // @48

    public float WaterDiffuse1MovementX { get => RF_F(52);  set => WF_F(52,  value); } // @52  WaterDiffuse1Movement[0]
    public float WaterDiffuse1MovementY { get => RF_F(56);  set => WF_F(56,  value); } // @56  WaterDiffuse1Movement[1]
    public float WaterDiffuse2MovementX { get => RF_F(60);  set => WF_F(60,  value); } // @60  WaterDiffuse2Movement[0]
    public float WaterDiffuse2MovementY { get => RF_F(64);  set => WF_F(64,  value); } // @64  WaterDiffuse2Movement[1]
    public float WaterNormal1MovementX { get => RF_F(68);  set => WF_F(68,  value); } // @68  WaterNormal1Movement[0]
    public float WaterNormal1MovementY { get => RF_F(72);  set => WF_F(72,  value); } // @72  WaterNormal1Movement[1]
    public float WaterNormal2MovementX { get => RF_F(76);  set => WF_F(76,  value); } // @76  WaterNormal2Movement[0]
    public float WaterNormal2MovementY { get => RF_F(80);  set => WF_F(80,  value); } // @80  WaterNormal2Movement[1]
    public float WaterNormal3MovementX { get => RF_F(84);  set => WF_F(84,  value); } // @84  WaterNormal3Movement[0]
    public float WaterNormal3MovementY { get => RF_F(88);  set => WF_F(88,  value); } // @88  WaterNormal3Movement[1]
    public float WaterNormal4MovementX { get => RF_F(92);  set => WF_F(92,  value); } // @92  WaterNormal4Movement[0]
    public float WaterNormal4MovementY { get => RF_F(96);  set => WF_F(96,  value); } // @96  WaterNormal4Movement[1]

    public float WaterColorR      { get => RF_F(100); set => WF_F(100, value); } // @100 WaterColor[0]
    public float WaterColorG      { get => RF_F(104); set => WF_F(104, value); } // @104 WaterColor[1]
    public float WaterColorB      { get => RF_F(108); set => WF_F(108, value); } // @108 WaterColor[2]
    public float WaterColorA      { get => RF_F(112); set => WF_F(112, value); } // @112 WaterColor[3]

    public float WaterFoamRepeatRateX   { get => RF_F(116); set => WF_F(116, value); } // @116 WaterFoamRepeatRate[0]
    public float WaterFoamRepeatRateY   { get => RF_F(120); set => WF_F(120, value); } // @120 WaterFoamRepeatRate[1]
    public float WaterNormalRepeatRateX { get => RF_F(124); set => WF_F(124, value); } // @124 WaterNormalRepeatRate[0]
    public float WaterNormalRepeatRateY { get => RF_F(128); set => WF_F(128, value); } // @128 WaterNormalRepeatRate[1]
    public float WaterNormalRepeatRateZ { get => RF_F(132); set => WF_F(132, value); } // @132 WaterNormalRepeatRate[2]
    public float WaterNormalRepeatRateW { get => RF_F(136); set => WF_F(136, value); } // @136 WaterNormalRepeatRate[3]

    public float WaterRefractionScale       { get => RF_F(140); set => WF_F(140, value); } // @140
    public float WaterReflectionScale       { get => RF_F(144); set => WF_F(144, value); } // @144
    public float WaterFresnelBias           { get => RF_F(148); set => WF_F(148, value); } // @148
    public float WaterFresnelPower          { get => RF_F(152); set => WF_F(152, value); } // @152
    public float WaterDepthScale            { get => RF_F(156); set => WF_F(156, value); } // @156
    public float WaterChromaticScale        { get => RF_F(160); set => WF_F(160, value); } // @160
    public float WaterSpecPower             { get => RF_F(164); set => WF_F(164, value); } // @164
    public float WaterBloomFactor           { get => RF_F(168); set => WF_F(168, value); } // @168
    public float WaterWhiteCapCutoff        { get => RF_F(172); set => WF_F(172, value); } // @172
    public float WaterWhiteCapBrightness    { get => RF_F(176); set => WF_F(176, value); } // @176
    public float WaterWaveTiming            { get => RF_F(180); set => WF_F(180, value); } // @180
    public float WaterWaveModulus           { get => RF_F(184); set => WF_F(184, value); } // @184
    public float WaterFoamHeightOffset      { get => RF_F(188); set => WF_F(188, value); } // @188
    public float WaterWaveStartDepth        { get => RF_F(192); set => WF_F(192, value); } // @192
    public float WaterDepthOriginX          { get => RF_F(196); set => WF_F(196, value); } // @196
    public float WaterDepthOriginY          { get => RF_F(200); set => WF_F(200, value); } // @200
    public float WaterDepthBoundsSizeX      { get => RF_F(204); set => WF_F(204, value); } // @204 WaterDepthBoundsSize[0]
    public float WaterDepthBoundsSizeY      { get => RF_F(208); set => WF_F(208, value); } // @208 WaterDepthBoundsSize[1]
    public float MaxWaterDepth              { get => RF_F(212); set => WF_F(212, value); } // @212
    public float WaterElevation             { get => RF_F(216); set => WF_F(216, value); } // @216
    public float WaterRenderElevationOverride{ get => RF_F(220); set => WF_F(220, value); } // @220

    public float WaterAuxParameter0 { get => RF_F(224); set => WF_F(224, value); } // @224
    public float WaterAuxParameter1 { get => RF_F(228); set => WF_F(228, value); } // @228

    public float AirElevation     { get => RF_F(232); set => WF_F(232, value); } // @232

    public float ReservedZero236  { get => RF_F(236); set => WF_F(236, value); } // @236 — reserved, zero on all 56 PC maps

    // @240..256 — string pointers, see EnvironmentMap/SkyMap/.../FogOfWarCorrectionCube above

    public float CoreFogColorR    { get => RF_F(260); set => WF_F(260, value); } // @260 CoreFogColor[0]
    public float CoreFogColorG    { get => RF_F(264); set => WF_F(264, value); } // @264 CoreFogColor[1]
    public float CoreFogColorB    { get => RF_F(268); set => WF_F(268, value); } // @268 CoreFogColor[2]
    public float CoreFogColorA    { get => RF_F(272); set => WF_F(272, value); } // @272 CoreFogColor[3]

    public float HorizonFogColorR { get => RF_F(276); set => WF_F(276, value); } // @276 HorizonFogColor[0]
    public float HorizonFogColorG { get => RF_F(280); set => WF_F(280, value); } // @280 HorizonFogColor[1]
    public float HorizonFogColorB { get => RF_F(284); set => WF_F(284, value); } // @284 HorizonFogColor[2]
    public float HorizonFogColorA { get => RF_F(288); set => WF_F(288, value); } // @288 HorizonFogColor[3]

    public float CoreFogAmount1   { get => RF_F(292); set => WF_F(292, value); } // @292 CoreFogAmounts[0]
    public float CoreFogAmount2   { get => RF_F(296); set => WF_F(296, value); } // @296 CoreFogAmounts[1]
    public float CoreFogAmount3   { get => RF_F(300); set => WF_F(300, value); } // @300 CoreFogAmounts[2]

    public float HorizonFogAmount1{ get => RF_F(304); set => WF_F(304, value); } // @304 HorizonFogAmounts[0]
    public float HorizonFogAmount2{ get => RF_F(308); set => WF_F(308, value); } // @308 HorizonFogAmounts[1]
    public float HorizonFogAmount3{ get => RF_F(312); set => WF_F(312, value); } // @312 HorizonFogAmounts[2]

    public float CoreFogDist1     { get => RF_F(316); set => WF_F(316, value); } // @316 CoreFogDists[0]
    public float CoreFogDist2     { get => RF_F(320); set => WF_F(320, value); } // @320 CoreFogDists[1]

    public float HorizonFogDist1  { get => RF_F(324); set => WF_F(324, value); } // @324 HorizonFogDists[0]
    public float HorizonFogDist2  { get => RF_F(328); set => WF_F(328, value); } // @328 HorizonFogDists[1]

    public float PointCloudOriginX { get => RF_F(332); set => WF_F(332, value); } // @332
    public float PointCloudOriginY { get => RF_F(336); set => WF_F(336, value); } // @336
    public float PointCloudOriginZ { get => RF_F(340); set => WF_F(340, value); } // @340
    public float PointCloudSizeX  { get => RF_F(344); set => WF_F(344, value); } // @344 PointCloudSize[0]
    public float PointCloudSizeY  { get => RF_F(348); set => WF_F(348, value); } // @348 PointCloudSize[1]
    public float PointCloudSizeZ  { get => RF_F(352); set => WF_F(352, value); } // @352 PointCloudSize[2]

    public int   GlowEnabled          { get => RF_I(356); set => WF_I(356, value); } // @356
    public float GlowBlurKernelScale0 { get => RF_F(360); set => WF_F(360, value); } // @360
    public float GlowBlurKernelScale1 { get => RF_F(364); set => WF_F(364, value); } // @364
    public float PostProcessSourceWeight { get => RF_F(368); set => WF_F(368, value); } // @368
    public int   GlowBlurPassCount     { get => RF_I(372); set => WF_I(372, value); } // @372 — measured as int32=1 across the corpus; looks like a bool/flag, not a float "Factor"
    public float GlowBlurFactor0   { get => RF_F(376); set => WF_F(376, value); } // @376 — measured as float=1.0 across the corpus; this is a float, not an int
    public float GlowBlurFactor1   { get => RF_F(380); set => WF_F(380, value); } // @380
    public int   BloomEnabled      { get => RF_I(384); set => WF_I(384, value); } // @384

    public float BloomThreshold      { get => RF_F(388); set => WF_F(388, value); } // @388
    public int   BloomDirectionCount { get => RF_I(392); set => WF_I(392, value); } // @392
    public float BloomDirectionPhase { get => RF_F(396); set => WF_F(396, value); } // @396 — measured as float=2.43; a float, not an int
    public float BloomSpreadDivisor { get => RF_F(400); set => WF_F(400, value); } // @400
    public float BloomSampleScaleX  { get => RF_F(404); set => WF_F(404, value); } // @404
    public float BloomSampleScaleY  { get => RF_F(408); set => WF_F(408, value); } // @408
    public float BloomResultWeight  { get => RF_F(412); set => WF_F(412, value); } // @412
    public int   BloomPassCount     { get => RF_I(416); set => WF_I(416, value); } // @416 — measured as int32=3; an int, not a float
    public float PointCloudLightAmount { get => RF_F(420); set => WF_F(420, value); } // @420 — measured as float=0.1; a float, not an int

    // This block's real identity differs from what earlier field names
    // implied: @424..432 is Fog of War, @436..444 is Intel. Byte offsets
    // are unchanged; only which name points at which offset differs from
    // an earlier version of this file.
    public float FogOfWarLowerHeight    { get => RF_F(424); set => WF_F(424, value); } // @424
    public float FogOfWarHighestHeight  { get => RF_F(428); set => WF_F(428, value); } // @428
    public float FogOfWarHeightStrength { get => RF_F(432); set => WF_F(432, value); } // @432
    public float IntelLowerHeight       { get => RF_F(436); set => WF_F(436, value); } // @436
    public float IntelHighestHeight     { get => RF_F(440); set => WF_F(440, value); } // @440
    public float IntelHeightStrength    { get => RF_F(444); set => WF_F(444, value); } // @444

    public int   DepthOfFieldEnabled { get => RF_I(448); set => WF_I(448, value); } // @448 — the name "Enabled" suggests int32 (matching WaterEnabled/GlowEnabled/GaussianEnabled), but the measured value of 0 is ambiguous (0 reads the same as float or int)
    public float DepthOfFieldStart   { get => RF_F(452); set => WF_F(452, value); } // @452
    public float DepthOfFieldEnd     { get => RF_F(456); set => WF_F(456, value); } // @456
    public float SharpenStart      { get => RF_F(460); set => WF_F(460, value); } // @460
    public float SharpenEnd        { get => RF_F(464); set => WF_F(464, value); } // @464
    public float SharpenBaseAmount { get => RF_F(468); set => WF_F(468, value); } // @468
    public float SharpenNearOffset { get => RF_F(472); set => WF_F(472, value); } // @472
    public float SharpenFarOffset  { get => RF_F(476); set => WF_F(476, value); } // @476
    public int   RenderPassACEnabled { get => RF_I(480); set => WF_I(480, value); } // @480
    public int   RenderPassB0Enabled { get => RF_I(484); set => WF_I(484, value); } // @484
    public int   RenderPassB4Enabled { get => RF_I(488); set => WF_I(488, value); } // @488

    public float ShadowCastDistance { get => RF_F(492); set => WF_F(492, value); } // @492 — name not fully confirmed, but the type is definitely float (measured as exactly 20.0)

    public float WindDirectionX    { get => RF_F(496); set => WF_F(496, value); } // @496
    public float WindDirectionZ    { get => RF_F(500); set => WF_F(500, value); } // @500
    public float WindSpeed         { get => RF_F(504); set => WF_F(504, value); } // @504

    // ================================================================
    // Convenience grouped accessors (tuples built from the fields above —
    // not separate fields, just combining X/Y/Z/R/G/B/A into one value)
    // ================================================================

    public (float X, float Y, float Z) SunDirection
    {
        get => (SunDirectionX, SunDirectionY, SunDirectionZ);
        set { SunDirectionX = value.X; SunDirectionY = value.Y; SunDirectionZ = value.Z; }
    }

    public (float R, float G, float B) SunColor
    {
        get => (SunColorR, SunColorG, SunColorB);
        set { SunColorR = value.R; SunColorG = value.G; SunColorB = value.B; }
    }

    public (float R, float G, float B, float A) ShadowColor
    {
        get => (ShadowColorR, ShadowColorG, ShadowColorB, ShadowColorA);
        set { ShadowColorR = value.R; ShadowColorG = value.G; ShadowColorB = value.B; ShadowColorA = value.A; }
    }

    public (float R, float G, float B, float A) WaterColor
    {
        get => (WaterColorR, WaterColorG, WaterColorB, WaterColorA);
        set { WaterColorR = value.R; WaterColorG = value.G; WaterColorB = value.B; WaterColorA = value.A; }
    }

    public (float R, float G, float B, float A) CoreFogColor
    {
        get => (CoreFogColorR, CoreFogColorG, CoreFogColorB, CoreFogColorA);
        set { CoreFogColorR = value.R; CoreFogColorG = value.G; CoreFogColorB = value.B; CoreFogColorA = value.A; }
    }

    public (float R, float G, float B, float A) HorizonFogColor
    {
        get => (HorizonFogColorR, HorizonFogColorG, HorizonFogColorB, HorizonFogColorA);
        set { HorizonFogColorR = value.R; HorizonFogColorG = value.G; HorizonFogColorB = value.B; HorizonFogColorA = value.A; }
    }

    // ---- Raw access (for debugging) ----
    public uint  GetRawUInt32(int off) => Platform == BdfPlatform.Pc
        ? BinaryPrimitives.ReadUInt32LittleEndian(Header.AsSpan(off, 4))
        : BinaryPrimitives.ReadUInt32BigEndian(Header.AsSpan(off, 4));
    public float GetRawFloat(int off) =>
        BitConverter.Int32BitsToSingle(unchecked((int)GetRawUInt32(off)));
    public int GetRawInt32(int off) => Platform == BdfPlatform.Pc
        ? BinaryPrimitives.ReadInt32LittleEndian(Header.AsSpan(off, 4))
        : BinaryPrimitives.ReadInt32BigEndian(Header.AsSpan(off, 4));
    public void SetRawUInt32(int off, uint v) {
        if (Platform == BdfPlatform.Pc) BinaryPrimitives.WriteUInt32LittleEndian(Header.AsSpan(off, 4), v);
        else BinaryPrimitives.WriteUInt32BigEndian(Header.AsSpan(off, 4), v);
    }
    public void SetRawFloat(int off, float v) =>
        SetRawUInt32(off, unchecked((uint)BitConverter.SingleToInt32Bits(v)));
    public void SetRawInt32(int off, int v) {
        if (Platform == BdfPlatform.Pc) BinaryPrimitives.WriteInt32LittleEndian(Header.AsSpan(off, 4), v);
        else BinaryPrimitives.WriteInt32BigEndian(Header.AsSpan(off, 4), v);
    }

    // ---- private helpers ----
    private int   RF_I(int off) => Platform == BdfPlatform.Pc
        ? BinaryPrimitives.ReadInt32LittleEndian(Header.AsSpan(off, 4))
        : BinaryPrimitives.ReadInt32BigEndian(Header.AsSpan(off, 4));
    private float RF_F(int off) => GetRawFloat(off);
    private void  WF_I(int off, int v) {
        if (Platform == BdfPlatform.Pc) BinaryPrimitives.WriteInt32LittleEndian(Header.AsSpan(off, 4), v);
        else BinaryPrimitives.WriteInt32BigEndian(Header.AsSpan(off, 4), v);
    }
    private void WF_F(int off, float v) => SetRawFloat(off, v);
}

/// <summary>Parse/Serialize for info.bdf.</summary>
public static class InfoBdf
{
    public static BdfPadding Padding => BdfPadding.Align32;

    public static InfoModel Parse(BdfContainer container)
    {
        var r = new BdfReader(container.Data, container.Platform);
        var model = new InfoModel { Platform = container.Platform };

        container.Data.AsSpan(0, 508).CopyTo(model.Header);

        model.EnvironmentMap         = r.ReadCStringAt(r.PeekUInt32At(240));
        model.SkyMap                 = r.ReadCStringAt(r.PeekUInt32At(244));
        model.ReservedTexturePath     = r.ReadCStringAt(r.PeekUInt32At(248));
        model.ColorCorrectionCube    = r.ReadCStringAt(r.PeekUInt32At(252));
        model.FogOfWarCorrectionCube = r.ReadCStringAt(r.PeekUInt32At(256));

        return model;
    }

    public static (byte[] Data, uint[] OffsetTable) Serialize(InfoModel model, BdfPlatform platform)
    {
        var le = platform == BdfPlatform.Pc;
        var poolWriter = new StringPoolWriter(dedup: true);
        var offEnv = poolWriter.Add(model.EnvironmentMap);
        var offSky = poolWriter.Add(model.SkyMap);
        var offTp3 = poolWriter.Add(model.ReservedTexturePath);
        var offCcc = poolWriter.Add(model.ColorCorrectionCube);
        var offFow = poolWriter.Add(model.FogOfWarCorrectionCube);
        var pool = poolWriter.ToArray();

        var data = new byte[508 + pool.Length];
        model.Header.CopyTo(data, 0);
        pool.CopyTo(data, 508);

        void WritePtr(int off, uint ptr)
        {
            if (le) System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(off, 4), ptr);
            else    System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(off, 4), ptr);
        }
        uint poolBase = 508;
        WritePtr(240, poolBase + offEnv);
        WritePtr(244, poolBase + offSky);
        WritePtr(248, poolBase + offTp3);
        WritePtr(252, poolBase + offCcc);
        WritePtr(256, poolBase + offFow);

        var ot = new uint[] { 240, 244, 248, 252, 256 };
        return (data, ot);
    }
}
}
