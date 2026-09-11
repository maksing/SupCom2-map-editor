using System;

namespace SC2.MapEditor.Document
{
    /// <summary>MapId is a
    /// stable internal identifier independent of file name; renaming the map
    /// is a separate explicit transaction (not yet implemented) that updates
    /// scenario paths and file
    /// states together, rather than a direct field write.</summary>
    [Serializable]
    public sealed class SC2MapIdentity
    {
        public string MapId = Guid.NewGuid().ToString("N");
        public string DisplayName = "";
        public string MapStem = "";
        public string Author = "";
        public string Description = "";
        public DateTime CreatedUtc = DateTime.UtcNow;
        public DateTime ModifiedUtc = DateTime.UtcNow;
    }
}
