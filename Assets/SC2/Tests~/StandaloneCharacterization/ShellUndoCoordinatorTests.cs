using System.Collections.Generic;
using System.Linq;
using SC2.MapEditor.Document;

namespace SC2CharacterizationTests;

public class ShellUndoCoordinatorTests
{
    private sealed class FakeChannel : ISC2UndoChannel
    {
        public int UndoCalls;
        public int RedoCalls;
        public bool UndoResult = true;
        public bool RedoResult = true;

        public bool TryUndo() { UndoCalls++; return UndoResult; }
        public bool TryRedo() { RedoCalls++; return RedoResult; }
    }

    [Fact]
    public void Record_KeepsOnlyTheLatestMaxSteps()
    {
        var coordinator = new SC2ShellUndoCoordinator();
        var channels = new List<FakeChannel>();
        for (var i = 0; i < SC2ShellUndoCoordinator.MaxSteps + 5; i++)
        {
            channels.Add(new FakeChannel());
            coordinator.Record(channels[i]);
        }

        Assert.Equal(SC2ShellUndoCoordinator.MaxSteps, coordinator.UndoCount);
        while (coordinator.Undo()) { }
        Assert.All(channels.Skip(5), channel => Assert.Equal(1, channel.UndoCalls));
        Assert.All(channels.Take(5), channel => Assert.Equal(0, channel.UndoCalls));
    }

    [Fact]
    public void Record_MakesCanUndoTrueAndCanRedoFalse()
    {
        var coordinator = new SC2ShellUndoCoordinator();
        Assert.False(coordinator.CanUndo);

        coordinator.Record(new FakeChannel());

        Assert.True(coordinator.CanUndo);
        Assert.False(coordinator.CanRedo);
    }

    [Fact]
    public void Undo_InvokesTheMostRecentlyRecordedChannel()
    {
        var coordinator = new SC2ShellUndoCoordinator();
        var first = new FakeChannel();
        var second = new FakeChannel();
        coordinator.Record(first);
        coordinator.Record(second);

        Assert.True(coordinator.Undo());

        Assert.Equal(0, first.UndoCalls);
        Assert.Equal(1, second.UndoCalls);
    }

    [Fact]
    public void Undo_MovesChannelToRedoStack_RedoInvokesSameChannel()
    {
        var coordinator = new SC2ShellUndoCoordinator();
        var channel = new FakeChannel();
        coordinator.Record(channel);

        Assert.True(coordinator.Undo());
        Assert.True(coordinator.CanRedo);
        Assert.False(coordinator.CanUndo);

        Assert.True(coordinator.Redo());
        Assert.Equal(1, channel.UndoCalls);
        Assert.Equal(1, channel.RedoCalls);
        Assert.True(coordinator.CanUndo);
        Assert.False(coordinator.CanRedo);
    }

    [Fact]
    public void Record_ClearsRedoStack()
    {
        var coordinator = new SC2ShellUndoCoordinator();
        coordinator.Record(new FakeChannel());
        coordinator.Undo();
        Assert.True(coordinator.CanRedo);

        coordinator.Record(new FakeChannel());

        Assert.False(coordinator.CanRedo);
    }

    [Fact]
    public void Undo_WhenChannelReturnsFalse_LeavesStateUnchanged()
    {
        var coordinator = new SC2ShellUndoCoordinator();
        var channel = new FakeChannel { UndoResult = false };
        coordinator.Record(channel);

        Assert.False(coordinator.Undo());

        Assert.True(coordinator.CanUndo);
        Assert.False(coordinator.CanRedo);
        Assert.Equal(1, channel.UndoCalls);
    }

    [Fact]
    public void Undo_WithNothingRecorded_ReturnsFalse()
    {
        var coordinator = new SC2ShellUndoCoordinator();
        Assert.False(coordinator.Undo());
    }

    [Fact]
    public void Redo_WithNothingUndone_ReturnsFalse()
    {
        var coordinator = new SC2ShellUndoCoordinator();
        coordinator.Record(new FakeChannel());
        Assert.False(coordinator.Redo());
    }

    [Fact]
    public void MixedChannelTypes_DispatchInTrueChronologicalOrder()
    {
        // Simulates a shell with both a legacy-snapshot channel and a
        // command-history channel interleaved — exactly the scenario
        // SC2TerrainCombinedUndo/SC2ObjectsShell/SC2CostsShell all need:
        // pressing Undo repeatedly must walk backwards through BOTH in the
        // order steps were actually recorded, not grouped by type.
        var coordinator = new SC2ShellUndoCoordinator();
        var legacyA = new FakeChannel();
        var commandB = new FakeChannel();
        var legacyC = new FakeChannel();

        coordinator.Record(legacyA);
        coordinator.Record(commandB);
        coordinator.Record(legacyC);

        coordinator.Undo();
        Assert.Equal(1, legacyC.UndoCalls);
        Assert.Equal(0, commandB.UndoCalls);
        Assert.Equal(0, legacyA.UndoCalls);

        coordinator.Undo();
        Assert.Equal(1, commandB.UndoCalls);
        Assert.Equal(0, legacyA.UndoCalls);

        coordinator.Undo();
        Assert.Equal(1, legacyA.UndoCalls);

        Assert.False(coordinator.CanUndo);
    }

