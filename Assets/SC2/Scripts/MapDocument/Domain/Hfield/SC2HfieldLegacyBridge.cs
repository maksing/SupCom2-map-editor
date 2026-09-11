using System;
using System.IO;

namespace SC2.MapEditor.Document
{
    /// <summary>
    /// Deliberately PARTIAL: only TerrainTypesUInt8 has real legacy data
    /// (SC2TerrainData.TerrainTypes). See SC2HfieldDocument's own doc
    /// comment for why HeightmapUInt16/Projection stay unpopulated.
    ///
    /// Split into a pure-data core (testable standalone) and a thin
    /// SC2TerrainData-accepting overload in a separate file/partial
    /// (SC2HfieldLegacyBridge.ScriptableObject.cs).
    /// </summary>
    public static partial class SC2HfieldLegacyBridge
    {
        public static SC2HfieldDocument BuildFromLegacy(ushort[] terrainTypes, int gridSize, ISC2BlobStore blobStore)
        {
            if (terrainTypes == null) throw new ArgumentNullException(nameof(terrainTypes));
            if (blobStore == null) throw new ArgumentNullException(nameof(blobStore));

            var bytes = new byte[terrainTypes.Length];
            for (var i = 0; i < terrainTypes.Length; i++)
            {
                // Terrain types are real 8-bit values, not just "usually fit
                // in a byte" -- if a real map ever violated that, a
                // silent truncating cast would corrupt data instead of
                // surfacing the anomaly (a value of 256 would wrap to 0, a
                // DIFFERENT real terrain type id, not a rounding error).
                if (terrainTypes[i] > byte.MaxValue)
                    throw new InvalidOperationException(
                        $"TerrainTypes[{i}] = {terrainTypes[i]} does not fit in a byte -- the plan's 8-bit assumption doesn't hold for this data.");
                bytes[i] = (byte)terrainTypes[i];
            }

            return new SC2HfieldDocument
            {
                MapX = gridSize,
                MapY = gridSize,
                TerrainTypesUInt8 = terrainTypes.Length > 0 ? blobStore.Put(new MemoryStream(bytes)) : default,
                SourceTerrainRevision = 0,
                // Missing overall, not Current: the document's defining
                // purpose (a resampled heightmap) has no real data at all,
                // even though TerrainTypesUInt8 does.
                State = SC2DerivedState.Missing,
            };
        }
    }
}
