using System;

namespace SC2.MapEditor.Document
{
    /// <summary>
    /// Starts with the same READ-ONLY legacy-view snapshot
    /// pattern used for Terrain: build a SC2EnvironmentDocument from
    /// the live SC2TerrainData fields, write nothing back. No live editing
    /// path (SC2EnvironmentShell) has been changed to go through this yet.
    ///
    /// Split into a pure-data core (testable standalone — this domain has
    /// zero UnityEngine dependency at all, simpler than Terrain's) and a
    /// thin SC2TerrainData-accepting overload in a separate file/partial
    /// (SC2EnvironmentLegacyBridge.ScriptableObject.cs), excluded from the
    /// standalone characterization project for the same reason as
    /// SC2TerrainLegacyBridge's ScriptableObject half.
    /// </summary>
    public static partial class SC2EnvironmentLegacyBridge
    {
        public static SC2EnvironmentDocument BuildFromLegacy(
            byte[] environmentHeader,
            string environmentMap,
            string skyMap,
            string texturePath3,
            string colorCorrectionCube,
            string fogOfWarCorrectionCube,
            int waterPlaneSize = 4096,
            float waterUvScale = 1024f,
            string[] waterTexturePaths = null,
            bool waterDepthAutoGenerate = false,
            int waterDepthResolution = 512,
            float waterDepthMargin = 64f,
            float waterDepthFalloffPower = 2f,
            float waterDepthMinAlpha = 0.04f,
            bool waterDepthPaintBorder = true,
            bool waterDepthImageBorderOutline = false)
        {
            if (environmentHeader == null) throw new ArgumentNullException(nameof(environmentHeader));
            if (environmentHeader.Length != SC2EnvironmentDocument.HeaderSize)
                throw new ArgumentException(
                    $"EnvironmentHeader must be exactly {SC2EnvironmentDocument.HeaderSize} bytes, was {environmentHeader.Length}.",
                    nameof(environmentHeader));

            var headerCopy = (byte[])environmentHeader.Clone();
            return new SC2EnvironmentDocument
            {
                // Both start equal — see this type's own doc comment for why
                // a legacy-view snapshot has no separate "as imported" form
                // to distinguish them with yet.
                OriginalHeader = (byte[])headerCopy.Clone(),
                WorkingHeader = headerCopy,
                EnvironmentMap = environmentMap ?? "",
                SkyMap = skyMap ?? "",
                TexturePath3 = texturePath3 ?? "",
                ColorCorrectionCube = colorCorrectionCube ?? "",
                FogOfWarCorrectionCube = fogOfWarCorrectionCube ?? "",
                WaterPlaneSize = waterPlaneSize,
                WaterUvScale = waterUvScale,
                WaterTexturePaths = waterTexturePaths ?? Array.Empty<string>(),
                WaterDepthAutoGenerate = waterDepthAutoGenerate,
                WaterDepthResolution = waterDepthResolution,
                WaterDepthMargin = waterDepthMargin,
                WaterDepthFalloffPower = waterDepthFalloffPower,
                WaterDepthMinAlpha = waterDepthMinAlpha,
                WaterDepthPaintBorder = waterDepthPaintBorder,
                WaterDepthImageBorderOutline = waterDepthImageBorderOutline,
            };
        }
    }
}
