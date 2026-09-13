using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace SC2.MapEditor.Document
{
    /// <summary>
    /// The reverse of every
    /// *LegacyBridge.BuildFromLegacy this project already has: given a
    /// SC2MapDocument (loaded from a manifest.json document package), rebuild
    /// the plain values a legacy SC2TerrainData needs. This is the pure-data
    /// core (testable standalone, mirrors every other bridge's own split) —
    /// it returns plain result POCOs (using the same UnityEngine-independent-
    /// enough types every other bridge's core already does: Vector3/Color are
    /// fine since this project's standalone test csproj already references
    /// UnityEngine.CoreModule.dll for exactly this reason), never constructs
    /// a SC2TerrainData ScriptableObject itself, and never resolves a
    /// Texture2D (that needs Resources.Load/SC2TerrainMaterialFactory, live
    /// Unity APIs this core deliberately stays free of) — see
    /// SC2MapDocumentToLegacyBridge.ScriptableObject.cs for the thin layer
    /// that actually builds a SC2TerrainData using these results.
    ///
    /// Este7.S4 is explicitly the plan's own highest-risk stage — a bug here
    /// reads a real saved map wrong or empty — so every reconstruction below
    /// favors an honest, safe default (empty list, all-zero header, -1/
    /// unassigned index) over guessing when a referenced Id/slot can't be
    /// resolved, the same posture SC2MaterialPairLinker.RelinkPairsToMaterials
    /// already established for "a reference no longer resolves."
    /// </summary>
    public static partial class SC2MapDocumentToLegacyBridge
    {
        /// <summary>A resolved-or-not texture reference, kept separate from
        /// SC2Material itself since the pure core never touches Texture2D —
        /// see this file's own header comment.</summary>
        public sealed class TextureSlotInfo
        {
            public string Name = "";
            public string ResourcePath = "";
        }

        public sealed class TerrainResult
        {
            public float[] Heights = Array.Empty<float>();
            public int GridWidth;
            public int GridHeight;
            public float CellSize;
            public SymmetryMode Symmetry;
            public Vector3 SymmetryCenter;
            public int RadialCount;
            public bool IsInitialized;
            public int DataVersion;
            public bool ImportOnlyMode;
            public bool SingleBspNodePerLayer;
            public bool VanillaCandidateBspSplit = true;
            public string PlayableAreaName = "";
            public bool BackgroundTerrainEnabled;
            public int BackgroundTerrainSize = 4096;
            public float BackgroundTerrainUvScale = 1024f;
            public bool GlobalUvScaleEnabled;
            public float GlobalUvScale = 4f;
            public bool RnmBakeSyncSunDirection = true;
            public bool RnmBakeSyncSunColor = true;
            public bool RnmBakeSyncSunIntensity = true;
            public bool RnmBakeSyncShadowColor = true;
            public Vector3 RnmBakeOverrideSunDirection = new Vector3(0.3f, 0.8f, 0.3f);
            public Color RnmBakeOverrideSunColor = Color.white;
            public float RnmBakeOverrideSunIntensity = 1f;
            public Color RnmBakeOverrideShadowColor = new Color(0.64f, 0.77f, 0.92f, 1f);
            public int RnmBakeShadowSoftRadius = 6;
            public List<SC2Material> Materials = new();
            public List<TextureSlotInfo> MaterialDiffuse = new();
            public List<TextureSlotInfo> MaterialNormal = new();
            public List<TextureSlotInfo> MaterialMask = new();
            public List<SC2MaterialPair> Pairs = new();
            public short[] TrianglePairIndex = Array.Empty<short>();
        }

        public static TerrainResult ReconstructTerrain(SC2TerrainDocument doc, ISC2BlobStore blobStore)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (blobStore == null) throw new ArgumentNullException(nameof(blobStore));

            var result = new TerrainResult
            {
                GridWidth = doc.Grid.Width,
                GridHeight = doc.Grid.Height,
                CellSize = doc.Grid.CellSize,
                Symmetry = doc.Symmetry.Mode,
                SymmetryCenter = doc.Symmetry.Center,
                RadialCount = doc.Symmetry.RadialCount,
                IsInitialized = doc.IsInitialized,
                DataVersion = doc.DataVersion,
                ImportOnlyMode = doc.ImportOnlyMode,
                SingleBspNodePerLayer = doc.SingleBspNodePerLayer,
                VanillaCandidateBspSplit = doc.VanillaCandidateBspSplit,
                PlayableAreaName = doc.PlayableAreaName ?? "",
                BackgroundTerrainEnabled = doc.BackgroundTerrainEnabled,
                BackgroundTerrainSize = doc.BackgroundTerrainSize,
                BackgroundTerrainUvScale = doc.BackgroundTerrainUvScale,
                GlobalUvScaleEnabled = doc.GlobalUvScaleEnabled,
                GlobalUvScale = doc.GlobalUvScale,
                RnmBakeSyncSunDirection = doc.RnmBakeSyncSunDirection,
                RnmBakeSyncSunColor = doc.RnmBakeSyncSunColor,
                RnmBakeSyncSunIntensity = doc.RnmBakeSyncSunIntensity,
                RnmBakeSyncShadowColor = doc.RnmBakeSyncShadowColor,
                RnmBakeOverrideSunDirection = doc.RnmBakeOverrideSunDirection,
                RnmBakeOverrideSunColor = doc.RnmBakeOverrideSunColor,
                RnmBakeOverrideSunIntensity = doc.RnmBakeOverrideSunIntensity,
                RnmBakeOverrideShadowColor = doc.RnmBakeOverrideShadowColor,
                RnmBakeShadowSoftRadius = doc.RnmBakeShadowSoftRadius,
                Heights = ReadFloats(blobStore, doc.Grid.HeightsFloat32),
            };

            // Id -> position in result.Materials (== position in doc.Materials,
            // since both lists are built in the same single pass below) —
            // needed to translate Material1Id/Material2Id back into the
            // legacy Mat1Index/Mat2Index every existing reader resolves
            // against.
            var materialIndexById = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < doc.Materials.Count; i++)
            {
                var m = doc.Materials[i];
                materialIndexById[m.Id] = i;
                result.Materials.Add(new SC2Material { Id = m.Id, Name = m.Name, Color = m.DebugColor });
                result.MaterialDiffuse.Add(ToSlotInfo(m.Diffuse));
                result.MaterialNormal.Add(ToSlotInfo(m.Normal));
                result.MaterialMask.Add(ToSlotInfo(m.Mask));
            }

            // slot (position in doc.MaterialPairs / result.Pairs) -> pair Id —
            // mirrors the forward bridge's own pairIdBySlot exactly (slot i
            // == pairs[i] at the moment a legacy view is built/rebuilt).
            var pairIdBySlot = new string[doc.MaterialPairs.Count];
            for (var i = 0; i < doc.MaterialPairs.Count; i++)
            {
                var p = doc.MaterialPairs[i];
                pairIdBySlot[i] = p.Id;

                var rnmMaskBytes = Array.Empty<byte>();
                int maskWidth = 2048, maskHeight = 2048;
                // Baked RNM lighting must come back too — Load prefers the
                // document package, so dropping it here silently reverted an
                // already-baked map to the neutral placeholder on reopen.
                var bakedRgb0 = Array.Empty<byte>();
                var bakedRgb1 = Array.Empty<byte>();
                var bakedRgb2 = Array.Empty<byte>();
                var bakedShadowAlpha = Array.Empty<byte>();
                if (!string.IsNullOrEmpty(p.RnmSetId))
                {
                    var set = doc.Rnm.Find(p.RnmSetId);
                    if (set != null)
                    {
                        rnmMaskBytes = ReadBytes(blobStore, set.MaskBytes);
                        maskWidth = set.Width;
                        maskHeight = set.Height;
                        bakedRgb0 = ReadBytes(blobStore, set.BakedRgb0);
                        bakedRgb1 = ReadBytes(blobStore, set.BakedRgb1);
                        bakedRgb2 = ReadBytes(blobStore, set.BakedRgb2);
                        bakedShadowAlpha = ReadBytes(blobStore, set.BakedShadowAlpha);
                    }
                }

                result.Pairs.Add(new SC2MaterialPair
                {
                    Id = p.Id,
                    Name = p.Name,
                    Mat1Id = p.Material1Id ?? "",
                    Mat2Id = p.Material2Id ?? "",
                    Mat1Index = ResolveMaterialIndex(p.Material1Id, materialIndexById),
                    Mat2Index = ResolveMaterialIndex(p.Material2Id, materialIndexById),
                    PairColor = p.DebugColor,
                    RnmMaskName = !string.IsNullOrEmpty(p.RnmSetId) ? p.RnmSetId : "mask01",
                    ShaderFlags = new List<SC2ShaderFlag>(p.ShaderFlags ?? new List<SC2ShaderFlag>()),
                    RnmMaskBytes = rnmMaskBytes,
                    MaskWidth = maskWidth,
                    MaskHeight = maskHeight,
                    TilingScale = p.TilingScale,
                    SimplifyRatio = p.SimplifyRatio,
                    RnmBakedRgb0 = bakedRgb0,
                    RnmBakedRgb1 = bakedRgb1,
                    RnmBakedRgb2 = bakedRgb2,
                    RnmBakedShadowAlpha = bakedShadowAlpha,
                });
            }

            result.TrianglePairIndex = ReconstructTrianglePairIndex(doc.PairAssignments, pairIdBySlot, blobStore);
            return result;
        }

        private static TextureSlotInfo ToSlotInfo(SC2TextureReference reference) => new()
        {
            Name = reference?.Name ?? "",
            ResourcePath = reference?.ResourcePath ?? "",
        };

        private static int ResolveMaterialIndex(string materialId, Dictionary<string, int> materialIndexById)
        {
            if (string.IsNullOrEmpty(materialId)) return -1;
            return materialIndexById.TryGetValue(materialId, out var index) ? index : -1;
        }

        /// <summary>SC2TrianglePairMap.SlotIds is the document's own append-
        /// only slot table, which may have MORE entries than
        /// doc.MaterialPairs (a deleted pair tombstones its slot rather than
        /// removing it — see that type's own doc comment) — so resolving a
        /// triangle's final legacy index goes slot -> pair Id (via SlotIds)
        /// -> that pair's CURRENT position in result.Pairs (via
        /// pairIdBySlot, inverted), not slot -> pairIdBySlot[slot] directly.
        /// A triangle whose pair was deleted correctly comes back
        /// unassigned (-1), the same "recompute from where the Id currently
        /// lives" safety net SC2MaterialPairLinker already uses for material
        /// deletion.</summary>
        private static short[] ReconstructTrianglePairIndex(SC2TrianglePairMap map, string[] pairIdBySlot, ISC2BlobStore blobStore)
        {
            var slotIndexPerTriangle = ReadShorts(blobStore, map.SlotIndexPerTriangleInt16);
            var result = new short[slotIndexPerTriangle.Length];

            var slotIdToPairsIndex = new Dictionary<string, short>(StringComparer.Ordinal);
            for (short i = 0; i < pairIdBySlot.Length; i++)
                if (!string.IsNullOrEmpty(pairIdBySlot[i]))
                    slotIdToPairsIndex[pairIdBySlot[i]] = i;

            for (var i = 0; i < slotIndexPerTriangle.Length; i++)
            {
                var slot = slotIndexPerTriangle[i];
                if (slot < 0 || slot >= map.SlotIds.Count) { result[i] = -1; continue; }
                var pairId = map.SlotIds[slot];
                result[i] = !string.IsNullOrEmpty(pairId) && slotIdToPairsIndex.TryGetValue(pairId, out var idx)
                    ? idx
                    : (short)-1;
            }

            return result;
        }

        public sealed class HfieldResult
        {
            public ushort[] TerrainTypes = Array.Empty<ushort>();
        }

        /// <param name="gridCellCount">SC2TerrainData.CostsGridSize squared —
        /// the fixed 1024x1024 grid every legacy TerrainTypes array is sized
        /// to, regardless of GridWidth/GridHeight (see that field's own
        /// comment on SC2TerrainData.cs).</param>
        public static HfieldResult ReconstructHfield(SC2HfieldDocument doc, ISC2BlobStore blobStore, int gridCellCount)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (blobStore == null) throw new ArgumentNullException(nameof(blobStore));

            var bytes = ReadBytes(blobStore, doc.TerrainTypesUInt8);
            var terrainTypes = new ushort[gridCellCount];
            if (bytes.Length == gridCellCount)
                for (var i = 0; i < gridCellCount; i++)
                    terrainTypes[i] = bytes[i];
            return new HfieldResult { TerrainTypes = terrainTypes };
        }

        public sealed class CostsResult
        {
            public List<SC2CostsBlockData> Blocks = new();
            public bool PlayableWater;
        }

        public static CostsResult ReconstructCosts(SC2CostsDocument doc, ISC2BlobStore blobStore)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (blobStore == null) throw new ArgumentNullException(nameof(blobStore));

            var result = new CostsResult();
            foreach (var layer in doc.Layers)
                result.Blocks.Add(new SC2CostsBlockData { Name = layer.FieldName, Navmesh = ReadBytes(blobStore, layer.FinalGrid) });
            // Reported bug: after a save/reload, SC2TerrainData.PlayableWater
            // silently reset to false even though the reloaded map's Costs
            // blocks were correctly the 5-block "playable water" set — the
            // Visibility menu's Naval/Amphibious toggles (which read
            // PlayableWater directly, not the block list) disappeared as a
            // result. PlayableWater has no field of its own anywhere in
            // SC2CostsDocument/the serializer — only the blocks do — so
            // re-derive it here the exact same way BuildFromLegacy already
            // infers SC2CostsDocument.Profile from the block names, instead
            // of leaving it at its default.
            result.PlayableWater = SC2CostsLegacyBridge.DetermineProfile(result.Blocks) == SC2CostsProfile.PlayableWater;
            return result;
        }

        public static float ReconstructCollision2HeightOffset(SC2CollisionDocument doc)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            return doc.Generation.VerticalOffset;
        }

        public static int ReconstructCollision2PolygonBudget(SC2CollisionDocument doc)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            return doc.Generation.MaxPolygons;
        }

        public sealed class EnvironmentResult
        {
            public byte[] Header = new byte[SC2EnvironmentDocument.HeaderSize];
            public string EnvironmentMap = "";
            public string SkyMap = "";
            public string TexturePath3 = "";
            public string ColorCorrectionCube = "";
            public string FogOfWarCorrectionCube = "";
            public int WaterPlaneSize = 4096;
            public float WaterUvScale = 1024f;
            public string[] WaterTexturePaths = Array.Empty<string>();
            public bool WaterDepthAutoGenerate;
            public int WaterDepthResolution = 512;
            public float WaterDepthMargin = 64f;
            public float WaterDepthFalloffPower = 2f;
            public float WaterDepthMinAlpha = 0.04f;
            public bool WaterDepthPaintBorder = true;
            public bool WaterDepthShoreOutline;
            public bool WaterDepthImageBorderOutline;
        }

        public static EnvironmentResult ReconstructEnvironment(SC2EnvironmentDocument doc)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));

            return new EnvironmentResult
            {
                Header = doc.WorkingHeader != null && doc.WorkingHeader.Length == SC2EnvironmentDocument.HeaderSize
                    ? (byte[])doc.WorkingHeader.Clone()
                    : new byte[SC2EnvironmentDocument.HeaderSize],
                EnvironmentMap = doc.EnvironmentMap ?? "",
                SkyMap = doc.SkyMap ?? "",
                TexturePath3 = doc.TexturePath3 ?? "",
                ColorCorrectionCube = doc.ColorCorrectionCube ?? "",
                FogOfWarCorrectionCube = doc.FogOfWarCorrectionCube ?? "",
                WaterPlaneSize = doc.WaterPlaneSize,
                WaterUvScale = doc.WaterUvScale,
                WaterTexturePaths = doc.WaterTexturePaths ?? Array.Empty<string>(),
                WaterDepthAutoGenerate = doc.WaterDepthAutoGenerate,
                WaterDepthResolution = doc.WaterDepthResolution,
                WaterDepthMargin = doc.WaterDepthMargin,
                WaterDepthFalloffPower = doc.WaterDepthFalloffPower,
                WaterDepthMinAlpha = doc.WaterDepthMinAlpha,
                WaterDepthPaintBorder = doc.WaterDepthPaintBorder,
                WaterDepthShoreOutline = doc.WaterDepthShoreOutline,
                WaterDepthImageBorderOutline = doc.WaterDepthImageBorderOutline,
            };
        }

        public sealed class ObjectsResult
        {
            public List<SC2ObjProp> Props = new();
            public List<SC2ObjMarker> Markers = new();
            public List<SC2ObjGraph> Graphs = new();
            public List<SC2ObjArmy> Armies = new();
            public List<SC2ObjUnitNode> Units = new();
            public List<SC2ObjZone> Zones = new();
            public List<SC2ObjChain> Chains = new();
            public List<SC2ObjSymmetryGroup> SymmetryGroups = new();
            public int NextObjectId;
            public string SaveLuaTextOriginal = "";
            public string ScenarioLuaTextOriginal = "";
        }

        /// <summary>List-level copy only (new List&lt;T&gt; instances, same
        /// element references), matching SC2ObjectsLegacyBridge.
        /// BuildFromLegacy's own explicit choice — see that method's doc
        /// comment for why sharing element references is safe/cheap here:
        /// this reconstructed SC2TerrainData is about to become the new
        /// live data via SC2TerrainShell.SetData, whose own DataChanged
        /// event triggers SC2ObjectsShell.EnsureObjectsDocumentSynced to
        /// (re)establish a properly independent document copy immediately
        /// afterward — see that method's own doc comment on why element-
        /// reference independence matters for undo specifically.</summary>
        public static ObjectsResult ReconstructObjects(SC2ObjectsDocument doc)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));

            return new ObjectsResult
            {
                Props = new List<SC2ObjProp>(doc.Props),
                Markers = new List<SC2ObjMarker>(doc.Markers),
                Graphs = new List<SC2ObjGraph>(doc.Graphs),
                Armies = new List<SC2ObjArmy>(doc.Armies),
                Units = new List<SC2ObjUnitNode>(doc.Units),
                Zones = new List<SC2ObjZone>(doc.Zones),
                Chains = new List<SC2ObjChain>(doc.Chains),
                SymmetryGroups = new List<SC2ObjSymmetryGroup>(doc.SymmetryGroups),
                NextObjectId = doc.NextObjectId,
                SaveLuaTextOriginal = doc.SaveLuaTextOriginal ?? "",
                ScenarioLuaTextOriginal = doc.ScenarioLuaTextOriginal ?? "",
            };
        }

        /// <summary>SC2TerrainData.MapName has no domain of its own — every
        /// bridge that touches map identity treats Scenario.KnownFields.Name
        /// as authoritative when set (SC2ScenarioShell keeps it in lockstep
        /// with live MapName renames), falling back to Identity.DisplayName
        /// (populated at document-snapshot time even when Scenario.
        /// KnownFields was never parsed — e.g. a from-scratch procedural map
        /// with no real scenario.lua to parse at all).</summary>
        public static string ReconstructMapName(SC2MapDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));

            if (!string.IsNullOrEmpty(document.Scenario?.KnownFields?.Name))
                return document.Scenario.KnownFields.Name;
            if (!string.IsNullOrEmpty(document.Identity?.DisplayName))
                return document.Identity.DisplayName;
            return "Untitled";
        }

        private static byte[] ReadBytes(ISC2BlobStore blobStore, SC2BlobReference reference)
        {
            if (blobStore == null || reference.IsEmpty)
                return Array.Empty<byte>();
            using var stream = blobStore.OpenRead(reference.ContentHash);
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return ms.ToArray();
        }

        private static float[] ReadFloats(ISC2BlobStore blobStore, SC2BlobReference reference)
        {
            var bytes = ReadBytes(blobStore, reference);
            var floats = new float[bytes.Length / sizeof(float)];
            if (floats.Length > 0)
                Buffer.BlockCopy(bytes, 0, floats, 0, floats.Length * sizeof(float));
            return floats;
        }

        private static short[] ReadShorts(ISC2BlobStore blobStore, SC2BlobReference reference)
        {
            var bytes = ReadBytes(blobStore, reference);
            var shorts = new short[bytes.Length / sizeof(short)];
            if (shorts.Length > 0)
                Buffer.BlockCopy(bytes, 0, shorts, 0, shorts.Length * sizeof(short));
            return shorts;
        }
    }
}
