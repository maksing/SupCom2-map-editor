using System;
using System.Collections.Generic;
using System.IO;

namespace SC2.MapEditor.Document
{
    /// <summary>
    /// Same
    /// read-only legacy-view snapshot pattern as Terrain/Environment/
    /// Collision2. SC2TerrainData.CostsBlocks already stores the ONE real
    /// thing this document needs (Name + final Navmesh bytes per block);
    /// nothing writes live edits through this bridge yet.
    ///
    /// Split into a pure-data core (testable standalone) and a thin
    /// SC2TerrainData-accepting overload in a separate file/partial
    /// (SC2CostsLegacyBridge.ScriptableObject.cs).
    /// </summary>
    public static partial class SC2CostsLegacyBridge
    {
        // Real vanilla names — every
        // one still starts with "Land_", not "Water_"; the earlier version
        // of this set ({Land_Small, Land_Medium, Land_Large, Water_Small,
        // Water_Large}) was the same wrong placeholder set
        // SC2TerrainData.EnsureCostsAndTerrainTypes used to hardcode, not a
        // real observed block-name set. Kept in sync with
        // SC2TerrainData.PlayableWaterCostsBlockNames by hand (this file is
        // deliberately standalone-testable, no ScriptableObject reference).
        private static readonly HashSet<string> PlayableWaterNames = new(StringComparer.Ordinal)
        {
            "Land_Small_water_Surface_ShoreLine",
            "Land_medium",
            "Land_Large_water_Abyss_ShoreLine",
            "Land_Large_water_Seafloor",
            "Land_Medium_Water_Surface",
        };

        private static readonly HashSet<string> LandOnlyNames = new(StringComparer.Ordinal)
            { "Land_Small", "Land_Medium", "Land_Large" };

        public static SC2CostsDocument BuildFromLegacy(IReadOnlyList<SC2CostsBlockData> costsBlocks, ISC2BlobStore blobStore,
            byte[] shoreHintOverrideMask = null)
        {
            if (costsBlocks == null) throw new ArgumentNullException(nameof(costsBlocks));
            if (blobStore == null) throw new ArgumentNullException(nameof(blobStore));

            var doc = new SC2CostsDocument
            {
                Profile = DetermineProfile(costsBlocks),
                SourceHfieldRevision = 0,
                // FinalGrid is a faithful, complete copy of every block's
                // bytes — unlike Collision2's Mesh, there is real data here
                // for every layer, so Missing would be inaccurate.
                State = SC2DerivedState.Current,
            };

            foreach (var block in costsBlocks)
            {
                doc.Layers.Add(new SC2CostsLayerDocument
                {
                    // No separate stable Id field exists on SC2CostsBlockData
                    // — see SC2CostsLayerDocument.Id's own doc comment for
                    // why using Name directly is safe here.
                    Id = block.Name,
                    FieldName = block.Name,
                    DisplayName = block.Name,
                    FinalGrid = blobStore.Put(new MemoryStream(block.Navmesh ?? Array.Empty<byte>())),
                    OutlineOpenMask = PutMask(blobStore, block.OutlineOpenMask),
                });
            }

            doc.ShoreHintOverrideMask = PutMask(blobStore, shoreHintOverrideMask);
            return doc;
        }

        /// <summary>A mask is only stored when a cell is set: the common case, no cell marked, is a megabyte of zeros per block that
        /// the load side re-creates (SC2TerrainData.EnsureCostsAndTerrainTypes) anyway.</summary>
        private static SC2BlobReference PutMask(ISC2BlobStore blobStore, byte[] mask)
        {
            if (mask == null) return SC2BlobReference.Empty;
            var any = false;
            foreach (var cell in mask)
                if (cell != 0) { any = true; break; }
            return any ? blobStore.Put(new MemoryStream(mask)) : SC2BlobReference.Empty;
        }

        /// <summary>Internal, not private — SC2MapDocumentToLegacyBridge's
        /// own ReconstructCosts reuses this to re-derive SC2TerrainData.
        /// PlayableWater on load (see that call site's own comment for the
        /// real bug this fixes: PlayableWater has no field of its own
        /// anywhere in the document/serializer chain, only CostsBlocks
        /// does, so re-inferring from the reconstructed block names is the
        /// only way to restore it — the exact same logic BuildFromLegacy
        /// above already uses for the document's own Profile field, kept
        /// as one shared source instead of a third hand-copied name
        /// check).</summary>
        internal static SC2CostsProfile DetermineProfile(IReadOnlyList<SC2CostsBlockData> blocks)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var b in blocks) names.Add(b.Name);

            if (names.SetEquals(PlayableWaterNames)) return SC2CostsProfile.PlayableWater;
            if (names.SetEquals(LandOnlyNames)) return SC2CostsProfile.LandOnly;
            return SC2CostsProfile.ImportedCustom;
        }
    }
}
