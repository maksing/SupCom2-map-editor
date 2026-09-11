using SC2.MapEditor.Document;
using SC2.MapEditor.SmartExport;

namespace SC2CharacterizationTests;

public class DecalsCommandTests
{
    [Theory]
    [InlineData(SC2DecalsAtlasSlot.Diffuse)]
    [InlineData(SC2DecalsAtlasSlot.Normal)]
    public void Execute_SetsTheTargetedSlotOnly(SC2DecalsAtlasSlot slot)
    {
        var document = new SC2MapDocument();
        var history = new SC2CommandHistory();

        history.Execute(document, new SC2SetDecalsAtlasPathCommand(slot, "/env/decals/atlas.dds"));

        var (diffuse, normal) = (document.Decals.DiffuseAtlasPath, document.Decals.NormalAtlasPath);
        if (slot == SC2DecalsAtlasSlot.Diffuse)
        {
            Assert.Equal("/env/decals/atlas.dds", diffuse);
            Assert.Equal("", normal);
        }
        else
        {
            Assert.Equal("", diffuse);
            Assert.Equal("/env/decals/atlas.dds", normal);
        }
    }

    [Fact]
    public void Undo_RestoresThePreviousValue()
    {
        var document = new SC2MapDocument();
        document.Decals.DiffuseAtlasPath = "/original.dds";
        var history = new SC2CommandHistory();

        history.Execute(document, new SC2SetDecalsAtlasPathCommand(SC2DecalsAtlasSlot.Diffuse, "/new.dds"));
        history.Undo(document);

        Assert.Equal("/original.dds", document.Decals.DiffuseAtlasPath);
        Assert.False(history.CanUndo);
        Assert.True(history.CanRedo);
    }

    [Fact]
    public void Redo_ReappliesTheNewValue()
    {
        var document = new SC2MapDocument();
        var history = new SC2CommandHistory();

        history.Execute(document, new SC2SetDecalsAtlasPathCommand(SC2DecalsAtlasSlot.Normal, "/new.dds"));
        history.Undo(document);
        history.Redo(document);

        Assert.Equal("/new.dds", document.Decals.NormalAtlasPath);
    }

    [Fact]
    public void Execute_NullValueIsTreatedAsEmptyString()
    {
        var document = new SC2MapDocument();
        var history = new SC2CommandHistory();

        history.Execute(document, new SC2SetDecalsAtlasPathCommand(SC2DecalsAtlasSlot.Diffuse, null));

        Assert.Equal("", document.Decals.DiffuseAtlasPath);
    }

    [Fact]
    public void Do_ReturnsChangeSetWithDecalsDirtyFlagAndDomain()
    {
        var document = new SC2MapDocument();
        var command = new SC2SetDecalsAtlasPathCommand(SC2DecalsAtlasSlot.Diffuse, "/new.dds");

        var changeSet = command.Do(document);

        Assert.Equal(SC2DirtyFlags.Decals, changeSet.DirectDirtyFlags);
        Assert.Contains(SC2DomainId.Decals, changeSet.ChangedDomains);
        Assert.Empty(changeSet.InvalidatedDomains);
        Assert.Equal("Set Decals.DiffuseAtlasPath", changeSet.CommandName);
    }
}

public class DecalsValidatorTests
{
    [Fact]
    public void Validate_WarnsWhenHasDecalsButDiffusePathIsEmpty()
    {
        var doc = new SC2DecalsDocument { HasDecals = true, DiffuseAtlasPath = "" };
        var report = SC2DecalsValidator.Validate(doc);

        Assert.Contains(report.Messages, m => m.Code == "DEC001" && m.Severity == SC2ValidationSeverity.Warning);
    }

    [Fact]
    public void Validate_NoWarningWhenNoDecalsAndDiffusePathIsEmpty()
    {
        var doc = new SC2DecalsDocument { HasDecals = false, DiffuseAtlasPath = "" };
        var report = SC2DecalsValidator.Validate(doc);

        Assert.Empty(report.Messages);
    }

    [Fact]
    public void Validate_NoWarningWhenHasDecalsAndDiffusePathIsSet()
    {
        var doc = new SC2DecalsDocument { HasDecals = true, DiffuseAtlasPath = "/atlas.dds" };
        var report = SC2DecalsValidator.Validate(doc);

        Assert.Empty(report.Messages);
    }

    [Fact]
    public void Validate_NullDocumentReturnsEmptyReport()
    {
        var report = SC2DecalsValidator.Validate(null);
        Assert.Empty(report.Messages);
    }
}
