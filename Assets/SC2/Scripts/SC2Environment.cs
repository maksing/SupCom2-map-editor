// SC2Environment.cs
// MonoBehaviour data carrier for info.bdf. Holds the full 508-byte
// Header (including its still-unconfirmed fields, offsets 356-507) for a
// lossless round-trip on export, plus applies the confirmed fields
// (offsets 0-239) to the scene: a directional light
// (SunDirection/SunColor/SunIntensity) and an approximate fog
// (CoreFogColor/CoreFogAmounts).
//
// Fog: SC2's real formula is non-linear (CoreFog vs HorizonFog, with
// separate Dist values for the "core" and the horizon) — this uses a
// LINEAR approximation via Unity's ExponentialSquared fog as a visual
// stand-in. An exact model (a full fog-volume/shader) is a separate,
// not-yet-done step.
//
// Water* fields (see WaterFields) are used by SC2WaterMaterialFactory
// for the SC2/Water shader.

using System;
using System.Buffers.Binary;
using UnityEngine;
using BdfIO.Core;
using BdfIO.Formats.Info;

public class SC2Environment : MonoBehaviour
{
    /// <summary>Full 508 bytes of the EnvironmentHeader — kept for a lossless export later.</summary>
    public byte[] Header = new byte[508];

    public string EnvironmentMap = "";
    public string SkyMap = "";
    public string TexturePath3 = "";
    public string ColorCorrectionCube = "";
    public string FogOfWarCorrectionCube = "";

    public Vector3 SunDirection;
    public Color SunColor = Color.white;
    public float SunIntensity = 1f;
    public float WaterElevation;
    public float AirElevation;

    public Color CoreFogColor = Color.gray;
    public Color HorizonFogColor = Color.gray;
    public float CoreFogAmount;

    /// <summary>Platform of the source info.bdf — needed to correctly interpret <see cref="Header"/> in <see cref="GetRawFloat"/>.</summary>
    public BdfPlatform Platform = BdfPlatform.Pc;

    /// <summary>Raw float value at an absolute offset into <see cref="Header"/>.</summary>
    public float GetRawFloat(int offset)
    {
        var bits = Platform == BdfPlatform.Pc
            ? BinaryPrimitives.ReadUInt32LittleEndian(Header.AsSpan(offset, 4))
            : BinaryPrimitives.ReadUInt32BigEndian(Header.AsSpan(offset, 4));
        return BitConverter.Int32BitsToSingle(unchecked((int)bits));
    }

    public void SetRawFloat(int offset, float value)
    {
        var bits = unchecked((uint)BitConverter.SingleToInt32Bits(value));
        if (Platform == BdfPlatform.Pc)
            BinaryPrimitives.WriteUInt32LittleEndian(Header.AsSpan(offset, 4), bits);
        else
            BinaryPrimitives.WriteUInt32BigEndian(Header.AsSpan(offset, 4), bits);
    }

    /// <summary>Water enabled flag (offset 48, int32 0/1).</summary>
    public int WaterEnabled
    {
        get
        {
            var bits = Platform == BdfPlatform.Pc
                ? BinaryPrimitives.ReadUInt32LittleEndian(Header.AsSpan(48, 4))
                : BinaryPrimitives.ReadUInt32BigEndian(Header.AsSpan(48, 4));
            return (int)bits;
        }
        set
        {
            var bits = (uint)value;
            if (Platform == BdfPlatform.Pc)
                BinaryPrimitives.WriteUInt32LittleEndian(Header.AsSpan(48, 4), bits);
            else
                BinaryPrimitives.WriteUInt32BigEndian(Header.AsSpan(48, 4), bits);
        }
    }

    /// <summary>Water* fields, used by <c>SC2WaterMaterialFactory</c>.</summary>
    public WaterFields Water => new()
    {
        Enabled = GetRawFloat(48) != 0, // int32, but its 0/1 bit pattern reads the same as a float
        Foam1Movement = new Vector2(GetRawFloat(52), GetRawFloat(56)),
        Foam2Movement = new Vector2(GetRawFloat(60), GetRawFloat(64)),
        Normal1Movement = new Vector2(GetRawFloat(68), GetRawFloat(72)),
        Normal2Movement = new Vector2(GetRawFloat(76), GetRawFloat(80)),
        Normal3Movement = new Vector2(GetRawFloat(84), GetRawFloat(88)),
        Normal4Movement = new Vector2(GetRawFloat(92), GetRawFloat(96)),
        Color = new Color(GetRawFloat(100), GetRawFloat(104), GetRawFloat(108), GetRawFloat(112)),
        FoamRepeatRate = new Vector2(GetRawFloat(116), GetRawFloat(120)),
        NormalRepeatRate = new Vector4(GetRawFloat(124), GetRawFloat(128), GetRawFloat(132), GetRawFloat(136)),
        RefractionScale = GetRawFloat(140),
        ChromaticScale = GetRawFloat(144),
        FresnelBias = GetRawFloat(148),
        DepthCastHeight = GetRawFloat(152),
        DepthScale = GetRawFloat(156),
        ChromaticScale2 = GetRawFloat(160),
        SpecPower2 = GetRawFloat(164),
        GlareBrightness = GetRawFloat(168),
        GlareThreshold = GetRawFloat(172),
        SpecPower = GetRawFloat(176),
        WaveTiming = GetRawFloat(180),
        WaveAmplitude = GetRawFloat(184),
        FoamHeightOffset = GetRawFloat(188),
        WaveStartDepth = GetRawFloat(192),
        DepthOffset = new Vector2(GetRawFloat(196), GetRawFloat(200)),
        DepthBoundsSize = new Vector2(GetRawFloat(204), GetRawFloat(208)),
        MaxDepth = GetRawFloat(212),
        Elevation = GetRawFloat(216),
        ReflectionStartHeight = GetRawFloat(220),
    };

