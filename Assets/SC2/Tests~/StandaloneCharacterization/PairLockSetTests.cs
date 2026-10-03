using Xunit;

namespace SC2CharacterizationTests;

public class PairLockSetTests
{
    [Fact]
    public void NothingIsLockedAtFirst()
    {
        var locks = new SC2PairLockSet();
        Assert.False(locks.Any);
        Assert.False(locks.IsLocked(0));
        Assert.False(locks.IsLocked(-1));
        Assert.All(locks.ToShaderArray(), v => Assert.Equal(0f, v));
    }

    [Fact]
    public void LockedPairsAreReportedAndGoIntoTheShaderTable()
    {
        var locks = new SC2PairLockSet();
        locks.Set(2, true);
        locks.Set(3, true);

        Assert.True(locks.Any);
        Assert.True(locks.IsLocked(2));
        Assert.True(locks.IsLocked(3));
        Assert.False(locks.IsLocked(1));
        var table = locks.ToShaderArray();
        Assert.Equal(SC2PairLockSet.Size, table.Length);
        Assert.Equal(1f, table[2]);
        Assert.Equal(1f, table[3]);
        Assert.Equal(0f, table[1]);
        Assert.Equal(0f, table[255]);   // the cells with no pair are not locked
    }

    [Fact]
    public void NoPairHasItsOwnEntryAtTheEndOfTheTable()
    {
        var locks = new SC2PairLockSet();
        locks.Set(-1, true);

        Assert.True(locks.IsLocked(-1));
        Assert.False(locks.IsLocked(0));
        Assert.Equal(1f, locks.ToShaderArray()[255]);
    }

    [Fact]
    public void LockAllThenUnlockingSomePairsLeavesOnlyThemPaintable()
    {
        var locks = new SC2PairLockSet();
        locks.LockAll(4);
        locks.Set(0, false);
        locks.Set(1, false);

        Assert.False(locks.IsLocked(0));
        Assert.False(locks.IsLocked(1));
        Assert.True(locks.IsLocked(2));
        Assert.True(locks.IsLocked(3));
        Assert.True(locks.IsLocked(-1));
        Assert.False(locks.IsLocked(4));   // a pair beyond the count was never locked
    }

    [Fact]
    public void InvertSwapsPairsAndTheUnassignedCellsAndUnlockClearsEverything()
    {
        var locks = new SC2PairLockSet();
        locks.Set(1, true);
        locks.Invert(3);
        Assert.True(locks.IsLocked(0));
        Assert.False(locks.IsLocked(1));
        Assert.True(locks.IsLocked(2));
        Assert.True(locks.IsLocked(-1));

        locks.UnlockAll();
        Assert.False(locks.Any);
    }

    [Fact]
    public void TheShaderTableBufferIsReused()
    {
        var locks = new SC2PairLockSet();
        var buffer = new float[SC2PairLockSet.Size];
        locks.Set(5, true);

        Assert.Same(buffer, locks.ToShaderArray(buffer));
        Assert.Equal(1f, buffer[5]);
    }
}
