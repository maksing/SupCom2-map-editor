using SC2.MapEditor.Document;

namespace SC2CharacterizationTests;

public class CommandHistoryTests
{
    // A minimal test double exercising the Do/Undo contract
    // SC2CommandHistory is built against, independent of any real
    // ISC2DocumentCommand implementation.
    private sealed class RenameCommand : ISC2DocumentCommand
    {
        private readonly string _newName;
        private string _previousName;

        public RenameCommand(string newName) => _newName = newName;

        public string Name => "Rename";

        public SC2DocumentChangeSet Do(SC2MapDocument document)
        {
            _previousName = document.Identity.DisplayName;
            document.Identity.DisplayName = _newName;
            return new SC2DocumentChangeSet { CommandName = Name };
        }

        public SC2DocumentChangeSet Undo(SC2MapDocument document)
        {
            document.Identity.DisplayName = _previousName;
            return new SC2DocumentChangeSet { CommandName = Name + " (undo)" };
        }
    }

    [Fact]
    public void Execute_AppliesCommandAndBumpsRevision()
    {
        var document = new SC2MapDocument();
        var history = new SC2CommandHistory();

        history.Execute(document, new RenameCommand("New Name"));

        Assert.Equal("New Name", document.Identity.DisplayName);
        Assert.Equal(1, document.Revision);
        Assert.True(history.CanUndo);
        Assert.False(history.CanRedo);
    }

    [Fact]
    public void Undo_RevertsCommandAndEnablesRedo()
    {
        var document = new SC2MapDocument();
        document.Identity.DisplayName = "Original";
        var history = new SC2CommandHistory();

        history.Execute(document, new RenameCommand("New Name"));
        history.Undo(document);

        Assert.Equal("Original", document.Identity.DisplayName);
        Assert.False(history.CanUndo);
        Assert.True(history.CanRedo);
    }

    [Fact]
    public void Redo_ReappliesCommand()
    {
        var document = new SC2MapDocument();
        var history = new SC2CommandHistory();

        history.Execute(document, new RenameCommand("New Name"));
        history.Undo(document);
        history.Redo(document);

        Assert.Equal("New Name", document.Identity.DisplayName);
        Assert.True(history.CanUndo);
        Assert.False(history.CanRedo);
    }

    [Fact]
    public void Execute_ClearsRedoStack()
    {
        var document = new SC2MapDocument();
        var history = new SC2CommandHistory();

        history.Execute(document, new RenameCommand("First"));
        history.Undo(document);
        Assert.True(history.CanRedo);

        history.Execute(document, new RenameCommand("Second"));
        Assert.False(history.CanRedo);
        Assert.Equal("Second", document.Identity.DisplayName);
    }

    [Fact]
    public void RaiseChanged_FiresChangedEventWithSameChangeSetInstance()
    {
        var document = new SC2MapDocument();
        var history = new SC2CommandHistory();
        SC2DocumentChangeSet observed = null;
        document.Changed += cs => observed = cs;

        history.Execute(document, new RenameCommand("New Name"));

        Assert.NotNull(observed);
        Assert.Equal("Rename", observed.CommandName);
    }

    [Fact]
    public void Undo_WithEmptyHistory_IsNoOp()
    {
        var document = new SC2MapDocument();
        var history = new SC2CommandHistory();

        history.Undo(document);

        Assert.Equal(0, document.Revision);
        Assert.False(history.CanUndo);
    }
}
