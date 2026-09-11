using SC2.MapEditor.Document;
using SC2.MapEditor.SmartExport;

namespace SC2CharacterizationTests;

public class ScenarioCommandTests
{
    [Fact]
    public void Execute_SetsScenarioName()
    {
        var document = new SC2MapDocument();
        var history = new SC2CommandHistory();

        history.Execute(document, new SC2SetScenarioNameCommand("My Map"));

        Assert.Equal("My Map", document.Scenario.KnownFields.Name);
    }

    [Fact]
    public void Undo_RestoresThePreviousName()
    {
        var document = new SC2MapDocument();
        document.Scenario.KnownFields.Name = "Original";
        var history = new SC2CommandHistory();

        history.Execute(document, new SC2SetScenarioNameCommand("New Name"));
        history.Undo(document);

        Assert.Equal("Original", document.Scenario.KnownFields.Name);
        Assert.False(history.CanUndo);
        Assert.True(history.CanRedo);
    }

    [Fact]
    public void Redo_ReappliesTheNewName()
    {
        var document = new SC2MapDocument();
        var history = new SC2CommandHistory();

        history.Execute(document, new SC2SetScenarioNameCommand("New Name"));
        history.Undo(document);
        history.Redo(document);

        Assert.Equal("New Name", document.Scenario.KnownFields.Name);
    }

    [Fact]
    public void Execute_NullValueIsTreatedAsEmptyString()
    {
        var document = new SC2MapDocument();
        var history = new SC2CommandHistory();

        history.Execute(document, new SC2SetScenarioNameCommand(null));

        Assert.Equal("", document.Scenario.KnownFields.Name);
    }

    [Fact]
    public void Do_ReturnsChangeSetWithScenarioLuaDirtyFlagAndDomain()
    {
        var document = new SC2MapDocument();
        var command = new SC2SetScenarioNameCommand("New Name");

        var changeSet = command.Do(document);

        Assert.Equal(SC2DirtyFlags.ScenarioLua, changeSet.DirectDirtyFlags);
        Assert.Contains(SC2DomainId.Scenario, changeSet.ChangedDomains);
        Assert.Empty(changeSet.InvalidatedDomains);
        Assert.Equal("Set Scenario.Name", changeSet.CommandName);
    }
}

public class ScenarioValidatorTests
{
    private static SC2ScenarioDocument WellFormedDoc() => new()
    {
        KnownFields = new SC2ScenarioKnownFields
        {
            Name = "SC2_TEST",
            MapPath = "/maps/SC2_TEST/SC2_TEST.scmap",
            SavePath = "/maps/SC2_TEST/SC2_TEST_save.lua",
            ScriptPath = "/maps/SC2_TEST/SC2_TEST_script.lua",
        }
    };

    [Fact]
    public void Validate_WarnsOnEmptyName()
    {
        var doc = WellFormedDoc();
        doc.KnownFields.Name = "";

        var report = SC2ScenarioValidator.Validate(doc);

        Assert.Contains(report.Messages, m => m.Code == "SCN001" && m.Severity == SC2ValidationSeverity.Warning);
    }

    [Fact]
    public void Validate_ErrorsOnEmptyMapPath()
    {
        var doc = WellFormedDoc();
        doc.KnownFields.MapPath = "";

        var report = SC2ScenarioValidator.Validate(doc);

        Assert.Contains(report.Messages, m => m.Code == "SCN002" && m.Severity == SC2ValidationSeverity.Error);
    }

    [Fact]
    public void Validate_ErrorsOnEmptySavePath()
    {
        var doc = WellFormedDoc();
        doc.KnownFields.SavePath = "   ";

        var report = SC2ScenarioValidator.Validate(doc);

        Assert.Contains(report.Messages, m => m.Code == "SCN003" && m.Severity == SC2ValidationSeverity.Error);
    }

    [Fact]
    public void Validate_ErrorsOnEmptyScriptPath()
    {
        var doc = WellFormedDoc();
        doc.KnownFields.ScriptPath = "";

        var report = SC2ScenarioValidator.Validate(doc);

        Assert.Contains(report.Messages, m => m.Code == "SCN004" && m.Severity == SC2ValidationSeverity.Error);
    }

    [Fact]
    public void Validate_NoMessagesForWellFormedDocument()
    {
        var report = SC2ScenarioValidator.Validate(WellFormedDoc());
        Assert.Empty(report.Messages);
    }

    [Fact]
    public void Validate_NullDocumentReturnsEmptyReport()
    {
        var report = SC2ScenarioValidator.Validate(null);
        Assert.Empty(report.Messages);
    }
}
