using System;

namespace SC2.MapEditor.Document
{
    /// <summary>
    /// The SC2TerrainData-accepting half of SC2TerrainLegacyBridge — kept in
    /// its own file (partial class) specifically so it can be left OUT of
    /// the standalone characterization test project's compile list, unlike
    /// SC2TerrainLegacyBridge.cs's pure-data core: SC2TerrainData is a
    /// ScriptableObject and can't be constructed outside a live Unity
    /// process (the same reason SC2ObjectsExport.cs/SC2ObjectsImport.cs are
    /// EditMode-test-only, not standalone-test-covered). This overload itself has no logic worth testing beyond
    /// "does it forward the right fields," which Unity EditMode tests can
    /// cover once run inside a live Editor.
    /// </summary>
    public static partial class SC2TerrainLegacyBridge
    {
        /// <summary>Calls data.EnsureStableIds() first (so repeated bridge
        /// calls against the same asset always see the same Ids — the whole
        /// point of "stable"), then delegates to the pure-data core, passing
        /// SC2TerrainMaterialFactory.ResolveResourcePath as the resolver the
        /// pure core can't reference directly (see BuildTextureReference's
        /// own doc comment).
        ///
        /// data.BackgroundTerrainData is itself a full SC2TerrainData, so a
        /// plain recursive call into this SAME method builds its document
        /// too (see SC2TerrainDocument.Background's own doc comment for why
        /// this exists at all) — no separate schema/bridge needed for the
        /// background's materials/pairs/heights/painted-RNM/baked-lighting.</summary>
        public static SC2TerrainDocument BuildFromLegacy(SC2TerrainData data, ISC2BlobStore blobStore)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            data.EnsureStableIds();
            var doc = BuildFromLegacy(
                data.Materials, data.Pairs, data.Heights, data.TrianglePairIndex,
                data.GridWidth, data.GridHeight, data.CellSize,
                data.Symmetry, data.SymmetryCenter, blobStore,
                data.IsInitialized, data.DataVersion,
                SC2TerrainMaterialFactory.ResolveResourcePath,
                data.RadialCount,
                data.ImportOnlyMode, data.SingleBspNodePerLayer, data.VanillaCandidateBspSplit,
                data.PlayableAreaName,
                data.BackgroundTerrainEnabled, data.BackgroundTerrainSize, data.BackgroundTerrainUvScale,
                data.GlobalUvScaleEnabled, data.GlobalUvScale,
                data.RnmBakeSyncSunDirection, data.RnmBakeSyncSunColor, data.RnmBakeSyncSunIntensity, data.RnmBakeSyncShadowColor,
                data.RnmBakeOverrideSunDirection, data.RnmBakeOverrideSunColor, data.RnmBakeOverrideSunIntensity, data.RnmBakeOverrideShadowColor,
                data.RnmBakeShadowSoftRadius,
                data.Uv1HeightWeight);
            AttachBlendLayers(doc, data.BlendLayerStacks, blobStore);
            if (data.BackgroundTerrainEnabled && data.BackgroundTerrainData != null)
                doc.Background = BuildFromLegacy(data.BackgroundTerrainData, blobStore);
            return doc;
        }
    }
}
