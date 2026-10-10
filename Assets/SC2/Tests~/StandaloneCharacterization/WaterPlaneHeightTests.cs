using SC2.MapEditor.Document;
using Xunit;

namespace SC2CharacterizationTests;

public class WaterPlaneHeightTests
{
    private static byte[] Header() => new byte[SC2EnvironmentDocument.HeaderSize];

    [Fact]
    public void A_map_without_the_setting_keeps_the_plane_synced_with_the_environment()
    {
        var doc = SC2EnvironmentLegacyBridge.BuildFromLegacy(Header(), "", "", "", "", "");
        Assert.True(doc.WaterPlaneSyncWithEnvironment);

        var back = SC2MapDocumentSerializer.Deserialize(SC2MapDocumentSerializer.Serialize(new SC2MapDocument()));
        Assert.True(back.Environment.WaterPlaneSyncWithEnvironment);
    }

    [Fact]
    public void The_plane_height_goes_through_the_legacy_bridge_both_ways()
    {
        var doc = SC2EnvironmentLegacyBridge.BuildFromLegacy(Header(), "", "", "", "", "",
            waterPlaneSyncWithEnvironment: false, waterPlaneHeight: 37.5f);
        Assert.False(doc.WaterPlaneSyncWithEnvironment);
        Assert.Equal(37.5f, doc.WaterPlaneHeight);

        var result = SC2MapDocumentToLegacyBridge.ReconstructEnvironment(doc);
        Assert.False(result.WaterPlaneSyncWithEnvironment);
        Assert.Equal(37.5f, result.WaterPlaneHeight);
    }

    [Fact]
    public void The_plane_height_survives_the_document_round_trip()
    {
        var document = new SC2MapDocument();
        document.Environment.WaterPlaneSyncWithEnvironment = false;
        document.Environment.WaterPlaneHeight = 12.25f;

        var back = SC2MapDocumentSerializer.Deserialize(SC2MapDocumentSerializer.Serialize(document));

        Assert.False(back.Environment.WaterPlaneSyncWithEnvironment);
        Assert.Equal(12.25f, back.Environment.WaterPlaneHeight);
    }
}
