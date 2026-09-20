using System;
using System.Collections.Generic;
using UnityEngine;

namespace SC2.MapEditor.Document
{
    /// <summary>
    /// The SC2TerrainData-
    /// constructing half of the reverse bridge (see SC2MapDocumentToLegacyBridge.cs
    /// for the pure-data core and why the split exists). This is the only
    /// place a Texture2D actually gets resolved, via SC2TerrainMaterialFactory.
    /// ResolveTextureByPathOrName — the exact same resolution chain
    /// SC2RuntimeTerrainPersistence's legacy JSON Load path already uses, so
    /// a document-package Load and a legacy-JSON Load behave identically for
    /// texture resolution. Excluded from the standalone characterization
    /// project the same way every other bridge's ScriptableObject half is
    /// (references the SC2TerrainData ScriptableObject).
    /// </summary>
    public static partial class SC2MapDocumentToLegacyBridge
    {
        public static SC2TerrainData ApplyToLegacy(SC2MapDocument document, ISC2BlobStore blobStore)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            if (blobStore == null) throw new ArgumentNullException(nameof(blobStore));

            var mapName = ReconstructMapName(document);
            var terrain = ReconstructTerrain(document.Terrain, blobStore);
            var data = ApplyTerrainResult(terrain, string.IsNullOrEmpty(mapName) ? "RuntimeTerrainData" : mapName);
            data.MapName = mapName;

            // document.Terrain.
            // Background (when present — see SC2TerrainDocument.Background's
            // own doc comment) is itself a full SC2TerrainDocument, built
            // recursively by the forward bridge — reconstruct it the exact
            // same way as the top-level terrain, via the same
            // ApplyTerrainResult helper, rather than letting it come back
            // null and silently regenerate a brand-new default background
            // (discarding real textures/ShaderFlags/painted RNM/baked
            // lighting/sculpted heights every single reload).
            if (document.Terrain.Background != null)
            {
                var bgTerrain = ReconstructTerrain(document.Terrain.Background, blobStore);
                data.BackgroundTerrainData = ApplyTerrainResult(bgTerrain,
                    (string.IsNullOrEmpty(mapName) ? "RuntimeTerrainData" : mapName) + " Background (generated)");
                // Matches SC2BackgroundTerrain.Regenerate's own convention —
                // this nested instance is never independently saved as its
                // own asset; it round-trips through the PARENT document's
                // own Background field instead (see that field's own doc
                // comment for why a plain reference alone can't do this).
                data.BackgroundTerrainData.hideFlags = HideFlags.DontSave;
            }

            // `data` is a fresh instance here, so CostsGridSize is still its
            // default (1024) unless restored from the saved document.
            // SC2HfieldLegacyBridge.BuildFromLegacy (the forward/save
            // direction) already writes the real CostsGridSize into
            // MapX/MapY - this read-back was missing, so a saved map using
            // any other grid size reloaded with CostsGridSize silently
            // reset to 1024, and EnsureCostsAndTerrainTypes' own
            // mismatched-length guard would then destructively resize the
            // just-loaded CostsBlocks/TerrainTypes arrays to match (the
            // exact failure mode SC2RuntimeTerrainPersistence's own legacy
            // JSON load path already had a comment warning about and a
            // real fix for - this document-package path just never got the
            // matching fix). 0 means an older save written before MapX/MapY
            // were populated - keep the in-memory default rather than
            // collapsing the grid to 0x0.
            if (document.Hfield != null && document.Hfield.MapX > 0)
                data.CostsGridSize = document.Hfield.MapX;
            var gridCellCount = data.CostsGridSize * data.CostsGridSize;
            var hfield = ReconstructHfield(document.Hfield, blobStore, gridCellCount);
            data.TerrainTypes = hfield.TerrainTypes;

            var costs = ReconstructCosts(document.Costs, blobStore);
            data.CostsBlocks = costs.Blocks;
            data.PlayableWater = costs.PlayableWater;

