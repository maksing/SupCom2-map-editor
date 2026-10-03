using System;

/// <summary>
/// The pairs a brush must not touch. A locked pair keeps its cells: the pair brush does not overwrite them and the blend mask brush
/// does not paint on them. Pair index -1 (a cell with no pair) has its own entry. Pure, so the rules can be tested.
/// </summary>
public sealed class SC2PairLockSet
{
    /// <summary>Size of the lock table the brush shader reads: pair indices 0..254 and, at 255, "no pair".</summary>
    public const int Size = 256;
    private const int UnassignedIndex = Size - 1;

    private readonly bool[] _locked = new bool[Size];

    /// <summary>Whether any pair (or the unassigned cells) is locked.</summary>
    public bool Any
    {
        get
        {
            for (var i = 0; i < _locked.Length; i++) if (_locked[i]) return true;
            return false;
        }
    }

    private static int IndexOf(int pair) => pair < 0 || pair >= UnassignedIndex ? UnassignedIndex : pair;

    /// <summary>True for a locked pair; -1 asks about the cells that have no pair.</summary>
    public bool IsLocked(int pair) => _locked[IndexOf(pair)];

    public void Set(int pair, bool locked) => _locked[IndexOf(pair)] = locked;

    public void UnlockAll() => Array.Clear(_locked, 0, _locked.Length);

    /// <summary>Locks pairs 0..<paramref name="pairCount"/>-1 and the unassigned cells.</summary>
    public void LockAll(int pairCount)
    {
        for (var i = 0; i < Math.Min(pairCount, UnassignedIndex); i++) _locked[i] = true;
        _locked[UnassignedIndex] = true;
    }

    /// <summary>Swaps locked and unlocked for pairs 0..<paramref name="pairCount"/>-1 and the unassigned cells.</summary>
    public void Invert(int pairCount)
    {
        for (var i = 0; i < Math.Min(pairCount, UnassignedIndex); i++) _locked[i] = !_locked[i];
        _locked[UnassignedIndex] = !_locked[UnassignedIndex];
    }

    /// <summary>The table for the brush shader: 1 for a locked entry, 0 otherwise. <paramref name="buffer"/> is reused when it has
    /// the right size.</summary>
    public float[] ToShaderArray(float[] buffer = null)
    {
        if (buffer == null || buffer.Length != Size) buffer = new float[Size];
        for (var i = 0; i < Size; i++) buffer[i] = _locked[i] ? 1f : 0f;
        return buffer;
    }
}
