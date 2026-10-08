using SC2.MapEditor.Document;
using SC2.MapEditor.SmartExport;
using Xunit;

namespace SC2CharacterizationTests;

public class Collision2FloorTests
{
    [Fact]
    public void NothingEndsUpBelowTheFloorAndHigherGroundIsUntouched()
    {
        // Raw heights are 1/128 world unit: 10, 50 and 90 world units.
        var heights = new ushort[] { 10 * 128, 50 * 128, 90 * 128 };
        SC2Collision2Floor.Apply(heights, 40f);
        Assert.Equal(new ushort[] { 40 * 128, 50 * 128, 90 * 128 }, heights);
    }

    [Fact]
    public void AFloorBelowZeroChangesNothing()
    {
        var heights = new ushort[] { 0, 128, 256 };
        SC2Collision2Floor.Apply(heights, -20f);
        Assert.Equal(new ushort[] { 0, 128, 256 }, heights);
    }

    [Fact]
    public void TheFloorSettingsSurviveTheDocumentRoundTrip()
    {
        var doc = new SC2MapDocument();
        doc.Collision2.FloorEnabled = true;
        doc.Collision2.FloorHeight = 123.5f;
        var back = SC2MapDocumentSerializer.Deserialize(SC2MapDocumentSerializer.Serialize(doc));
        Assert.True(back.Collision2.FloorEnabled);
        Assert.Equal(123.5f, back.Collision2.FloorHeight);
    }

    [Fact]
    public void ADocumentWithoutTheFloorHasItOff()
    {
        var back = SC2MapDocumentSerializer.Deserialize(SC2MapDocumentSerializer.Serialize(new SC2MapDocument()));
        Assert.False(back.Collision2.FloorEnabled);
    }

    [Fact]
    public void TheCustomAreaSurvivesTheDocumentRoundTrip()
    {
        var doc = new SC2MapDocument();
        doc.Collision2.CustomArea = true;
        doc.Collision2.AreaMinX = -50f; doc.Collision2.AreaMinZ = 10f;
        doc.Collision2.AreaSizeX = 2048f; doc.Collision2.AreaSizeZ = 1536f;
        var back = SC2MapDocumentSerializer.Deserialize(SC2MapDocumentSerializer.Serialize(doc));
        Assert.True(back.Collision2.CustomArea);
        Assert.Equal(-50f, back.Collision2.AreaMinX);
        Assert.Equal(10f, back.Collision2.AreaMinZ);
        Assert.Equal(2048f, back.Collision2.AreaSizeX);
        Assert.Equal(1536f, back.Collision2.AreaSizeZ);
    }

    [Fact]
    public void TheExtentsCommandIsOneUndoStepForTheWholeEdit()
    {
        var document = new SC2MapDocument();
        var history = new SC2CommandHistory();
        var edited = new SC2CollisionExtents(true, 40f, true, -10f, -10f, 1100f, 900f);

        history.Execute(document, new SC2SetCollision2ExtentsCommand(edited));
        Assert.True(document.Collision2.FloorEnabled);
        Assert.Equal(40f, document.Collision2.FloorHeight);
        Assert.Equal(900f, document.Collision2.AreaSizeZ);

        history.Undo(document);
        Assert.False(document.Collision2.FloorEnabled);
        Assert.False(document.Collision2.CustomArea);
        Assert.Equal(0f, document.Collision2.AreaSizeX);

        history.Redo(document);
        Assert.True(SC2CollisionExtents.From(document.Collision2).Equals(edited));
    }
}
