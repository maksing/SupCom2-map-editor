namespace SC2.MapEditor.Document
{
    /// <summary>
    /// A mesh stored as
    /// two blobs (vertices, triangle indices) plus cheap counts for display
    /// without deserializing, same shape as SC2TerrainGrid's
    /// HeightsFloat32/SC2TrianglePairMap's blob-plus-metadata pattern.
    /// Referenced by SC2CollisionDocument.Mesh.
    /// </summary>
    public sealed class SC2MeshBlobReference
    {
        public SC2BlobReference VerticesFloat32; // 3 floats per vertex, x,y,z
        public SC2BlobReference TrianglesUInt32; // 3 indices per triangle
        public int VertexCount;
        public int TriangleCount;
    }
}
