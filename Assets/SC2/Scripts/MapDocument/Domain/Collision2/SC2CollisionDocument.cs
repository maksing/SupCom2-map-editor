namespace SC2.MapEditor.Document
{
    public enum SC2CollisionSourceMode
    {
        OriginalPassthrough,
        GeneratedFromHfield,
        ImportedManualMesh
    }

    /// <summary>Holds the &lt;= 2500
    /// polygons limit setting and vertical offset.</summary>
    public sealed class SC2CollisionGenerationSettings
    {
        public int MaxPolygons = 2500;
        public float VerticalOffset;
    }

    /// <summary>
    /// Built today only by
    /// SC2CollisionLegacyBridge as a read-only snapshot — nothing
    /// writes live edits through this yet.
    /// </summary>
    public sealed class SC2CollisionDocument
    {
        public SC2CollisionSourceMode SourceMode;
        public SC2MeshBlobReference Mesh;
        public SC2CollisionGenerationSettings Generation = new();
        public long SourceHfieldRevision;
        public SC2DerivedState State;

        /// <summary>The Collision2 tab's preview mesh (last Generate or import) as container bytes, so it survives a
        /// restart. Empty = none.</summary>
        public SC2BlobReference PreviewMeshBytes;

        /// <summary>The imported/corrected mesh written to collision2.win.bdf when <see cref="UseCustomMesh"/> is set.</summary>
        public SC2BlobReference CustomMeshBytes;

        public bool UseCustomMesh;
        public bool HidePreview;
        public bool Wireframe;
    }
}