    /// <summary>A typed snapshot of the Water* fields (offset 48-224).</summary>
    public struct WaterFields
    {
        public bool Enabled;
        public Vector2 Foam1Movement, Foam2Movement;
        public Vector2 Normal1Movement, Normal2Movement, Normal3Movement, Normal4Movement;
        public Color Color;
        public Vector2 FoamRepeatRate;
        public Vector4 NormalRepeatRate;
        public float RefractionScale, ChromaticScale, ChromaticScale2;
        public float FresnelBias, DepthCastHeight, DepthScale;
        public float SpecPower, SpecPower2, GlareBrightness, GlareThreshold;
        public float WaveTiming, WaveAmplitude, FoamHeightOffset, WaveStartDepth;
        public Vector2 DepthOffset, DepthBoundsSize;
        public float MaxDepth, Elevation, ReflectionStartHeight;
    }

    /// <summary>Fills this component from a parsed InfoModel and applies light/fog to the scene.</summary>
    public void Apply(InfoModel model)
    {
        Header = (byte[])model.Header.Clone();
        Platform = model.Platform;

        EnvironmentMap = model.EnvironmentMap;
        SkyMap = model.SkyMap;
        TexturePath3 = model.ReservedTexturePath;
        ColorCorrectionCube = model.ColorCorrectionCube;
        FogOfWarCorrectionCube = model.FogOfWarCorrectionCube;

        var (sx, sy, sz) = model.SunDirection;
        SunDirection = new Vector3(sx, sy, sz);

        var (r, g, b) = model.SunColor;
        SunColor = new Color(r, g, b);
        SunIntensity = model.SunIntensity;
        WaterElevation = model.WaterElevation;
        AirElevation = model.AirElevation;

        CoreFogColor = ReadColor4(model, 260);
        HorizonFogColor = ReadColor4(model, 276);
        CoreFogAmount = model.GetRawFloat(292);

        ApplyToScene();
    }

    private static Color ReadColor4(InfoModel model, int offset) => new(
        model.GetRawFloat(offset + 0),
        model.GetRawFloat(offset + 4),
        model.GetRawFloat(offset + 8),
        model.GetRawFloat(offset + 12));

    /// <summary>
    /// The Directional Light points along SunDirection (its forward
    /// direction equals SunDirection; if the light appears to come from
    /// the wrong side, check the sign). Fog is an ExponentialSquared
    /// approximation (see the note above).
    /// </summary>
    public void ApplyToScene()
    {
        var light = FindOrCreateSunLight();
        // SunIntensity already gets a Mathf.Max(0.01f, ...) floor right
        // below for exactly this reason (a from-scratch map's Environment
        // header is genuinely all-zero bytes until authored/imported - see
        // EnsureEnvironmentHeader's own doc comment) but SunColor never got
        // the same treatment: a literal (0,0,0) light color makes
        // Unity's _LightColor0 evaluate to zero in every shader that
        // multiplies by it, e.g. SC2/Unit's
        // `lit = baseColor * (ambient + lightColor * ndotl)` - so every
        // placed prop/unit rendered fully unlit/black, indistinguishable
        // from "nothing is there" against dark terrain on a map whose Sun
        // was never set.
        var sunColorWasBlack = IsEffectivelyBlack(SunColor);
        light.color = sunColorWasBlack ? Color.white : SunColor;
        light.intensity = Mathf.Max(0.01f, SunIntensity);
        if (sunColorWasBlack)
            Debug.Log("[SC2 Placement] SunColor was black (map's Environment data is unset) - using white as a fallback so props/units stay visibly lit.");
        if (SunDirection.sqrMagnitude > 0.0001f)
            light.transform.rotation = Quaternion.LookRotation(SunDirection.normalized);

        // Same reasoning as the SunColor floor above, for the OTHER half of
        // every lit shader's lighting term (SC2/Unit's
        // `ambient + lightColor * ndotl`): this project never sets
        // RenderSettings.ambientLight/ambientMode anywhere, so it's whatever
        // the loaded scene's own baked Lighting settings happen to be - not
        // something this codebase controls or can assume is non-black. If
        // it (or the whole flat ambient color) is at or near zero, the
        // Sun-color floor above isn't enough by itself to keep props/units
        // visibly lit.
        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
        if (IsEffectivelyBlack(RenderSettings.ambientLight))
        {
            RenderSettings.ambientLight = new Color(0.25f, 0.25f, 0.28f);
            Debug.Log("[SC2 Placement] RenderSettings.ambientLight was black - using a dim gray fallback so props/units stay visibly lit.");
        }

        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.ExponentialSquared;
        RenderSettings.fogColor = CoreFogColor;
        RenderSettings.fogDensity = Mathf.Clamp(CoreFogAmount * 0.001f, 0f, 0.05f);
    }

    private static bool IsEffectivelyBlack(Color c) => c.r <= 0.001f && c.g <= 0.001f && c.b <= 0.001f;

    private static Light FindOrCreateSunLight()
    {
        var existing = UnityEngine.Object.FindObjectOfType<Light>();
        if (existing != null && existing.type == LightType.Directional)
            return existing;

        var go = new GameObject("Sun (info.bdf)");
        var light = go.AddComponent<Light>();
        light.type = LightType.Directional;
        return light;
    }
}