            data.Collision2HeightOffset = ReconstructCollision2HeightOffset(document.Collision2);
            data.Collision2PolygonBudget = ReconstructCollision2PolygonBudget(document.Collision2);
            data.Collision2PreviewBytes = NullIfEmpty(ReadBytes(blobStore, document.Collision2.PreviewMeshBytes));
            data.CustomCollision2Bytes = NullIfEmpty(ReadBytes(blobStore, document.Collision2.CustomMeshBytes));
            data.UseCustomCollision2 = document.Collision2.UseCustomMesh && data.CustomCollision2Bytes != null;
            data.Collision2HidePreview = document.Collision2.HidePreview;
            data.Collision2Wireframe = document.Collision2.Wireframe;

            if (document.Scenario != null)
            {
                data.DisplayName = document.Scenario.KnownFields?.Name ?? "";
                data.MapShotBgraPixels = NullIfEmpty(ReadBytes(blobStore, document.Scenario.MapShotBgra));
                data.CustomMinimapPng = NullIfEmpty(ReadBytes(blobStore, document.Scenario.CustomMinimapPng));
            }

            var env = ReconstructEnvironment(document.Environment);
            data.EnvironmentHeader = env.Header;
            data.EnvironmentMap = env.EnvironmentMap;
            data.SkyMap = env.SkyMap;
            data.TexturePath3 = env.TexturePath3;
            data.ColorCorrectionCube = env.ColorCorrectionCube;
            data.FogOfWarCorrectionCube = env.FogOfWarCorrectionCube;
            data.EnvironmentTextureSourceTags = (string[])env.TextureExport.SourceTags.Clone();
            data.EnvironmentTextureForceExport = (bool[])env.TextureExport.ForceExport.Clone();
            data.WaterPlaneSize = env.WaterPlaneSize;
            data.WaterUvScale = env.WaterUvScale;
            data.WaterTexturePaths = env.WaterTexturePaths;
            data.WaterDepthAutoGenerate = env.WaterDepthAutoGenerate;
            data.WaterDepthResolution = env.WaterDepthResolution;
            data.WaterDepthMargin = env.WaterDepthMargin;
            data.WaterDepthFalloffPower = env.WaterDepthFalloffPower;
            data.WaterDepthMinAlpha = env.WaterDepthMinAlpha;
            data.WaterDepthPaintBorder = env.WaterDepthPaintBorder;
            data.WaterDepthImageBorderOutline = env.WaterDepthImageBorderOutline;

            var objects = ReconstructObjects(document.Objects);
            data.ObjProps = objects.Props;
            data.ObjMarkers = objects.Markers;
            data.ObjGraphs = objects.Graphs;
            data.ObjArmies = objects.Armies;
            data.ObjUnits = objects.Units;
            data.ObjZones = objects.Zones;
            data.ObjChains = objects.Chains;
            data.ObjSymmetryGroups = objects.SymmetryGroups;
            data.ObjReferenceUnits = objects.ReferenceUnits;
            data.NextObjectId = Mathf.Max(1, objects.NextObjectId);
            data.ObjSaveLuaTextOriginal = objects.SaveLuaTextOriginal;
            data.ObjScenarioLuaTextOriginal = objects.ScenarioLuaTextOriginal;

            return data;
        }

        private static Texture2D ResolveSlot(TextureSlotInfo slot, string materialName, string slotLabel) =>
            SC2TerrainMaterialFactory.ResolveTextureByPathOrName(slot.Name, slot.ResourcePath, materialName, slotLabel);

