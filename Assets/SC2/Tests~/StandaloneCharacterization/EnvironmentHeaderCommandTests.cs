using System;
using SC2.MapEditor.Document;
using SC2.MapEditor.SmartExport;

namespace SC2CharacterizationTests;

public class EnvironmentHeaderCommandTests
{
    private static byte[] FillArray(byte fill)
    {
        var arr = new byte[SC2SetEnvironmentHeaderCommand.HeaderSize];
        for (var i = 0; i < arr.Length; i++) arr[i] = fill;
        return arr;
    }

    [Fact]
    public void Execute_ReplacesTheWholeHeader()
    {
        var document = new SC2MapDocument();
        document.Environment.WorkingHeader = FillArray(1);
        var history = new SC2CommandHistory();

        history.Execute(document, new SC2SetEnvironmentHeaderCommand(FillArray(2)));

        Assert.Equal(FillArray(2), document.Environment.WorkingHeader);
    }

    [Fact]
    public void Undo_RestoresThePreviousHeader()
    {
        var document = new SC2MapDocument();
        document.Environment.WorkingHeader = FillArray(1);
        var history = new SC2CommandHistory();

        history.Execute(document, new SC2SetEnvironmentHeaderCommand(FillArray(2)));
        history.Undo(document);

        Assert.Equal(FillArray(1), document.Environment.WorkingHeader);
        Assert.False(history.CanUndo);
        Assert.True(history.CanRedo);
    }

    [Fact]
    public void Redo_ReappliesTheNewHeader()
    {
        var document = new SC2MapDocument();
        var history = new SC2CommandHistory();

        history.Execute(document, new SC2SetEnvironmentHeaderCommand(FillArray(2)));
        history.Undo(document);
        history.Redo(document);

        Assert.Equal(FillArray(2), document.Environment.WorkingHeader);
    }

    [Fact]
    public void Constructor_ClonesTheInputArray()
    {
        var input = FillArray(5);
        var command = new SC2SetEnvironmentHeaderCommand(input);
        input[0] = 99; // mutate the caller's copy after construction

        var document = new SC2MapDocument();
        command.Do(document);

        Assert.Equal(5, document.Environment.WorkingHeader[0]); // unaffected by the later mutation
    }

    [Fact]
    public void Do_ReturnsChangeSetWithInfoDirtyFlagAndEnvironmentDomain()
    {
        var document = new SC2MapDocument();
        var command = new SC2SetEnvironmentHeaderCommand(FillArray(2));

        var changeSet = command.Do(document);

        Assert.Equal(SC2DirtyFlags.Info, changeSet.DirectDirtyFlags);
        Assert.Contains(SC2DomainId.Environment, changeSet.ChangedDomains);
        Assert.Empty(changeSet.InvalidatedDomains);
        Assert.Equal("Set Environment.Header", changeSet.CommandName);
    }

    [Fact]
    public void Constructor_ThrowsOnNullOrWrongSizedHeader()
    {
        Assert.Throws<ArgumentNullException>(() => new SC2SetEnvironmentHeaderCommand(null));
        Assert.Throws<ArgumentException>(() => new SC2SetEnvironmentHeaderCommand(new byte[10]));
    }
}
