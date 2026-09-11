using System;
using SC2.MapEditor.Document;

namespace SC2CharacterizationTests;

public class EnvironmentDocumentBridgeTests
{
    private static byte[] SampleHeader(byte fill = 0x42)
    {
        var header = new byte[SC2EnvironmentDocument.HeaderSize];
        for (var i = 0; i < header.Length; i++) header[i] = (byte)(fill + i % 7);
        return header;
    }

    [Fact]
    public void BuildFromLegacy_ProjectsHeaderAndStrings()
    {
        var header = SampleHeader();

        var doc = SC2EnvironmentLegacyBridge.BuildFromLegacy(
            header, "env.dds", "sky.dds", "path3.dds", "colorcube.dds", "fowcube.dds",
            waterPlaneSize: 2048, waterUvScale: 512f, waterTexturePaths: new[] { "water_d.dds" });

        Assert.Equal(header, doc.WorkingHeader);
        Assert.Equal(header, doc.OriginalHeader);
        Assert.Equal("env.dds", doc.EnvironmentMap);
        Assert.Equal("sky.dds", doc.SkyMap);
        Assert.Equal("path3.dds", doc.TexturePath3);
        Assert.Equal("colorcube.dds", doc.ColorCorrectionCube);
        Assert.Equal("fowcube.dds", doc.FogOfWarCorrectionCube);
        // WaterPlaneSize/WaterUvScale/WaterTexturePaths must have a document
        // schema home, not fall through to UnknownFields.
        Assert.Equal(2048, doc.WaterPlaneSize);
        Assert.Equal(512f, doc.WaterUvScale);
        Assert.Equal(new[] { "water_d.dds" }, doc.WaterTexturePaths);
        Assert.Empty(doc.UnknownFields);
    }

    [Fact]
    public void BuildFromLegacy_OriginalAndWorkingAreIndependentCopies()
    {
        var header = SampleHeader();
        var doc = SC2EnvironmentLegacyBridge.BuildFromLegacy(header, "", "", "", "", "");

        doc.WorkingHeader[0] = (byte)(doc.WorkingHeader[0] + 1);

        Assert.NotEqual(doc.WorkingHeader[0], doc.OriginalHeader[0]);
    }

    [Fact]
    public void BuildFromLegacy_DoesNotAliasTheInputArray()
    {
        var header = SampleHeader();
        var doc = SC2EnvironmentLegacyBridge.BuildFromLegacy(header, "", "", "", "", "");

        header[0] = (byte)(header[0] + 1);

        Assert.NotEqual(header[0], doc.WorkingHeader[0]);
        Assert.NotEqual(header[0], doc.OriginalHeader[0]);
    }

    [Fact]
    public void BuildFromLegacy_NullStringsBecomeEmptyStrings()
    {
        var doc = SC2EnvironmentLegacyBridge.BuildFromLegacy(SampleHeader(), null, null, null, null, null);

        Assert.Equal("", doc.EnvironmentMap);
        Assert.Equal("", doc.SkyMap);
        Assert.Equal("", doc.TexturePath3);
        Assert.Equal("", doc.ColorCorrectionCube);
        Assert.Equal("", doc.FogOfWarCorrectionCube);
    }

    [Fact]
    public void BuildFromLegacy_ThrowsOnNullHeader()
    {
        Assert.Throws<ArgumentNullException>(() =>
            SC2EnvironmentLegacyBridge.BuildFromLegacy(null, "", "", "", "", ""));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(507)]
    [InlineData(509)]
    [InlineData(1024)]
    public void BuildFromLegacy_ThrowsOnWrongHeaderLength(int wrongLength)
    {
        var header = new byte[wrongLength];
        Assert.Throws<ArgumentException>(() =>
            SC2EnvironmentLegacyBridge.BuildFromLegacy(header, "", "", "", "", ""));
    }
}