        /// <summary>Builds a live SC2TerrainData from a reconstructed
        /// TerrainResult — factored out of ApplyToLegacy so the decorative
        /// background terrain (a full SC2TerrainData in its own right, see
        /// SC2TerrainDocument.Background's own doc comment) can share this
        /// exact same reconstruction instead of duplicating it. Calls
        /// EnsureSerializableState/InvalidateGeometryCache itself (moved out
        /// of ApplyToLegacy's own tail, which used to call them once for the
        /// top-level instance only) so BOTH instances end up in the same
        /// self-consistent state every other SC2TerrainData is expected to
        /// be in — the background is a real target for SC2BackgroundTerrain.
        /// EnsureGenerated/TopologyMatches, SC2MaterialsPairsMode, and
        /// SC2GeometryMode/SC2PaintMode immediately after this returns, not
        /// an inert data blob.</summary>
        private static SC2TerrainData ApplyTerrainResult(TerrainResult terrain, string instanceName)
        {
            var data = ScriptableObject.CreateInstance<SC2TerrainData>();
            data.name = instanceName;
            data.GridWidth = terrain.GridWidth;
            data.GridHeight = terrain.GridHeight;
            data.CellSize = terrain.CellSize;
            data.IsInitialized = terrain.IsInitialized;
            data.Heights = terrain.Heights;
            data.Symmetry = terrain.Symmetry;
            data.SymmetryCenter = terrain.SymmetryCenter;
            data.RadialCount = terrain.RadialCount;
            data.DataVersion = terrain.DataVersion;
            data.ImportOnlyMode = terrain.ImportOnlyMode;
            data.SingleBspNodePerLayer = terrain.SingleBspNodePerLayer;
            data.VanillaCandidateBspSplit = terrain.VanillaCandidateBspSplit;
            data.PlayableAreaName = terrain.PlayableAreaName;
            data.BackgroundTerrainEnabled = terrain.BackgroundTerrainEnabled;
            data.BackgroundTerrainSize = terrain.BackgroundTerrainSize;
            data.BackgroundTerrainUvScale = terrain.BackgroundTerrainUvScale;
            data.GlobalUvScaleEnabled = terrain.GlobalUvScaleEnabled;
            data.GlobalUvScale = terrain.GlobalUvScale;
            data.Uv1HeightWeight = terrain.Uv1HeightWeight;
            data.RnmBakeSyncSunDirection = terrain.RnmBakeSyncSunDirection;
            data.RnmBakeSyncSunColor = terrain.RnmBakeSyncSunColor;
            data.RnmBakeSyncSunIntensity = terrain.RnmBakeSyncSunIntensity;
            data.RnmBakeSyncShadowColor = terrain.RnmBakeSyncShadowColor;
            data.RnmBakeOverrideSunDirection = terrain.RnmBakeOverrideSunDirection;
            data.RnmBakeOverrideSunColor = terrain.RnmBakeOverrideSunColor;
            data.RnmBakeOverrideSunIntensity = terrain.RnmBakeOverrideSunIntensity;
            data.RnmBakeOverrideShadowColor = terrain.RnmBakeOverrideShadowColor;
            data.RnmBakeShadowSoftRadius = terrain.RnmBakeShadowSoftRadius;
            data.TrianglePairIndex = terrain.TrianglePairIndex;

            data.Materials = new List<SC2Material>(terrain.Materials.Count);
            for (var i = 0; i < terrain.Materials.Count; i++)
            {
                var m = terrain.Materials[i];
                m.Diffuse = ResolveSlot(terrain.MaterialDiffuse[i], m.Name, "Diffuse");
                m.Normal = ResolveSlot(terrain.MaterialNormal[i], m.Name, "Normal");
                m.Mask = ResolveSlot(terrain.MaterialMask[i], m.Name, "Mask");
                m.DiffuseForceExport = terrain.MaterialDiffuse[i].ForceExport;
                m.NormalForceExport = terrain.MaterialNormal[i].ForceExport;
                m.MaskForceExport = terrain.MaterialMask[i].ForceExport;
                data.Materials.Add(m);
            }
            data.Pairs = terrain.Pairs;

            data.EnsureSerializableState();
            data.InvalidateGeometryCache();
            return data;
        }
    }
}
