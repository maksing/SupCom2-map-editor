using System.IO;
using SC2.MapEditor.Document;
using SC2.MapEditor.SmartExport;

namespace SC2CharacterizationTests;

public class SessionTests
{
    [Fact]
    public void Session_WrapsDocument()
    {
        var document = new SC2MapDocument();
        var session = new SC2MapSession(document);

        Assert.Same(document, session.Document);
        Assert.NotNull(session.Selection);
        Assert.NotNull(session.Workspace);
        Assert.NotNull(session.Operation);
        Assert.NotNull(session.History);
        Assert.NotNull(session.ScenePreview);
    }

    [Fact]
    public void SetDocument_ReplacesDocumentAndResetsDocumentScopedState()
    {
        var original = new SC2MapDocument();
        var session = new SC2MapSession(original);
        session.Selection.Set("Markers", new[] { 1, 2 });
        session.Operation.Begin("Import");
        session.Operation.Warnings.Add("something");
        session.Workspace.ActiveTool = "Move";
        session.Workspace.OverlayGraphs = false;

        var replacement = new SC2MapDocument();
        session.SetDocument(replacement);

        Assert.Same(replacement, session.Document);
        Assert.True(session.Selection.IsEmpty);
        Assert.False(session.History.CanUndo);
        Assert.False(session.Operation.IsRunning);
        Assert.Empty(session.Operation.Warnings);

        // Workspace (UI chrome) is NOT document-scoped and must survive the swap.
        Assert.Equal("Move", session.Workspace.ActiveTool);
        Assert.False(session.Workspace.OverlayGraphs);
    }

    [Fact]
    public void SetDocument_RejectsNull()
    {
        var session = new SC2MapSession(new SC2MapDocument());
        Assert.Throws<System.ArgumentNullException>(() => session.SetDocument(null));
    }

    [Fact]
    public void PendingDirtyFlags_StartsAtNone()
    {
        var session = new SC2MapSession(new SC2MapDocument());
        Assert.Equal(SC2DirtyFlags.None, session.PendingDirtyFlags);
    }

    [Fact]
    public void PendingDirtyFlags_AccumulatesRealCommandDirtyFlagsExpanded()
    {
        var session = new SC2MapSession(new SC2MapDocument());

        session.History.Execute(session.Document,
            new SC2SetEnvironmentTexturePathCommand(SC2EnvironmentTextureSlot.SkyMap, "sky.dds"));

        // ExpandStaleFlags(Info) == Info (Environment is a leaf domain, per
        // SC2SetEnvironmentTexturePathCommand's own doc comment) — no
        // dependents get pulled in.
        Assert.Equal(SC2DirtyFlags.Info, session.PendingDirtyFlags);
    }

    [Fact]
    public void PendingDirtyFlags_MultipleCommandsOrTogether()
    {
        var session = new SC2MapSession(new SC2MapDocument());

        session.History.Execute(session.Document,
            new SC2SetEnvironmentTexturePathCommand(SC2EnvironmentTextureSlot.SkyMap, "sky.dds"));
        session.History.Execute(session.Document,
            new SC2SetEnvironmentTexturePathCommand(SC2EnvironmentTextureSlot.TexturePath3, "tex3.dds"));

        Assert.Equal(SC2DirtyFlags.Info, session.PendingDirtyFlags);
        Assert.True((session.PendingDirtyFlags & SC2DirtyFlags.Info) != 0);
    }

    [Fact]
    public void MarkExported_ResetsPendingDirtyFlagsToNone()
    {
        var session = new SC2MapSession(new SC2MapDocument());
        session.History.Execute(session.Document,
            new SC2SetEnvironmentTexturePathCommand(SC2EnvironmentTextureSlot.SkyMap, "sky.dds"));
        Assert.NotEqual(SC2DirtyFlags.None, session.PendingDirtyFlags);

        session.MarkExported();

        Assert.Equal(SC2DirtyFlags.None, session.PendingDirtyFlags);
    }

    [Fact]
    public void SetDocument_ResetsPendingDirtyFlags_AndTracksTheNewDocument()
    {
        var session = new SC2MapSession(new SC2MapDocument());
        session.History.Execute(session.Document,
            new SC2SetEnvironmentTexturePathCommand(SC2EnvironmentTextureSlot.SkyMap, "sky.dds"));
        Assert.NotEqual(SC2DirtyFlags.None, session.PendingDirtyFlags);

        var replacement = new SC2MapDocument();
        session.SetDocument(replacement);
        Assert.Equal(SC2DirtyFlags.None, session.PendingDirtyFlags);

        session.History.Execute(session.Document,
            new SC2SetEnvironmentTexturePathCommand(SC2EnvironmentTextureSlot.EnvironmentMap, "env.dds"));
        Assert.Equal(SC2DirtyFlags.Info, session.PendingDirtyFlags);
    }

    private static SC2DirectoryBlobStore NewStore()
    {
        var root = Directory.CreateTempSubdirectory("sc2sessionblobs_").FullName;
        return new SC2DirectoryBlobStore(root);
    }

    [Fact]
    public void DerivedState_ChangedDomain_BecomesDirty()
    {
        var session = new SC2MapSession(new SC2MapDocument());

        session.History.Execute(session.Document,
            new SC2SetCollision2VerticalOffsetCommand(0.5f));

        Assert.Equal(SC2DerivedState.Dirty, session.Document.Collision2.State);
    }

