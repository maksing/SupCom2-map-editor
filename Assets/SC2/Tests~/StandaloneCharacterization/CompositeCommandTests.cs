using System.Collections.Generic;
using SC2.MapEditor.Document;
using SC2.MapEditor.SmartExport;
using UnityEngine;

namespace SC2CharacterizationTests;

public class CompositeCommandTests
{
    private static SC2MapDocument DocumentWithMarkerAndProp(out SC2ObjMarker marker, out SC2ObjProp prop)
    {
        var document = new SC2MapDocument();
        marker = new SC2ObjMarker { Id = 1, Position = new Vector3(1, 0, 1) };
        prop = new SC2ObjProp { Id = 2, Position = new Vector3(2, 0, 2) };
        document.Objects.Markers.Add(marker);
        document.Objects.Props.Add(prop);
        return document;
    }

    [Fact]
    public void Execute_AppliesEverySubCommand()
    {
        var document = DocumentWithMarkerAndProp(out _, out _);
        var history = new SC2CommandHistory();
        var composite = new SC2CompositeCommand("Move object(s)", new List<ISC2DocumentCommand>
        {
            new SC2SetMarkerPositionCommand(1, new Vector3(9, 0, 9)),
            new SC2SetPropPositionCommand(2, new Vector3(8, 0, 8)),
        });

        history.Execute(document, composite);

        Assert.Equal(new Vector3(9, 0, 9), document.Objects.Markers[0].Position);
        Assert.Equal(new Vector3(8, 0, 8), document.Objects.Props[0].Position);
    }

    [Fact]
    public void Undo_RevertsEverySubCommandInOneStep()
    {
        var document = DocumentWithMarkerAndProp(out var marker, out var prop);
        var originalMarkerPos = marker.Position;
        var originalPropPos = prop.Position;
        var history = new SC2CommandHistory();
        var composite = new SC2CompositeCommand("Move object(s)", new List<ISC2DocumentCommand>
        {
            new SC2SetMarkerPositionCommand(1, new Vector3(9, 0, 9)),
            new SC2SetPropPositionCommand(2, new Vector3(8, 0, 8)),
        });
        history.Execute(document, composite);

        history.Undo(document);

        Assert.Equal(originalMarkerPos, document.Objects.Markers[0].Position);
        Assert.Equal(originalPropPos, document.Objects.Props[0].Position);
        Assert.False(history.CanUndo);
        Assert.True(history.CanRedo);
    }

    [Fact]
    public void Redo_ReappliesEverySubCommand()
    {
        var document = DocumentWithMarkerAndProp(out _, out _);
        var history = new SC2CommandHistory();
        var composite = new SC2CompositeCommand("Move object(s)", new List<ISC2DocumentCommand>
        {
            new SC2SetMarkerPositionCommand(1, new Vector3(9, 0, 9)),
            new SC2SetPropPositionCommand(2, new Vector3(8, 0, 8)),
        });
        history.Execute(document, composite);
        history.Undo(document);

        history.Redo(document);

        Assert.Equal(new Vector3(9, 0, 9), document.Objects.Markers[0].Position);
        Assert.Equal(new Vector3(8, 0, 8), document.Objects.Props[0].Position);
    }

    [Fact]
    public void Do_MergesDirtyFlagsAndChangedDomainsAcrossSubCommands()
    {
        var document = DocumentWithMarkerAndProp(out _, out _);
        var composite = new SC2CompositeCommand("Move object(s)", new List<ISC2DocumentCommand>
        {
            new SC2SetMarkerPositionCommand(1, new Vector3(9, 0, 9)),
            new SC2SetPropPositionCommand(2, new Vector3(8, 0, 8)),
        });

        var changeSet = composite.Do(document);

        Assert.Equal(SC2DirtyFlags.Objects, changeSet.DirectDirtyFlags);
        Assert.Contains(SC2DomainId.Objects, changeSet.ChangedDomains);
        // SC2SetMarkerPositionCommand alone invalidates Scenario (ARMY_N
        // StartPositions derive from markers); the merged set must still
        // carry that even though SC2SetPropPositionCommand's own
        // InvalidatedDomains is empty.
        Assert.Contains(SC2DomainId.Scenario, changeSet.InvalidatedDomains);
    }

    [Fact]
    public void Execute_RaisesChangedExactlyOnceForTheWholeBatch()
    {
        var document = DocumentWithMarkerAndProp(out _, out _);
        var history = new SC2CommandHistory();
        var raiseCount = 0;
        document.Changed += _ => raiseCount++;
        var composite = new SC2CompositeCommand("Move object(s)", new List<ISC2DocumentCommand>
        {
            new SC2SetMarkerPositionCommand(1, new Vector3(9, 0, 9)),
            new SC2SetPropPositionCommand(2, new Vector3(8, 0, 8)),
        });

        history.Execute(document, composite);

        Assert.Equal(1, raiseCount);
        Assert.Equal(1, document.Revision);
    }
}
