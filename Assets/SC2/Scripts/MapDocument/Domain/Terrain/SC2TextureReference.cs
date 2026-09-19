namespace SC2.MapEditor.Document
{
    /// <summary>
    /// Stable
    /// texture identity independent of a live Texture2D reference.
    ///
    /// Deliberately minimal/provisional: only `Name` (Texture2D.name, the one
    /// identity that reads identically in the Editor and a build with no
    /// AssetDatabase dependency) is populated by SC2TerrainLegacyBridge today.
    /// A fuller resolution pipeline — resolving through the vanilla texture
    /// catalog, content-addressing custom textures via the blob store — is a
    /// separate, later undertaking once texture assignment itself moves onto
    /// commands; this type exists
    /// now so SC2MaterialDocument has somewhere real to point instead of a
    /// live Texture2D, without pretending the full resolution pipeline exists.
    /// </summary>
    public sealed class SC2TextureReference
    {
        public string Id = "";
        public string Name = "";
        public string ResourcePath = "";
        public string GamePath = "";
        public string Source = "";
        public string ContentHash = "";
        // Copy this texture into the exported map's own Textures folder even
        // if it comes from a vanilla game archive (default false: reference
        // it by its real in-game path instead) — see
        // SC2VanillaArchiveClassifier.
        public bool ForceExport;

        public bool IsAssigned => !string.IsNullOrEmpty(Id) || !string.IsNullOrEmpty(ResourcePath)
            || !string.IsNullOrEmpty(GamePath) || !string.IsNullOrEmpty(Name);

        public SC2TextureReference Clone() => new SC2TextureReference
        {
            Id = Id, Name = Name, ResourcePath = ResourcePath, GamePath = GamePath,
            Source = Source, ContentHash = ContentHash, ForceExport = ForceExport,
        };

        public void CopyFrom(SC2TextureReference other)
        {
            other ??= new SC2TextureReference();
            Id = other.Id ?? "";
            Name = other.Name ?? "";
            ResourcePath = other.ResourcePath ?? "";
            GamePath = other.GamePath ?? "";
            Source = other.Source ?? "";
            ContentHash = other.ContentHash ?? "";
            ForceExport = other.ForceExport;
        }
    }
}