    [Fact]
    public void UndoCountAndRedoCount_ReflectMarkerStackDepth()
    {
        var coordinator = new SC2ShellUndoCoordinator();
        coordinator.Record(new FakeChannel());
        coordinator.Record(new FakeChannel());
        Assert.Equal(2, coordinator.UndoCount);
        Assert.Equal(0, coordinator.RedoCount);

        coordinator.Undo();
        Assert.Equal(1, coordinator.UndoCount);
        Assert.Equal(1, coordinator.RedoCount);
    }

    [Fact]
    public void LastActionTimestamp_AdvancesOnEachRecord()
    {
        var coordinator = new SC2ShellUndoCoordinator();
        Assert.Equal(0, coordinator.LastActionTimestamp);

        coordinator.Record(new FakeChannel());
        var first = coordinator.LastActionTimestamp;
        Assert.True(first > 0);

        coordinator.Record(new FakeChannel());
        Assert.True(coordinator.LastActionTimestamp >= first);
    }

    [Fact]
    public void LastActionTimestamp_ReflectsNewTopOfStack_AfterUndo_NotTheJustUndoneStep()
    {
        // Regression test: LastActionTimestamp must not be a single scalar
        // bumped only by Record() and never touched by Undo() - otherwise a shell that
        // had just used up its "most recent" claim would keep reporting the
        // already-undone step's stale timestamp instead of the new top of
        // its own stack. That staleness could make this shell keep
        // incorrectly winning the global Ctrl+Z dispatch race against a
        // genuinely more recent action from another domain, skipping it.
        var coordinator = new SC2ShellUndoCoordinator();
        coordinator.Record(new FakeChannel()); // step A
        var afterA = coordinator.LastActionTimestamp;
        coordinator.Record(new FakeChannel()); // step B, newer than A
        var afterB = coordinator.LastActionTimestamp;
        Assert.True(afterB >= afterA);

        coordinator.Undo(); // reverts B

        // Must now report A's own timestamp (the new top of the stack), not
        // B's already-undone one.
        Assert.Equal(afterA, coordinator.LastActionTimestamp);
    }

    [Fact]
    public void LastRedoTimestamp_ReflectsWhenEachStepWasUndone_NotWhenItWasOriginallyRecorded()
    {
        // A step recorded LONG ago but undone MOST RECENTLY must report a
        // fresher LastRedoTimestamp than a step recorded more recently but
        // undone earlier - Redo has to replay in exact reverse of the undo
        // sequence (most-recently-undone first), which is the opposite
        // order from "most recently recorded first."
        var coordinator = new SC2ShellUndoCoordinator();
        var old = new FakeChannel();
        var recent = new FakeChannel();
        coordinator.Record(old);    // recorded first (older)
        coordinator.Record(recent); // recorded second (newer)

        coordinator.Undo(); // undoes `recent` first (LIFO on the undo stack)
        var afterFirstUndo = coordinator.LastRedoTimestamp;
        coordinator.Undo(); // undoes `old` second

        // `old` was undone AFTER `recent`, so it must now be on top of the
        // redo stack with a FRESHER timestamp than when `recent` alone had
        // just been undone - even though `old` was RECORDED earlier.
        Assert.True(coordinator.LastRedoTimestamp >= afterFirstUndo);

        Assert.True(coordinator.Redo());
        Assert.Equal(1, old.RedoCalls);
        Assert.Equal(0, recent.RedoCalls);
    }

    [Fact]
    public void Clear_RemovesAllUndoAndRedoState()
    {
        var coordinator = new SC2ShellUndoCoordinator();
        coordinator.Record(new FakeChannel());
        coordinator.Undo();
        Assert.True(coordinator.CanRedo);

        coordinator.Clear();

        Assert.False(coordinator.CanUndo);
        Assert.False(coordinator.CanRedo);
    }

    [Fact]
    public void DelegateUndoChannel_ForwardsToProvidedFunctions()
    {
        var undoCalled = false;
        var redoCalled = false;
        var channel = new SC2DelegateUndoChannel(
            tryUndo: () => { undoCalled = true; return true; },
            tryRedo: () => { redoCalled = true; return true; });

        Assert.True(channel.TryUndo());
        Assert.True(channel.TryRedo());
        Assert.True(undoCalled);
        Assert.True(redoCalled);
    }
}
