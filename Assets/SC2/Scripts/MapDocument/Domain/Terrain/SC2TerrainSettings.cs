using UnityEngine;

namespace SC2.MapEditor.Document
{
    /// <summary>No whole-terrain
    /// simplification setting exists in the legacy model today (SimplifyRatio
    /// lives per-pair, already on SC2MaterialPairDocument) — SC2TerrainLegacyBridge
    /// leaves this at defaults; it's a seam for a future feature, not a
    /// projection of anything that currently exists.</summary>
    public sealed class SC2TerrainSimplificationSettings
    {
        public bool Enabled;
    }

    /// <summary>Same status as SC2TerrainSimplificationSettings — no
    /// per-terrain normals setting exists in the legacy model
    /// (TerrainMeshBuilder always calls RecalculateNormals()); defaults only.</summary>
    public sealed class SC2TerrainNormalsSettings
    {
        public bool RecalculateOnEdit = true;
    }

    /// <summary>Mirrors the legacy SC2TerrainData.Symmetry/SymmetryCenter/
    /// RadialCount fields directly — this one DOES have a real legacy value
    /// to project.</summary>
    public sealed class SC2SymmetrySettings
    {
        public SymmetryMode Mode = SymmetryMode.None;
        public Vector3 Center;
        /// <summary>Only meaningful when Mode==Radial. See
        /// SC2TerrainData.RadialCount's own doc comment.</summary>
        public int RadialCount = 4;
    }
}
