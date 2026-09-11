using System.Linq;
using SC2.MapEditor.Document;
using SC2.MapEditor.SmartExport;

namespace SC2CharacterizationTests;

public class EnvironmentCommandTests
{
    [Theory]
    [InlineData(SC2EnvironmentTextureSlot.EnvironmentMap)]
    [InlineData(SC2EnvironmentTextureSlot.SkyMap)]
    [InlineData(SC2EnvironmentTextureSlot.TexturePath3)]
    [InlineData(SC2EnvironmentTextureSlot.ColorCorrectionCube)]
    [InlineData(SC2EnvironmentTextureSlot.FogOfWarCorrectionCube)]
    public void Execute_SetsTheTargetedSlotOnly(SC2EnvironmentTextureSlot slot)
    {
        var document = new SC2MapDocument();
        var history = new SC2CommandHistory();

        history.Execute(document, new SC2SetEnvironmentTexturePathCommand(slot, "/env/new/path.dds"));

        Assert.Equal("/env/new/path.dds", GetSlot(document.Environment, slot));
        foreach (var other in System.Enum.GetValues<SC2EnvironmentTextureSlot>())
            if (other != slot)
                Assert.Equal("", GetSlot(document.Environment, other));
    }

    [Fact]
    public void Undo_RestoresThePreviousValue()
    {
        var document = new SC2MapDocument();
        document.Environment.SkyMap = "/env/original/sky.dds";
        var history = new SC2CommandHistory();

        history.Execute(document, new SC2SetEnvironmentTexturePathCommand(SC2EnvironmentTextureSlot.SkyMap, "/env/new/sky.dds"));
        history.Undo(document);

        Assert.Equal("/env/original/sky.dds", document.Environment.SkyMap);
        Assert.False(history.CanUndo);
        Assert.True(history.CanRedo);
    }

    [Fact]
    public void Redo_ReappliesTheNewValue()
    {
        var document = new SC2MapDocument();
        var history = new SC2CommandHistory();

        history.Execute(document, new SC2SetEnvironmentTexturePathCommand(SC2EnvironmentTextureSlot.SkyMap, "/env/new/sky.dds"));
        history.Undo(document);
        history.Redo(document);

        Assert.Equal("/env/new/sky.dds", document.Environment.SkyMap);
    }

    [Fact]
    public void Execute_NullValueIsTreatedAsEmptyString()
    {
        var document = new SC2MapDocument();
        var history = new SC2CommandHistory();

        history.Execute(document, new SC2SetEnvironmentTexturePathCommand(SC2EnvironmentTextureSlot.SkyMap, null));

        Assert.Equal("", document.Environment.SkyMap);
    }

    [Fact]
    public void Do_ReturnsChangeSetWithInfoDirtyFlagAndEnvironmentDomain()
    {
        var document = new SC2MapDocument();
        var command = new SC2SetEnvironmentTexturePathCommand(SC2EnvironmentTextureSlot.SkyMap, "/env/new/sky.dds");

        var changeSet = command.Do(document);

        Assert.Equal(SC2DirtyFlags.Info, changeSet.DirectDirtyFlags);
        Assert.Contains(SC2DomainId.Environment, changeSet.ChangedDomains);
        Assert.Empty(changeSet.InvalidatedDomains);
        Assert.Equal("Set Environment.SkyMap", changeSet.CommandName);
    }

    [Fact]
    public void Execute_RaisesChangedWithMatchingRevisions()
    {
        var document = new SC2MapDocument();
        var history = new SC2CommandHistory();
        SC2DocumentChangeSet observed = null;
        document.Changed += cs => observed = cs;

        history.Execute(document, new SC2SetEnvironmentTexturePathCommand(SC2EnvironmentTextureSlot.SkyMap, "/env/new/sky.dds"));

        Assert.NotNull(observed);
        Assert.Equal(0, observed.BeforeRevision);
        Assert.Equal(1, observed.AfterRevision);
        Assert.Equal(1, document.Revision);
    }

    private static string GetSlot(SC2EnvironmentDocument doc, SC2EnvironmentTextureSlot slot) => slot switch
    {
        SC2EnvironmentTextureSlot.EnvironmentMap => doc.EnvironmentMap,
        SC2EnvironmentTextureSlot.SkyMap => doc.SkyMap,
        SC2EnvironmentTextureSlot.TexturePath3 => doc.TexturePath3,
        SC2EnvironmentTextureSlot.ColorCorrectionCube => doc.ColorCorrectionCube,
        SC2EnvironmentTextureSlot.FogOfWarCorrectionCube => doc.FogOfWarCorrectionCube,
        _ => throw new System.ArgumentOutOfRangeException(nameof(slot)),
    };
}

public class EnvironmentValidatorTests
{
    [Fact]
    public void Validate_WarnsWhenEnvironmentMapIsEmpty()
    {
        var doc = new SC2EnvironmentDocument { EnvironmentMap = "", SkyMap = "/env/sky.dds" };
        var report = SC2EnvironmentValidator.Validate(doc);

        Assert.Contains(report.Messages, m => m.Code == "ENV001" && m.Severity == SC2ValidationSeverity.Warning);
        Assert.DoesNotContain(report.Messages, m => m.Code == "ENV002");
    }

    [Fact]
    public void Validate_WarnsWhenSkyMapIsEmpty()
    {
        var doc = new SC2EnvironmentDocument { EnvironmentMap = "/env/env.dds", SkyMap = "   " };
        var report = SC2EnvironmentValidator.Validate(doc);

        Assert.Contains(report.Messages, m => m.Code == "ENV002" && m.Severity == SC2ValidationSeverity.Warning);
        Assert.DoesNotContain(report.Messages, m => m.Code == "ENV001");
    }

    [Fact]
    public void Validate_NoWarningsWhenBothPathsAreSet()
    {
        var doc = new SC2EnvironmentDocument { EnvironmentMap = "/env/env.dds", SkyMap = "/env/sky.dds" };
        var report = SC2EnvironmentValidator.Validate(doc);

        Assert.Empty(report.Messages);
        Assert.False(report.HasWarnings);
        Assert.False(report.HasErrors);
    }

    [Fact]
    public void Validate_NullDocumentReturnsEmptyReport()
    {
        var report = SC2EnvironmentValidator.Validate(null);
        Assert.Empty(report.Messages);
    }
}
