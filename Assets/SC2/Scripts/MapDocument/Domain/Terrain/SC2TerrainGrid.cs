namespace SC2.MapEditor.Document
{
    /// <summary>Heights stored
    /// as a blob (not inline float[]) so the manifest JSON stays small
    /// regardless of grid size; same reasoning as SC2ArchiveEntry.
    /// OriginalContent. Height range is fixed to SC2TerrainData's existing
    /// 0..512 with a flat default of 100 (MinimumHeight/MaximumHeight here
    /// describe the CURRENT data's actual min/max, not the hard clamp range
    /// — useful for UI display without re-scanning the heightmap).</summary>
    public sealed class SC2TerrainGrid
    {
        public int Width;
        public int Height;
        public float CellSize;
        public SC2BlobReference HeightsFloat32;
        public float MinimumHeight;
        public float MaximumHeight;
    }
}
