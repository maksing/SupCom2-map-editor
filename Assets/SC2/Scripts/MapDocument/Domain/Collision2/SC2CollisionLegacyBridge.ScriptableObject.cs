using System;

namespace SC2.MapEditor.Document
{
    /// <summary>
    /// The SC2TerrainData-accepting half of SC2CollisionLegacyBridge — kept
    /// in its own file so it can be left OUT of the standalone
    /// characterization test project's compile list, matching
    /// SC2TerrainLegacyBridge.ScriptableObject.cs's reasoning exactly.
    /// </summary>
    public static partial class SC2CollisionLegacyBridge
    {
        public static SC2CollisionDocument BuildFromLegacy(SC2TerrainData data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            return BuildFromLegacy(data.Collision2HeightOffset, data.Collision2PolygonBudget);
        }
    }
}
