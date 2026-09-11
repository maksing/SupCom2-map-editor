using System;

namespace SC2.MapEditor.Document
{
    public enum SC2MapSourceKind
    {
        NewProcedural,
        ScdArchiveFile,
        ScdDirectory,
        TerrainBdf,
        BlenderTerrainBdf,
        RecoveredAutosave
    }

    /// <summary>
    /// OriginalFingerprint is
    /// SHA-256 of the archive file's bytes for a ScdArchiveFile source; for
    /// a ScdDirectory source it's a stable hash over the sorted list of
    /// (normalized path, length, content SHA-256) — computed once during
    /// import and never recomputed implicitly, so a later re-import can
    /// detect that the source directory or archive changed between Probe and Import.
    /// </summary>
    [Serializable]
    public sealed class SC2MapSource
    {
        public SC2MapSourceKind Kind;
        public SC2SourceContainerKind ContainerKind;
        public string OriginalPath;
        public string OriginalFileName;
        public string OriginalFingerprint;
        public DateTime ImportedUtc;
        public bool OriginalStillAvailable;
    }
}
