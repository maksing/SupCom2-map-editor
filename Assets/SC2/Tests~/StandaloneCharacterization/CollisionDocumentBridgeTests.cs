using SC2.MapEditor.Document;

namespace SC2CharacterizationTests;

public class CollisionDocumentBridgeTests
{
    [Fact]
    public void BuildFromLegacy_ProjectsVerticalOffset()
    {
        var doc = SC2CollisionLegacyBridge.BuildFromLegacy(collision2HeightOffset: 0.25f);

        Assert.Equal(0.25f, doc.Generation.VerticalOffset);
    }

    [Fact]
    public void BuildFromLegacy_UsesTheShellsOwnDisplayedPolygonBudget()
    {
        var doc = SC2CollisionLegacyBridge.BuildFromLegacy(0f);

        Assert.Equal(2500, doc.Generation.MaxPolygons);
        Assert.Equal(SC2CollisionLegacyBridge.LegacyMaxPolygons, doc.Generation.MaxPolygons);
    }

    [Fact]
    public void BuildFromLegacy_AlwaysGeneratedFromHfield()
    {
        var doc = SC2CollisionLegacyBridge.BuildFromLegacy(1.5f);

        Assert.Equal(SC2CollisionSourceMode.GeneratedFromHfield, doc.SourceMode);
    }

    [Fact]
    public void BuildFromLegacy_HonestlyLeavesMeshUnpopulated()
    {
        // SC2TerrainData never persists a generated collision mesh -- the
        // legacy shell regenerates it into a session-local field and never
        // writes it back. A read-only snapshot of SC2TerrainData has
        // nothing real to put here.
        var doc = SC2CollisionLegacyBridge.BuildFromLegacy(0f);

        Assert.Null(doc.Mesh);
        Assert.Equal(SC2DerivedState.Missing, doc.State);
        Assert.Equal(0, doc.SourceHfieldRevision);
    }

    [Theory]
    [InlineData(-5f)]
    [InlineData(0f)]
    [InlineData(0.25f)]
    [InlineData(5f)]
    public void BuildFromLegacy_PreservesOffsetAcrossItsFullLegacyRange(float offset)
    {
        // SC2Collision2Shell's slider clamps to [-5, 5] -- the bridge itself
        // does no clamping of its own (that's a UI concern), just confirming
        // it round-trips the whole range faithfully.
        var doc = SC2CollisionLegacyBridge.BuildFromLegacy(offset);

        Assert.Equal(offset, doc.Generation.VerticalOffset);
    }
}
