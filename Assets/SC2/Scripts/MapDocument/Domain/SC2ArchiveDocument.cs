using System;
using System.Collections.Generic;

namespace SC2.MapEditor.Document
{
    public enum SC2ArchiveEntryRole
    {
        Unknown,
        ScenarioLua,
        SaveLua,
        ScriptLua,
        TerrainBdf,
        HfieldBdf,
        CostsBdf,
        Collision2Bdf,
        MapObjsBdf,
        InfoBdf,
        DecalsBdf,
        MapTexture,
        CustomAsset,
        ForeignMapEntry
    }

    /// <summary>
    /// One entry as it
    /// existed in the original source container. OriginalContent references
    /// an immutable blob rather than embedding bytes directly, so unknown
    /// files (a full passthrough for everything, including files unknown to
    /// the editor) can be preserved without bloating the manifest.
    /// </summary>
    [Serializable]
    public sealed class SC2ArchiveEntry
    {
        public string OriginalPath;
        public string NormalizedPath;
        public long UncompressedLength;
        public string Sha256;
        public SC2ArchiveEntryRole Role;
        public SC2BlobReference OriginalContent;
    }

    [Serializable]
    public sealed class SC2ArchiveDocument
    {
        public List<SC2ArchiveEntry> Entries = new();
        public string SourceContainerFingerprint;
        public SC2SourceContainerKind SourceContainerKind;
        public string SelectedScenarioEntryPath;

        public SC2ArchiveEntry Find(string normalizedPath)
        {
            foreach (var e in Entries)
                if (string.Equals(e.NormalizedPath, normalizedPath, StringComparison.OrdinalIgnoreCase))
                    return e;
            return null;
        }
    }
}
