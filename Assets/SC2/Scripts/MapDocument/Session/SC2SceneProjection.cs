namespace SC2.MapEditor.Document
{
    /// <summary>
    /// Owns whichever preview
    /// scene/objects currently render a SC2MapDocument's state in the Unity
    /// viewport. Intentionally empty at this infrastructure-skeleton stage:
    /// today rendering is still driven directly by SC2TerrainData/
    /// SC2ObjectsShell, so there is no real projection to own yet. This type
    /// exists only as the named seam SC2MapSession points at, so later
    /// stages (once a domain's viewport rendering actually reads from a
    /// SC2MapDocument instead of legacy fields) have one obvious place to
    /// put that ownership rather than inventing it ad hoc.
    /// </summary>
    public sealed class SC2SceneProjection
    {
        public bool IsActive { get; private set; }

        public void MarkActive() => IsActive = true;
        public void MarkInactive() => IsActive = false;
    }
}
