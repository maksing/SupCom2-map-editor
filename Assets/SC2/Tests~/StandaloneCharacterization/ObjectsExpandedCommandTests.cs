using System;
using SC2.MapEditor.Document;
using SC2.MapEditor.SmartExport;
using UnityEngine;

namespace SC2CharacterizationTests;

public class PropPositionCommandTests
{
    [Fact]
    public void Execute_MovesTheTargetedPropOnly()
    {
        var document = new SC2MapDocument();
        document.Objects.Props.Add(new SC2ObjProp { Id = 1, Position = new Vector3(1, 1, 1) });
        document.Objects.Props.Add(new SC2ObjProp { Id = 2, Position = new Vector3(2, 2, 2) });
        var history = new SC2CommandHistory();

        history.Execute(document, new SC2SetPropPositionCommand(1, new Vector3(9, 9, 9)));

        Assert.Equal(new Vector3(9, 9, 9), document.Objects.Props[0].Position);
        Assert.Equal(new Vector3(2, 2, 2), document.Objects.Props[1].Position);
    }

    [Fact]
    public void Undo_RestoresThePreviousPosition()
    {
        var document = new SC2MapDocument();
        document.Objects.Props.Add(new SC2ObjProp { Id = 1, Position = new Vector3(1, 1, 1) });
        var history = new SC2CommandHistory();

        history.Execute(document, new SC2SetPropPositionCommand(1, new Vector3(9, 9, 9)));
        history.Undo(document);

        Assert.Equal(new Vector3(1, 1, 1), document.Objects.Props[0].Position);
    }

    [Fact]
    public void Do_DoesNotInvalidateScenario()
    {
        var document = new SC2MapDocument();
        document.Objects.Props.Add(new SC2ObjProp { Id = 1 });
        var command = new SC2SetPropPositionCommand(1, Vector3.zero);

        var changeSet = command.Do(document);

        Assert.Equal(SC2DirtyFlags.Objects, changeSet.DirectDirtyFlags);
        Assert.Empty(changeSet.InvalidatedDomains);
    }

    [Fact]
    public void Do_ThrowsWhenPropIdIsNotFound()
    {
        var document = new SC2MapDocument();
        Assert.Throws<InvalidOperationException>(() => new SC2SetPropPositionCommand(999, Vector3.zero).Do(document));
    }
}

public class ZoneRectCommandTests
{
    [Fact]
    public void Execute_SetsAllFourCoordinates()
    {
        var document = new SC2MapDocument();
        document.Objects.Zones.Add(new SC2ObjZone { Id = 1, X1 = 0, Z1 = 0, X2 = 10, Z2 = 10 });
        var history = new SC2CommandHistory();

        history.Execute(document, new SC2SetZoneRectCommand(1, 5, 6, 50, 60));

        var zone = document.Objects.Zones[0];
        Assert.Equal((5f, 6f, 50f, 60f), (zone.X1, zone.Z1, zone.X2, zone.Z2));
    }

    [Fact]
    public void Undo_RestoresThePreviousRect()
    {
        var document = new SC2MapDocument();
        document.Objects.Zones.Add(new SC2ObjZone { Id = 1, X1 = 0, Z1 = 0, X2 = 10, Z2 = 10 });
        var history = new SC2CommandHistory();

        history.Execute(document, new SC2SetZoneRectCommand(1, 5, 6, 50, 60));
        history.Undo(document);

        var zone = document.Objects.Zones[0];
        Assert.Equal((0f, 0f, 10f, 10f), (zone.X1, zone.Z1, zone.X2, zone.Z2));
    }

    [Fact]
    public void Do_ThrowsWhenZoneIdIsNotFound()
    {
        var document = new SC2MapDocument();
        Assert.Throws<InvalidOperationException>(() => new SC2SetZoneRectCommand(999, 0, 0, 1, 1).Do(document));
    }
}

public class ChainNameCommandTests
{
    [Fact]
    public void Execute_RenamesTheChain()
    {
        var document = new SC2MapDocument();
        document.Objects.Chains.Add(new SC2ObjChain { Id = 1, Name = "Old" });
        var history = new SC2CommandHistory();

        history.Execute(document, new SC2SetChainNameCommand(1, "New"));

        Assert.Equal("New", document.Objects.Chains[0].Name);
    }

    [Fact]
    public void Undo_RestoresThePreviousName()
    {
        var document = new SC2MapDocument();
        document.Objects.Chains.Add(new SC2ObjChain { Id = 1, Name = "Old" });
        var history = new SC2CommandHistory();

        history.Execute(document, new SC2SetChainNameCommand(1, "New"));
        history.Undo(document);

        Assert.Equal("Old", document.Objects.Chains[0].Name);
    }
}

