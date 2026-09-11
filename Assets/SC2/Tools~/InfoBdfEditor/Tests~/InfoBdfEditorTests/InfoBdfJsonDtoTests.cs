using System.Text.Json;
using BdfIO.Core;
using BdfIO.Formats.Info;

namespace InfoBdfEditorTests;

/// <summary>
/// The standalone app's export and the Unity panel's "Import JSON..."
/// button only ever communicate through InfoBdfJsonDto — if this shape or
/// its serialization options drift, importing an exported file in Unity
/// silently loses data instead of erroring (JsonUtility.FromJson leaves
/// unmatched fields at their C# default rather than throwing). These tests
/// exercise exactly the two real serializer calls each side makes
/// (System.Text.Json with IncludeFields=true on the exe side) without
/// needing Unity's JsonUtility itself (not available outside Unity) —
/// field-name/shape agreement is verified structurally instead.
/// </summary>
public sealed class InfoBdfJsonDtoTests
{
    private static readonly JsonSerializerOptions Options = new() { IncludeFields = true };

    [Fact]
    public void DtoUsesPublicFieldsNotProperties()
    {
        // JsonUtility (Unity) only ever (de)serializes public fields, never
        // properties — if this DTO ever gained a property instead of a
        // field, the exe's JSON would look fine but Unity would silently
        // ignore that value on import.
        var type = typeof(InfoBdfJsonDto);
        Assert.Empty(type.GetProperties());
        Assert.NotEmpty(type.GetFields());
    }

    [Fact]
    public void RoundTripsThroughSystemTextJsonWithIncludeFields()
    {
        var original = new InfoModel { Platform = BdfPlatform.Pc };
        original.SunIntensity = 1.25f;
        original.WaterEnabled = 1;
        original.EnvironmentMap = "/textures/environment/test_env.dds";

        var dto = new InfoBdfJsonDto
        {
            Platform = original.Platform.ToString(),
            HeaderBase64 = Convert.ToBase64String(original.Header),
            EnvironmentMap = original.EnvironmentMap,
            SkyMap = original.SkyMap,
            ReservedTexturePath = original.ReservedTexturePath,
            ColorCorrectionCube = original.ColorCorrectionCube,
            FogOfWarCorrectionCube = original.FogOfWarCorrectionCube
        };

        var json = JsonSerializer.Serialize(dto, Options);
        var reparsedDto = JsonSerializer.Deserialize<InfoBdfJsonDto>(json, Options);

        Assert.NotNull(reparsedDto);
        Assert.Equal(dto.Platform, reparsedDto!.Platform);
        Assert.Equal(dto.HeaderBase64, reparsedDto.HeaderBase64);
        Assert.Equal(dto.EnvironmentMap, reparsedDto.EnvironmentMap);

        // Reconstruct an InfoModel the same way SC2InfoBdfEditorPanel.
        // PickJsonFile does, and confirm the numeric fields survive.
        var header = Convert.FromBase64String(reparsedDto.HeaderBase64);
        Assert.Equal(508, header.Length);
        var rebuilt = new InfoModel { Platform = Enum.Parse<BdfPlatform>(reparsedDto.Platform) };
        header.CopyTo(rebuilt.Header, 0);

        Assert.Equal(original.SunIntensity, rebuilt.SunIntensity);
        Assert.Equal(original.WaterEnabled, rebuilt.WaterEnabled);
    }

    [Fact]
    public void HeaderBase64IsAlways508Bytes()
    {
        var m = new InfoModel { Platform = BdfPlatform.Pc };
        var base64 = Convert.ToBase64String(m.Header);
        Assert.Equal(508, Convert.FromBase64String(base64).Length);
    }
}
