using System.Collections.Generic;
using UnityEngine;

namespace SC2.MapEditor.Document
{
    /// <summary>No
    /// real hfield projection algorithm is wired to the procedural
    /// SC2TerrainData workflow yet (heights are exported directly, not
    /// resampled through a documented top-hit/gap-fill policy), so these are
    /// minimal placeholders for a future projection service.</summary>
    public enum SC2TopHitPolicy
    {
        HighestSurface
    }

    public enum SC2GapFillPolicy
    {
        None
    }

    public sealed class SC2HfieldProjectionSettings
    {
        public int Width;
        public int Height;
        public Rect WorldBounds;
        public SC2TopHitPolicy TopHitPolicy;
        public SC2GapFillPolicy GapFillPolicy;
        public List<string> IncludedLayerIds = new();
        public List<string> ExcludedLayerIds = new();
        public List<string> ExcludedMaterialPairIds = new();
    }

    /// <summary>
    /// Built today only by
    /// SC2HfieldLegacyBridge, and only PARTIALLY: TerrainTypesUInt8 is the
    /// one field SC2TerrainData actually persists (as legacy ushort[],
    /// converted to byte[] here since real terrain types are 8-bit
    /// single-channel data). The
    /// resampled heightmap this domain exists for (HeightmapUInt16) is never
    /// computed or stored on SC2TerrainData at all — export projects
    /// directly from Heights without an intermediate persisted hfield grid
    /// — so it's left unpopulated, same honesty as SC2CollisionDocument.Mesh.
    /// </summary>
    public sealed class SC2HfieldDocument
    {
        public int MapX;
        public int MapY;
        public Rect PlayableRect;
        public SC2BlobReference HeightmapUInt16;
        public SC2BlobReference TerrainTypesUInt8;
        public SC2HfieldProjectionSettings Projection = new();
        public long SourceTerrainRevision;
        public SC2DerivedState State;
    }
}
