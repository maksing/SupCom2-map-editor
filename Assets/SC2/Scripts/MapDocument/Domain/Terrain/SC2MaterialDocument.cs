using System.Collections.Generic;
using UnityEngine;

namespace SC2.MapEditor.Document
{
    /// <summary>Id is the
    /// stable identity (mirrors SC2Material.Id, Scripts/TerrainEditor/
    /// SC2TerrainData.cs); this document's own list position carries no
    /// meaning and must never be used to resolve a reference.</summary>
    public sealed class SC2MaterialDocument
    {
        public string Id = "";
        public string Name = "";
        public SC2TextureReference Diffuse = new();
        public SC2TextureReference Normal = new();
        public SC2TextureReference Mask = new();
        public Color DebugColor = Color.white;
    }

    /// <summary>Same Id-stability reasoning as SC2MaterialDocument. RnmSetId
    /// points into SC2TerrainDocument.Rnm.MaskSets (a pair's RNM mask bytes,
    /// deduplicated by SC2TerrainLegacyBridge across every pair sharing the
    /// same RnmMaskName+bytes — pairs can share a common RnmMaskName, so a
    /// naive per-pair blob would
    /// waste space on every map with more than one pair sharing a mask).</summary>
    public sealed class SC2MaterialPairDocument
    {
        public string Id = "";
        public string Name = "";
        public string Material1Id = "";
        public string Material2Id = "";
        public Color DebugColor = Color.white;
        public float TilingScale = 4f;
        public float SimplifyRatio;
        // Reuses the existing, already-UnityEngine-free SC2ShaderFlag
        // (Scripts/TerrainEditor/SC2ShaderFlag.cs) rather than a parallel
        // string-based flag type — same Key/Value shape the legacy pair
        // already stores, no lossy round-trip through flag names alone.
        public List<SC2ShaderFlag> ShaderFlags = new();
        public SC2HfieldContribution HfieldContribution = SC2HfieldContribution.InheritLayer;
        public string RnmSetId = "";
    }
}
