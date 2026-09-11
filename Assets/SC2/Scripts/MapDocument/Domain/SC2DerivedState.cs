namespace SC2.MapEditor.Document
{
    /// <summary>Shared across
    /// every derived domain document (Hfield/Costs/Collision2/...). Kept
    /// deliberately distinct from "dirty": Dirty means the USER changed this domain directly; Stale
    /// means an upstream SOURCE this domain depends on changed and this one
    /// hasn't been regenerated against it yet — conflating the two would
    /// hide exactly the kind of "which decision does the user need to make"
    /// signal this state exists to carry.</summary>
    public enum SC2DerivedState
    {
        Original,
        Current,
        Dirty,
        Stale,
        Generating,
        Invalid,
        Missing
    }
}
