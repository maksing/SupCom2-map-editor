using SC2.MapEditor.Document;
using SC2.MapEditor.SmartExport;
using UnityEngine;

namespace SC2CharacterizationTests;

public class ObjectsCommandTests
{
    private static SC2MapDocument DocumentWithMarker(int id, Vector3 position)
    {
        var document = new SC2MapDocument();
        document.Objects.Markers.Add(new SC2ObjMarker { Id = id, Name = $"Marker{id}", Position = position });
        return document;
    }

    [Fact]
    public void Execute_MovesTheTargetedMarkerOnly()
    {
        var document = DocumentWithMarker(1, new Vector3(1, 2, 3));
        document.Objects.Markers.Add(new SC2ObjMarker { Id = 2, Name = "Marker2", Position = new Vector3(9, 9, 9) });
        var history = new SC2CommandHistory();

        history.Execute(document, new SC2SetMarkerPositionCommand(1, new Vector3(10, 20, 30)));

        Assert.Equal(new Vector3(10, 20, 30), document.Objects.Markers[0].Position);
        Assert.Equal(new Vector3(9, 9, 9), document.Objects.Markers[1].Position);
    }

    [Fact]
    public void Undo_RestoresThePreviousPosition()
    {
        var document = DocumentWithMarker(1, new Vector3(1, 2, 3));
        var history = new SC2CommandHistory();

        history.Execute(document, new SC2SetMarkerPositionCommand(1, new Vector3(10, 20, 30)));
        history.Undo(document);

        Assert.Equal(new Vector3(1, 2, 3), document.Objects.Markers[0].Position);
        Assert.False(history.CanUndo);
        Assert.True(history.CanRedo);
    }

    [Fact]
    public void Redo_ReappliesTheNewPosition()
    {
        var document = DocumentWithMarker(1, new Vector3(1, 2, 3));
        var history = new SC2CommandHistory();

        history.Execute(document, new SC2SetMarkerPositionCommand(1, new Vector3(10, 20, 30)));
        history.Undo(document);
        history.Redo(document);

        Assert.Equal(new Vector3(10, 20, 30), document.Objects.Markers[0].Position);
    }

    [Fact]
    public void Do_ReturnsChangeSetWithObjectsDirtyFlagAndInvalidatesScenario()
    {
        var document = DocumentWithMarker(1, Vector3.zero);
        var command = new SC2SetMarkerPositionCommand(1, new Vector3(5, 5, 5));

        var changeSet = command.Do(document);

        Assert.Equal(SC2DirtyFlags.Objects, changeSet.DirectDirtyFlags);
        Assert.Contains(SC2DomainId.Objects, changeSet.ChangedDomains);
        Assert.Contains(SC2DomainId.Scenario, changeSet.InvalidatedDomains);
        Assert.Equal("Move Marker #1", changeSet.CommandName);
    }

    [Fact]
    public void Do_ThrowsWhenMarkerIdIsNotFound()
    {
        var document = new SC2MapDocument();
        var command = new SC2SetMarkerPositionCommand(999, Vector3.zero);

        Assert.Throws<System.InvalidOperationException>(() => command.Do(document));
    }
}

public class ObjectsValidatorTests
{
    [Fact]
    public void Validate_ErrorsOnDuplicateIdAcrossDifferentListTypes()
    {
        var doc = new SC2ObjectsDocument();
        doc.Props.Add(new SC2ObjProp { Id = 1 });
        doc.Markers.Add(new SC2ObjMarker { Id = 1, Name = "M1" }); // same Id as the prop, different list

        var report = SC2ObjectsValidator.Validate(doc);

        Assert.Contains(report.Messages, m => m.Code == "OBJ001" && m.Severity == SC2ValidationSeverity.Error);
    }

    [Fact]
    public void Validate_NoErrorWhenIdsAreUniqueAcrossAllLists()
    {
        var doc = new SC2ObjectsDocument();
        doc.Props.Add(new SC2ObjProp { Id = 1 });
        doc.Markers.Add(new SC2ObjMarker { Id = 2, Name = "M1" });

        var report = SC2ObjectsValidator.Validate(doc);

        Assert.DoesNotContain(report.Messages, m => m.Code == "OBJ001");
    }

    [Fact]
    public void Validate_WarnsOnChainReferencingAMissingMarkerName()
    {
        var doc = new SC2ObjectsDocument();
        doc.Markers.Add(new SC2ObjMarker { Id = 1, Name = "ARMY_1" });
        doc.Chains.Add(new SC2ObjChain { Id = 10, Name = "Patrol", MarkerNames = { "ARMY_1", "ARMY_2_TYPO" } });

        var report = SC2ObjectsValidator.Validate(doc);

        Assert.Contains(report.Messages, m => m.Code == "OBJ002" && m.Severity == SC2ValidationSeverity.Warning && m.Message.Contains("ARMY_2_TYPO"));
        Assert.DoesNotContain(report.Messages, m => m.Code == "OBJ002" && m.Message.Contains("'ARMY_1'"));
    }

    [Fact]
    public void Validate_NoWarningsWhenAllChainMarkerNamesExist()
    {
        var doc = new SC2ObjectsDocument();
        doc.Markers.Add(new SC2ObjMarker { Id = 1, Name = "ARMY_1" });
        doc.Chains.Add(new SC2ObjChain { Id = 10, Name = "Patrol", MarkerNames = { "ARMY_1" } });

        var report = SC2ObjectsValidator.Validate(doc);

        Assert.Empty(report.Messages);
    }

    [Fact]
    public void Validate_NullDocumentReturnsEmptyReport()
    {
        var report = SC2ObjectsValidator.Validate(null);
        Assert.Empty(report.Messages);
    }
}