    [Fact]
    public void DerivedState_InvalidatedDomains_BecomeStale_UnlessAlreadyDirty()
    {
        var store = NewStore();
        var session = new SC2MapSession(new SC2MapDocument());

        session.History.Execute(session.Document,
            new SC2SetTerrainHeightsCommand(new byte[16], 0f, 10f, store));

        // Hfield/Costs/Collision2 were only INVALIDATED (upstream Terrain
        // geometry changed), not directly edited — Stale, not Dirty.
        Assert.Equal(SC2DerivedState.Stale, session.Document.Hfield.State);
        Assert.Equal(SC2DerivedState.Stale, session.Document.Costs.State);
        Assert.Equal(SC2DerivedState.Stale, session.Document.Collision2.State);
    }

    [Fact]
    public void DerivedState_DirtyTakesPrecedenceOverStale()
    {
        var store = NewStore();
        var session = new SC2MapSession(new SC2MapDocument());

        // Directly edit Collision2 first (Dirty)...
        session.History.Execute(session.Document, new SC2SetCollision2VerticalOffsetCommand(0.5f));
        Assert.Equal(SC2DerivedState.Dirty, session.Document.Collision2.State);

        // ...then a Terrain height edit invalidates it too (would normally
        // set Stale) — Dirty must NOT be downgraded to Stale.
        session.History.Execute(session.Document, new SC2SetTerrainHeightsCommand(new byte[16], 0f, 10f, store));
        Assert.Equal(SC2DerivedState.Dirty, session.Document.Collision2.State);
    }

    [Fact]
    public void MarkExported_FlipsDirtyAndStaleBackToCurrent()
    {
        var store = NewStore();
        var session = new SC2MapSession(new SC2MapDocument());
        session.History.Execute(session.Document, new SC2SetTerrainHeightsCommand(new byte[16], 0f, 10f, store));
        Assert.Equal(SC2DerivedState.Stale, session.Document.Hfield.State);
        Assert.Equal(SC2DerivedState.Stale, session.Document.Costs.State);
        Assert.Equal(SC2DerivedState.Stale, session.Document.Collision2.State);

        session.MarkExported();

        Assert.Equal(SC2DerivedState.Current, session.Document.Hfield.State);
        Assert.Equal(SC2DerivedState.Current, session.Document.Costs.State);
        Assert.Equal(SC2DerivedState.Current, session.Document.Collision2.State);
    }

    [Fact]
    public void MarkExported_LeavesUntouchedDomainsAlone()
    {
        var session = new SC2MapSession(new SC2MapDocument());
        // Never touched — starts at the enum's default value.
        var before = session.Document.Scenario.State;

        session.MarkExported();

        Assert.Equal(before, session.Document.Scenario.State);
    }

    [Fact]
    public void SelectionState_SetThenContainsAndClear()
    {
        var selection = new SC2SelectionState();
        Assert.True(selection.IsEmpty);

        selection.Set("Markers", new[] { 1, 2, 3 });

        Assert.False(selection.IsEmpty);
        Assert.Equal("Markers", selection.Kind);
        Assert.True(selection.Contains(2));
        Assert.False(selection.Contains(99));

        selection.Clear();
        Assert.True(selection.IsEmpty);
        Assert.Equal("", selection.Kind);
    }

    [Fact]
    public void SelectionState_SetReplacesPreviousSelectionEntirely()
    {
        var selection = new SC2SelectionState();
        selection.Set("Markers", new[] { 1, 2 });
        selection.Set("Props", new[] { 5 });

        Assert.Equal("Props", selection.Kind);
        Assert.False(selection.Contains(1));
        Assert.True(selection.Contains(5));
    }

    [Fact]
    public void OperationState_BeginReportFinish_TracksProgress()
    {
        var op = new SC2OperationState();
        op.Begin("Export");

        Assert.True(op.IsRunning);
        Assert.Equal("Export", op.OperationName);
        Assert.Equal(0f, op.Progress01);

        op.Report(0.5f, "Halfway there");
        Assert.Equal(0.5f, op.Progress01);
        Assert.Equal("Halfway there", op.StatusMessage);

        op.Finish();
        Assert.False(op.IsRunning);
        Assert.Equal(1f, op.Progress01);
    }

    [Fact]
    public void OperationState_Begin_ClearsPreviousWarningsAndErrors()
    {
        var op = new SC2OperationState();
        op.Begin("First");
        op.Warnings.Add("warn 1");
        op.Errors.Add("err 1");

        op.Begin("Second");

        Assert.Empty(op.Warnings);
        Assert.Empty(op.Errors);
    }

    [Fact]
    public void WorkspaceState_HasSensibleDefaults()
    {
        var workspace = new SC2WorkspaceState();

        Assert.Equal("Select", workspace.ActiveTool);
        Assert.True(workspace.OverlayObjects);
        Assert.True(workspace.OverlayMarkers);
        Assert.True(workspace.OverlayGraphs);
        Assert.True(workspace.OverlayAreas);
        Assert.Equal(UnityEngine.Vector3.zero, workspace.CameraFocusPoint);
    }

    [Fact]
    public void SceneProjection_StartsInactive_AndTogglesExplicitly()
    {
        var projection = new SC2SceneProjection();
        Assert.False(projection.IsActive);

        projection.MarkActive();
        Assert.True(projection.IsActive);

        projection.MarkInactive();
        Assert.False(projection.IsActive);
    }
}
