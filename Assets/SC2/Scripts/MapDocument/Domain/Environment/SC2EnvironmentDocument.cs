using System.Collections.Generic;

namespace SC2.MapEditor.Document
{
    /// <summary>
    /// A placeholder for a
    /// specific unverified/low-confidence info.bdf field, matched by byte
    /// offset (mirrors the legacy SC2EnvironmentShell's "Unverified" tab,
    /// which currently exposes ~25 Unknown* float/int fields directly on
    /// InfoModel). Not populated by SC2EnvironmentLegacyBridge today —
    /// WorkingHeader already carries every one of those bytes; this exists
    /// as the seam for a future pass that wants to annotate specific fields
    /// (confidence notes, suspected purpose) rather than treat the header as
    /// one opaque blob.
    /// </summary>
    public sealed class SC2UnknownInfoField
    {
        public int ByteOffset;
        public string Note = "";
    }

    /// <summary>
    /// OriginalHeader vs
    /// WorkingHeader follows the general "keep the as-imported form
    /// for round-trip, edit a separate working copy" pattern (same idea as
    /// SC2ArchiveEntry.OriginalContent). For SC2EnvironmentLegacyBridge's
    /// read-only snapshot specifically, both start out equal: the legacy
    /// SC2TerrainData has no separately-stored "as originally imported"
    /// Environment snapshot the way Objects does (ObjSaveLuaTextOriginal) —
    /// EnvironmentHeader is just the live, current value. A real
    /// OriginalHeader/WorkingHeader split only starts to matter once Import
    /// (SC2MapImportService) actually populates this document from
    /// a real info.bdf blob, which it doesn't do yet.
    /// </summary>
    public sealed class SC2EnvironmentDocument
    {
        public const int HeaderSize = 508;

        public byte[] OriginalHeader = new byte[HeaderSize];
        public byte[] WorkingHeader = new byte[HeaderSize];
        public string EnvironmentMap = "";
        public string SkyMap = "";
        public string TexturePath3 = "";
        public string ColorCorrectionCube = "";
        public string FogOfWarCorrectionCube = "";
        public List<SC2UnknownInfoField> UnknownFields = new();

        /// <summary>Live water-plane geometry settings (SC2WaterPlane) — plain
        /// SC2TerrainData scalars with no info.bdf byte-offset home, the same
        /// "field with nowhere to live in the document" gap Terrain's
        /// ImportOnlyMode/BSP options had (confirmed real via a reload
        /// resetting those) — fixed here for the same reason.</summary>
        public int WaterPlaneSize = 4096;
        public float WaterUvScale = 1024f;
        public string[] WaterTexturePaths = System.Array.Empty<string>();

        /// <summary>Auto-generated waterDepth (Mask1) texture settings — see
        /// SC2WaterDepthTextureGenerator. Same "field with nowhere to live"
        /// gap as WaterPlaneSize/WaterUvScale/WaterTexturePaths above, fixed
        /// here for the same reason (a reload would otherwise silently reset
        /// these to their SC2TerrainData defaults).</summary>
        public bool WaterDepthAutoGenerate = false;
        public int WaterDepthResolution = 512;
        public float WaterDepthMargin = 64f;
        public float WaterDepthFalloffPower = 2f;
        public float WaterDepthMinAlpha = 0.04f;
        public bool WaterDepthPaintBorder = true;
        public bool WaterDepthImageBorderOutline = false;
    }
}
