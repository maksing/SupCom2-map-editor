using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace SC2.MapEditor.Document
{
    /// <summary>
    /// Builds a READ-ONLY
    /// snapshot SC2TerrainDocument from the live legacy fields; nothing here
    /// writes back to SC2TerrainData, and no live editing code path has been
    /// changed to go through this or through commands yet — that remains
    /// out of scope for this pass.
    ///
    /// Split into a pure-data core (BuildFromLegacy over plain lists/arrays —
    /// the actually-testable logic) and a thin SC2TerrainData-accepting
    /// overload used by real callers (SC2TerrainLegacyBridge.ScriptableObject.cs,
    /// a separate file/partial deliberately excluded from the standalone
    /// characterization test project's compile list, since SC2TerrainData is
    /// a ScriptableObject and can't be constructed there).
    /// </summary>
    public static partial class SC2TerrainLegacyBridge
    {

        public static SC2TerrainDocument BuildFromLegacy(
            IReadOnlyList<SC2Material> materials,
            IReadOnlyList<SC2MaterialPair> pairs,
            float[] heights,
            short[] trianglePairIndex,
            int gridWidth,
            int gridHeight,
            float cellSize,
            SymmetryMode symmetry,
            Vector3 symmetryCenter,
            ISC2BlobStore blobStore,
            bool isInitialized = false,
            int dataVersion = 0,
            Func<Texture2D, string> resolveResourcePath = null,
            int radialCount = 4,
            bool importOnlyMode = false,
            bool singleBspNodePerLayer = false,
            bool vanillaCandidateBspSplit = true,
            string playableAreaName = "",
            bool backgroundTerrainEnabled = false,
            int backgroundTerrainSize = 4096,
            float backgroundTerrainUvScale = 1024f,
            bool globalUvScaleEnabled = false,
            float globalUvScale = 4f,
            bool rnmBakeSyncSunDirection = true,
            bool rnmBakeSyncSunColor = true,
            bool rnmBakeSyncSunIntensity = true,
            bool rnmBakeSyncShadowColor = true,
            Vector3 rnmBakeOverrideSunDirection = default,
            Color rnmBakeOverrideSunColor = default,
            float rnmBakeOverrideSunIntensity = 1f,
            Color rnmBakeOverrideShadowColor = default,
            int rnmBakeShadowSoftRadius = 6,
            float uv1HeightWeight = 1f)
        {
            if (materials == null) throw new ArgumentNullException(nameof(materials));
            if (pairs == null) throw new ArgumentNullException(nameof(pairs));
            if (heights == null) throw new ArgumentNullException(nameof(heights));
            if (trianglePairIndex == null) throw new ArgumentNullException(nameof(trianglePairIndex));
            if (blobStore == null) throw new ArgumentNullException(nameof(blobStore));

            var doc = new SC2TerrainDocument
            {
                AuthoringMode = SC2TerrainAuthoringMode.ProceduralHeightmap,
                Grid = BuildGrid(heights, gridWidth, gridHeight, cellSize, blobStore),
                Symmetry = new SC2SymmetrySettings { Mode = symmetry, Center = symmetryCenter, RadialCount = radialCount },
                IsInitialized = isInitialized,
                DataVersion = dataVersion,
                ImportOnlyMode = importOnlyMode,
                SingleBspNodePerLayer = singleBspNodePerLayer,
                VanillaCandidateBspSplit = vanillaCandidateBspSplit,
                PlayableAreaName = playableAreaName ?? "",
                BackgroundTerrainEnabled = backgroundTerrainEnabled,
                BackgroundTerrainSize = backgroundTerrainSize,
                BackgroundTerrainUvScale = backgroundTerrainUvScale,
                GlobalUvScaleEnabled = globalUvScaleEnabled,
                GlobalUvScale = globalUvScale,
                Uv1HeightWeight = uv1HeightWeight,
                RnmBakeSyncSunDirection = rnmBakeSyncSunDirection,
                RnmBakeSyncSunColor = rnmBakeSyncSunColor,
                RnmBakeSyncSunIntensity = rnmBakeSyncSunIntensity,
                RnmBakeSyncShadowColor = rnmBakeSyncShadowColor,
                RnmBakeOverrideSunDirection = rnmBakeOverrideSunDirection,
                RnmBakeOverrideSunColor = rnmBakeOverrideSunColor,
                RnmBakeOverrideSunIntensity = rnmBakeOverrideSunIntensity,
                RnmBakeOverrideShadowColor = rnmBakeOverrideShadowColor,
                RnmBakeShadowSoftRadius = rnmBakeShadowSoftRadius,
            };

            foreach (var m in materials)
                doc.Materials.Add(BuildMaterial(m, resolveResourcePath));

            // Maps legacy 0-based Pairs-list-index -> the pair's own stable
            // Id, purely as local scratch for translating TrianglePairIndex
            // below — never persisted or exposed; SC2TrianglePairMap.SlotIds
            // is populated in the SAME order as `pairs` (slot i == pairs[i]),
            // which is fine precisely because SlotIds is append-only and
            // never renumbered from here on.
            var pairIdBySlot = new string[pairs.Count];
            for (var i = 0; i < pairs.Count; i++)
            {
                var p = pairs[i];
                EnsureId(p);
                pairIdBySlot[i] = p.Id;
                doc.MaterialPairs.Add(BuildPair(p, doc, blobStore));
            }

            doc.PairAssignments = BuildPairAssignments(trianglePairIndex, pairIdBySlot, blobStore);

            return doc;
        }

        private static void EnsureId(SC2Material m)
        {
            if (string.IsNullOrEmpty(m.Id)) m.Id = Guid.NewGuid().ToString("N");
        }

        private static void EnsureId(SC2MaterialPair p)
        {
            if (string.IsNullOrEmpty(p.Id)) p.Id = Guid.NewGuid().ToString("N");
        }

        private static SC2TerrainGrid BuildGrid(float[] heights, int gridWidth, int gridHeight, float cellSize, ISC2BlobStore blobStore)
        {
            var grid = new SC2TerrainGrid { Width = gridWidth, Height = gridHeight, CellSize = cellSize };

            if (heights.Length > 0)
            {
                var min = float.MaxValue;
                var max = float.MinValue;
                foreach (var h in heights)
                {
                    if (h < min) min = h;
                    if (h > max) max = h;
                }
                grid.MinimumHeight = min;
                grid.MaximumHeight = max;
            }

            grid.HeightsFloat32 = blobStore.Put(FloatsToStream(heights));
            return grid;
        }

        private static SC2MaterialDocument BuildMaterial(SC2Material m, Func<Texture2D, string> resolveResourcePath)
        {
            EnsureId(m);
            return new SC2MaterialDocument
            {
                Id = m.Id,
                Name = m.Name,
                Diffuse = BuildTextureReference(m.Diffuse, m.DiffuseForceExport, resolveResourcePath),
                Normal = BuildTextureReference(m.Normal, m.NormalForceExport, resolveResourcePath),
                Mask = BuildTextureReference(m.Mask, m.MaskForceExport, resolveResourcePath),
                DebugColor = m.Color,
            };
        }

        /// <summary>ResourcePath is populated via
        /// an externally-supplied resolveResourcePath delegate — previously
        /// only Name was recorded, which the reverse bridge (SC2MapDocumentTo
        /// LegacyBridge) can't safely resolve back to a live Texture2D from
        /// alone (two different textures in different source folders can
        /// share the same leaf name — the same ambiguity already fixed
        /// for the runtime texture picker). A delegate rather than calling
        /// SC2TerrainMaterialFactory.ResolveResourcePath directly, because
        /// that type isn't part of this file's standalone-testable compile
        /// closure (see the class doc comment) — the SC2TerrainData-
        /// accepting overload (SC2TerrainLegacyBridge.ScriptableObject.cs)
        /// passes it in; every other/older caller passes null, leaving
        /// ResourcePath empty exactly as before this change. Null resolver
        /// or a resolver returning null both mean "nothing to resolve," the
        /// same honest gap every other bridge already leaves for genuinely
        /// unavailable data.</summary>
        private static SC2TextureReference BuildTextureReference(Texture2D texture, bool forceExport, Func<Texture2D, string> resolveResourcePath)
        {
            if (texture == null) return new SC2TextureReference { ForceExport = forceExport };
            return new SC2TextureReference
            {
                Name = texture.name ?? "",
                ResourcePath = resolveResourcePath != null ? (resolveResourcePath(texture) ?? "") : "",
                ForceExport = forceExport,
            };
        }

        private static SC2MaterialPairDocument BuildPair(SC2MaterialPair p, SC2TerrainDocument doc, ISC2BlobStore blobStore)
        {
            EnsureId(p);

            string rnmSetId = "";
            if (p.RnmMaskBytes != null && p.RnmMaskBytes.Length > 0)
            {
                var existing = doc.Rnm.Find(p.RnmMaskName);
                if (existing == null)
                {
                    // First pair seen with this RnmMaskName defines the
                    // shared blob; a same-name pair with genuinely different
                    // bytes (a data inconsistency, not expected in practice)
                    // silently keeps whichever bytes were seen first rather
                    // than erroring — recoverable per this codebase's general
                    // "warn, don't hard-fail" import philosophy.
                    SC2BlobReference PutIfAny(byte[] bytes) =>
                        bytes != null && bytes.Length > 0
                            ? blobStore.Put(new MemoryStream(bytes))
                            : SC2BlobReference.Empty;

                    doc.Rnm.MaskSets.Add(new SC2RnmMaskSetDocument
                    {
                        Id = p.RnmMaskName,
                        Name = p.RnmMaskName,
                        Width = p.MaskWidth,
                        Height = p.MaskHeight,
                        MaskBytes = blobStore.Put(new MemoryStream(p.RnmMaskBytes)),
                        // Baked lighting rides along with the mask it belongs
                        // to; every pair sharing this name shares one bake.
                        BakedRgb0 = PutIfAny(p.RnmBakedRgb0),
                        BakedRgb1 = PutIfAny(p.RnmBakedRgb1),
                        BakedRgb2 = PutIfAny(p.RnmBakedRgb2),
                        BakedShadowAlpha = PutIfAny(p.RnmBakedShadowAlpha),
                    });
                }
                rnmSetId = p.RnmMaskName;
            }

            return new SC2MaterialPairDocument
            {
                Id = p.Id,
                Name = p.Name,
                Material1Id = ResolveMaterialId(p.Mat1Index, doc),
                Material2Id = ResolveMaterialId(p.Mat2Index, doc),
                DebugColor = p.PairColor,
                TilingScale = p.TilingScale,
                SimplifyRatio = p.SimplifyRatio,
                ShaderFlags = new List<SC2ShaderFlag>(p.ShaderFlags ?? new List<SC2ShaderFlag>()),
                RnmSetId = rnmSetId,
            };
        }

        private static string ResolveMaterialId(int legacyIndex, SC2TerrainDocument doc)
        {
            if (legacyIndex < 0 || legacyIndex >= doc.Materials.Count) return "";
            return doc.Materials[legacyIndex].Id;
        }

        private static SC2TrianglePairMap BuildPairAssignments(short[] trianglePairIndex, string[] pairIdBySlot, ISC2BlobStore blobStore)
        {
            var map = new SC2TrianglePairMap { SlotIds = new List<string>(pairIdBySlot) };

            var slotIndexPerTriangle = new short[trianglePairIndex.Length];
            for (var i = 0; i < trianglePairIndex.Length; i++)
            {
                var legacyIndex = trianglePairIndex[i];
                slotIndexPerTriangle[i] = legacyIndex >= 0 && legacyIndex < pairIdBySlot.Length ? legacyIndex : (short)-1;
            }

            map.SlotIndexPerTriangleInt16 = blobStore.Put(ShortsToStream(slotIndexPerTriangle));
            return map;
        }

        private static Stream FloatsToStream(float[] values)
        {
            var bytes = new byte[values.Length * sizeof(float)];
            Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
            return new MemoryStream(bytes);
        }

        private static Stream ShortsToStream(short[] values)
        {
            var bytes = new byte[values.Length * sizeof(short)];
            Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
            return new MemoryStream(bytes);
        }
    }
}
