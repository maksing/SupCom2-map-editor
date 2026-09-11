using System;

namespace SC2.MapEditor.Document
{
    /// <summary>
    /// The SC2TerrainData-accepting half of SC2ObjectsLegacyBridge — kept in
    /// its own file so it can be left OUT of the standalone characterization
    /// test project's compile list, matching SC2TerrainLegacyBridge.
    /// ScriptableObject.cs's reasoning exactly.
    /// </summary>
    public static partial class SC2ObjectsLegacyBridge
    {
        public static SC2ObjectsDocument BuildFromLegacy(SC2TerrainData data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            data.EnsureObjectIds();
            return BuildFromLegacy(
                data.ObjProps, data.ObjMarkers, data.ObjGraphs, data.ObjArmies,
                data.ObjUnits, data.ObjZones, data.ObjChains, data.ObjSymmetryGroups,
                data.NextObjectId, data.ObjSaveLuaTextOriginal, data.ObjScenarioLuaTextOriginal);
        }
    }
}
