using SC2.MapEditor.Document;
using SC2.MapEditor.SmartExport;

namespace SC2CharacterizationTests;

public class Collision2CommandTests
{
    [Fact]
    public void Execute_SetsVerticalOffset()
    {
        var document = new SC2MapDocument();
        var history = new SC2CommandHistory();

        history.Execute(document, new SC2SetCollision2VerticalOffsetCommand(1.5f));

        Assert.Equal(1.5f, document.Collision2.Generation.VerticalOffset);
    }

    [Fact]
    public void Undo_RestoresThePreviousValue()
    {
        var document = new SC2MapDocument();
        document.Collision2.Generation.VerticalOffset = 0.25f;
        var history = new SC2CommandHistory();

        history.Execute(document, new SC2SetCollision2VerticalOffsetCommand(3f));
        history.Undo(document);

        Assert.Equal(0.25f, document.Collision2.Generation.VerticalOffset);
        Assert.False(history.CanUndo);
        Assert.True(history.CanRedo);
    }

    [Fact]
    public void Redo_ReappliesTheNewValue()
    {
        var document = new SC2MapDocument();
        var history = new SC2CommandHistory();

        history.Execute(document, new SC2SetCollision2VerticalOffsetCommand(3f));
        history.Undo(document);
        history.Redo(document);

        Assert.Equal(3f, document.Collision2.Generation.VerticalOffset);
    }

    [Fact]
    public void Do_ReturnsChangeSetWithCollision2DirtyFlagAndDomain()
    {
        var document = new SC2MapDocument();
        var command = new SC2SetCollision2VerticalOffsetCommand(2f);

        var changeSet = command.Do(document);

        Assert.Equal(SC2DirtyFlags.Collision2, changeSet.DirectDirtyFlags);
        Assert.Contains(SC2DomainId.Collision2, changeSet.ChangedDomains);
        Assert.Empty(changeSet.InvalidatedDomains);
        Assert.Equal("Set Collision2.VerticalOffset", changeSet.CommandName);
    }

    [Fact]
    public void Execute_RaisesChangedWithMatchingRevisions()
    {
        var document = new SC2MapDocument();
        var history = new SC2CommandHistory();
        SC2DocumentChangeSet observed = null;
        document.Changed += cs => observed = cs;

        history.Execute(document, new SC2SetCollision2VerticalOffsetCommand(2f));

        Assert.NotNull(observed);
        Assert.Equal(0, observed.BeforeRevision);
        Assert.Equal(1, observed.AfterRevision);
        Assert.Equal(1, document.Revision);
    }
}

/// <summary>Mirrors Collision2CommandTests' own VerticalOffset coverage
/// exactly, for the other mutable field on the same settings object.</summary>
public class Collision2PolygonBudgetCommandTests
{
    [Fact]
    public void Execute_SetsMaxPolygons()
    {
        var document = new SC2MapDocument();
        var history = new SC2CommandHistory();

        history.Execute(document, new SC2SetCollision2PolygonBudgetCommand(6000));

        Assert.Equal(6000, document.Collision2.Generation.MaxPolygons);
    }

    [Fact]
    public void Undo_RestoresThePreviousValue()
    {
        var document = new SC2MapDocument();
        document.Collision2.Generation.MaxPolygons = 2500;
        var history = new SC2CommandHistory();

        history.Execute(document, new SC2SetCollision2PolygonBudgetCommand(8000));
        history.Undo(document);

        Assert.Equal(2500, document.Collision2.Generation.MaxPolygons);
        Assert.False(history.CanUndo);
        Assert.True(history.CanRedo);
    }

    [Fact]
    public void Redo_ReappliesTheNewValue()
    {
        var document = new SC2MapDocument();
        var history = new SC2CommandHistory();

        history.Execute(document, new SC2SetCollision2PolygonBudgetCommand(8000));
        history.Undo(document);
        history.Redo(document);

        Assert.Equal(8000, document.Collision2.Generation.MaxPolygons);
    }

    [Fact]
    public void Do_ReturnsChangeSetWithCollision2DirtyFlagAndDomain()
    {
        var document = new SC2MapDocument();
        var command = new SC2SetCollision2PolygonBudgetCommand(5000);

        var changeSet = command.Do(document);

        Assert.Equal(SC2DirtyFlags.Collision2, changeSet.DirectDirtyFlags);
        Assert.Contains(SC2DomainId.Collision2, changeSet.ChangedDomains);
        Assert.Empty(changeSet.InvalidatedDomains);
        Assert.Equal("Set Collision2.MaxPolygons", changeSet.CommandName);
    }
}

public class Collision2ValidatorTests
{
    [Fact]
    public void Validate_ErrorsWhenMaxPolygonsExceedsLimit()
    {
        // Limit is now 10000 (SC2Collision2Shell's user-adjustable budget
        // slider tops out there) — the format itself has no fixed ceiling
        // (Collision2Bdf.Serialize writes plain UInt32 counts), so this
        // just confirms the validator still catches a value beyond
        // whatever the slider itself allows.
        var doc = new SC2CollisionDocument { Generation = new SC2CollisionGenerationSettings { MaxPolygons = 12000 } };
        var report = SC2CollisionValidator.Validate(doc);

        Assert.Contains(report.Messages, m => m.Code == "COL001" && m.Severity == SC2ValidationSeverity.Error);
        Assert.True(report.HasErrors);
    }

    [Fact]
    public void Validate_NoErrorAtExactlyTheLimit()
    {
        var doc = new SC2CollisionDocument { Generation = new SC2CollisionGenerationSettings { MaxPolygons = 10000 } };
        var report = SC2CollisionValidator.Validate(doc);

        Assert.Empty(report.Messages);
        Assert.False(report.HasErrors);
    }

    [Fact]
    public void Validate_NoErrorBelowTheLimit()
    {
        // 3000 (above the OLD 2500 default, below the NEW 10000 ceiling) —
        // exercises the exact value that used to be an Error and now isn't,
        // since 2500 was never a real format limit (see
        // SC2CollisionValidator's own doc comment).
        var doc = new SC2CollisionDocument { Generation = new SC2CollisionGenerationSettings { MaxPolygons = 3000 } };
        var report = SC2CollisionValidator.Validate(doc);

        Assert.Empty(report.Messages);
    }

    [Fact]
    public void Validate_NullDocumentReturnsEmptyReport()
    {
        var report = SC2CollisionValidator.Validate(null);
        Assert.Empty(report.Messages);
    }
}
