using System;

namespace SC2.MapEditor.Document
{
    /// <summary>
    /// The SC2TerrainData-accepting half of SC2CostsLegacyBridge — kept in
    /// its own file so it can be left OUT of the standalone characterization
    /// test project's compile list, matching SC2TerrainLegacyBridge.
    /// ScriptableObject.cs's reasoning exactly.
    /// </summary>
    public static partial class SC2CostsLegacyBridge
    {
        public static SC2CostsDocument BuildFromLegacy(SC2TerrainData data, ISC2BlobStore blobStore)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            data.EnsureCostsAndTerrainTypes();
            return BuildFromLegacy(data.CostsBlocks, blobStore, data.CostsShoreHintOverrideMask);
        }
    }
}
