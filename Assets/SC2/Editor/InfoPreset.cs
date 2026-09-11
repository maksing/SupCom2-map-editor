#if UNITY_EDITOR
using System;
using UnityEngine;
using BdfIO.Formats.Info;

/// <summary>
/// An info.bdf preset for saving/loading as a standalone .json file.
/// Stores the whole Header as base64 — this guarantees an exact
/// round-trip of ALL fields (including ⚠/Unknown ones) without risking a
/// missed field from manually enumerating DTO properties. Plus readable
/// string paths and metadata for the UI (name, date, author note).
/// </summary>
[Serializable]
public class InfoPreset
{
    public string PresetName = "";
    public string CreatedAt = "";
    public string Note = "";

    /// <summary>508-byte EnvironmentHeader, base64.</summary>
    public string HeaderBase64 = "";

    public string EnvironmentMap = "";
    public string SkyMap = "";
    public string TexturePath3 = "";
    public string ColorCorrectionCube = "";
    public string FogOfWarCorrectionCube = "";

    public static InfoPreset FromModel(InfoModel m, string presetName, string note = "")
    {
        return new InfoPreset
        {
            PresetName = presetName,
            CreatedAt = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"),
            Note = note,
            HeaderBase64 = Convert.ToBase64String(m.Header),
            EnvironmentMap = m.EnvironmentMap,
            SkyMap = m.SkyMap,
            TexturePath3 = m.ReservedTexturePath, // JSON key kept as "TexturePath3" for backward compat with existing saved presets
            ColorCorrectionCube = m.ColorCorrectionCube,
            FogOfWarCorrectionCube = m.FogOfWarCorrectionCube,
        };
    }

    /// <summary>Applies the preset onto an existing model (preserving Platform).</summary>
    public void ApplyTo(InfoModel m)
    {
        var bytes = Convert.FromBase64String(HeaderBase64);
        if (bytes.Length != 508)
            throw new InvalidOperationException($"InfoPreset.HeaderBase64 has the wrong size: {bytes.Length} (expected 508).");
        Array.Copy(bytes, m.Header, 508);

        m.EnvironmentMap = EnvironmentMap;
        m.SkyMap = SkyMap;
        m.ReservedTexturePath = TexturePath3;
        m.ColorCorrectionCube = ColorCorrectionCube;
        m.FogOfWarCorrectionCube = FogOfWarCorrectionCube;
    }

    public string ToJson(bool pretty = true) => JsonUtility.ToJson(this, pretty);
    public static InfoPreset? FromJson(string json) => JsonUtility.FromJson<InfoPreset>(json);
}
#endif
