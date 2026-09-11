using System;

/// <summary>
/// Shared JSON shape for exporting an InfoModel out of the standalone
/// WinForms Info.bdf Editor app (Tools~/InfoBdfEditor) and importing it back
/// into the Unity-side SC2InfoBdfEditorPanel — the SAME file, compiled into
/// both projects (Unity via its normal asset scan, the WinForms app via the
/// same absolute-path &lt;Compile Include&gt; pattern already used by
/// Tests~/StandaloneCharacterization), so the two sides can never drift.
///
/// Public FIELDS, not properties: Unity's JsonUtility only (de)serializes
/// public fields, never properties. The exe side must therefore configure
/// System.Text.Json with JsonSerializerOptions.IncludeFields = true (its
/// default only serializes properties) to produce/consume the same shape.
///
/// HeaderBase64 carries the whole 508-byte EnvironmentHeader as one blob
/// rather than ~90 individual fields — simplest, zero risk of missing or
/// mis-typing a field; every numeric InfoModel property is a fixed-offset
/// view over this same byte array (see InfoBdf.cs), so the header alone is
/// sufficient to reconstruct every field. The 5 texture path strings live
/// outside the header (string pool pointers) and are carried separately.
/// </summary>
[Serializable]
public sealed class InfoBdfJsonDto
{
    /// <summary>"Pc" or "Xbox" — BdfPlatform enum name, as a string so
    /// neither side needs the other's enum type directly.</summary>
    public string Platform = "Pc";

    /// <summary>Convert.ToBase64String(model.Header) — the whole 508-byte
    /// EnvironmentHeader.</summary>
    public string HeaderBase64 = "";

    public string EnvironmentMap = "";
    public string SkyMap = "";
    public string ReservedTexturePath = "";
    public string ColorCorrectionCube = "";
    public string FogOfWarCorrectionCube = "";
}
