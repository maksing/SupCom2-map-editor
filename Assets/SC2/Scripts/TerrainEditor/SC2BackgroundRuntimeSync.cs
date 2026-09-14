/// <summary>
/// Tiny static "please resync the background runtime soon" flag, for the
/// few call sites (SC2TerrainUndoStack, a fully generic/reusable class with
/// no reference back to SC2TerrainShell) that can't reach
/// SC2TerrainShell.RefreshBackgroundRuntime()/EnsureBackgroundRuntime()
/// directly. Every other caller that DOES have a shell reference
/// (SC2TerrainShell itself, SC2MaterialsPairsMode) calls those directly
/// instead of going through this flag — this exists purely to bridge the
/// one reference-less case, mirroring the old
/// SC2BackgroundTerrainPreview.MarkDirty()'s own role for the same call
/// site.
/// </summary>
public static class SC2BackgroundRuntimeSync
{
    private static bool _pending;
    public static void MarkDirty() => _pending = true;

    /// <summary>Returns true (and clears the flag) at most once per actual
    /// MarkDirty() call — callers should follow up with a real resync
    /// (SC2TerrainShell.RefreshBackgroundRuntime) when this returns true.</summary>
    public static bool ConsumePending()
    {
        if (!_pending) return false;
        _pending = false;
        return true;
    }
}