public class ArmyNameCommandTests
{
    [Fact]
    public void Execute_RenamesTheArmy()
    {
        var document = new SC2MapDocument();
        document.Objects.Armies.Add(new SC2ObjArmy { Id = 1, Name = "Old" });
        var history = new SC2CommandHistory();

        history.Execute(document, new SC2SetArmyNameCommand(1, "New"));

        Assert.Equal("New", document.Objects.Armies[0].Name);
    }

    [Fact]
    public void Undo_RestoresThePreviousName()
    {
        var document = new SC2MapDocument();
        document.Objects.Armies.Add(new SC2ObjArmy { Id = 1, Name = "Old" });
        var history = new SC2CommandHistory();

        history.Execute(document, new SC2SetArmyNameCommand(1, "New"));
        history.Undo(document);

        Assert.Equal("Old", document.Objects.Armies[0].Name);
    }
}

public class UnitPositionCommandTests
{
    [Fact]
    public void Execute_MovesTheTargetedUnitOnly()
    {
        var document = new SC2MapDocument();
        document.Objects.Units.Add(new SC2ObjUnitNode { Id = 1, Position = new Vector3(1, 1, 1) });
        document.Objects.Units.Add(new SC2ObjUnitNode { Id = 2, Position = new Vector3(2, 2, 2) });
        var history = new SC2CommandHistory();

        history.Execute(document, new SC2SetUnitPositionCommand(1, new Vector3(9, 9, 9)));

        Assert.Equal(new Vector3(9, 9, 9), document.Objects.Units[0].Position);
        Assert.Equal(new Vector3(2, 2, 2), document.Objects.Units[1].Position);
    }

    [Fact]
    public void Undo_RestoresThePreviousPosition()
    {
        var document = new SC2MapDocument();
        document.Objects.Units.Add(new SC2ObjUnitNode { Id = 1, Position = new Vector3(1, 1, 1) });
        var history = new SC2CommandHistory();

        history.Execute(document, new SC2SetUnitPositionCommand(1, new Vector3(9, 9, 9)));
        history.Undo(document);

        Assert.Equal(new Vector3(1, 1, 1), document.Objects.Units[0].Position);
    }
}

public class GraphNameCommandTests
{
    [Fact]
    public void Execute_RenamesTheGraph()
    {
        var document = new SC2MapDocument();
        document.Objects.Graphs.Add(new SC2ObjGraph { Id = 1, Name = "Old" });
        var history = new SC2CommandHistory();

        history.Execute(document, new SC2SetGraphNameCommand(1, "New"));

        Assert.Equal("New", document.Objects.Graphs[0].Name);
    }

    [Fact]
    public void Undo_RestoresThePreviousName()
    {
        var document = new SC2MapDocument();
        document.Objects.Graphs.Add(new SC2ObjGraph { Id = 1, Name = "Old" });
        var history = new SC2CommandHistory();

        history.Execute(document, new SC2SetGraphNameCommand(1, "New"));
        history.Undo(document);

        Assert.Equal("Old", document.Objects.Graphs[0].Name);
    }
}

public class SymmetryGroupKindCommandTests
{
    [Fact]
    public void Execute_SetsKind()
    {
        var document = new SC2MapDocument();
        document.Objects.SymmetryGroups.Add(new SC2ObjSymmetryGroup { Id = 1, Kind = "Marker" });
        var history = new SC2CommandHistory();

        history.Execute(document, new SC2SetSymmetryGroupKindCommand(1, "Prop"));

        Assert.Equal("Prop", document.Objects.SymmetryGroups[0].Kind);
    }

    [Fact]
    public void Undo_RestoresThePreviousKind()
    {
        var document = new SC2MapDocument();
        document.Objects.SymmetryGroups.Add(new SC2ObjSymmetryGroup { Id = 1, Kind = "Marker" });
        var history = new SC2CommandHistory();

        history.Execute(document, new SC2SetSymmetryGroupKindCommand(1, "Prop"));
        history.Undo(document);

        Assert.Equal("Marker", document.Objects.SymmetryGroups[0].Kind);
    }
}

public class ObjectsValidatorZoneTests
{
    [Fact]
    public void Validate_WarnsOnDegenerateZoneRect()
    {
        var doc = new SC2ObjectsDocument();
        doc.Zones.Add(new SC2ObjZone { Id = 1, Name = "Z", X1 = 10, Z1 = 0, X2 = 5, Z2 = 20 });

        var report = SC2ObjectsValidator.Validate(doc);

        Assert.Contains(report.Messages, m => m.Code == "OBJ003" && m.Severity == SC2ValidationSeverity.Warning);
    }

    [Fact]
    public void Validate_NoWarningForWellFormedZoneRect()
    {
        var doc = new SC2ObjectsDocument();
        doc.Zones.Add(new SC2ObjZone { Id = 1, Name = "Z", X1 = 0, Z1 = 0, X2 = 10, Z2 = 10 });

        var report = SC2ObjectsValidator.Validate(doc);

        Assert.DoesNotContain(report.Messages, m => m.Code == "OBJ003");
    }
}
