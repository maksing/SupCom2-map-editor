using System.Collections.Generic;

namespace SC2.MapEditor.Document
{
    public enum SC2TerrainLayerRole
    {
        GameplaySurface,
        BridgeTopHit,
        DecorativeTerrain,
        BackgroundOnly,
        Water,
        Cloud,
        ExcludeFromHfield
    }

    /// <summary>Whether a specific material pair overrides its layer's
    /// default hfield inclusion — lets one pair (e.g. a single
    /// water patch within an otherwise-gameplay layer) opt out without
    /// resorting to name-based heuristics.</summary>
    public enum SC2HfieldContribution
    {
        InheritLayer,
        Include,
        Exclude
    }

    /// <summary>
    /// NOT modeled by the
    /// current legacy SC2TerrainData at all: today's procedural terrain is a
    /// single flat heightmap + one triangle-pair-index array, with no
    /// discrete "layer" concept (gameplay vs. decorative vs. water surfaces
    /// aren't distinguished). SC2TerrainLegacyBridge therefore produces an
    /// EMPTY Layers list for now — this type exists so the shape is ready
    /// for whenever a real multi-layer authoring workflow is added (a much
    /// larger feature, not attempted in this pass).
    /// </summary>
    public sealed class SC2TerrainLayerDocument
    {
        public string LayerId;
        public string Name;
        public SC2TerrainLayerRole Role;
        public bool IncludeInVisualTerrain = true;
        public bool IncludeInHfield = true;
        public bool IncludeInCollisionSource = true;
        public List<string> MaterialPairIds = new();
    }
}
