namespace SC2.MapEditor.Document
{
    /// <summary>
    /// The pair-blend mask
    /// texture (legacy: a live RenderTexture, "BlendRT"/"PairMapRT", spread
    /// across SC2TerrainShell/SC2TerrainRuntime/SC2PaintMode/
    /// SC2TerrainExporter — see also SC2GameViewEditorUI.LateUpdate's
    /// RenderTexture.active reset, which exists specifically because of this
    /// GPU-side state). Reading a live RenderTexture back into a static blob
    /// and understanding its exact channel layout is real, separate work —
    /// not attempted yet. This type exists only so
    /// SC2TerrainDocument has a real field shape;
    /// SC2TerrainLegacyBridge leaves SC2TerrainDocument.Blend null rather
    /// than populate it with something unverified.
    /// </summary>
    public sealed class SC2BlendDocument
    {
        public int Width;
        public int Height;
        public SC2BlobReference BlendTextureRgba32;
    }
}
