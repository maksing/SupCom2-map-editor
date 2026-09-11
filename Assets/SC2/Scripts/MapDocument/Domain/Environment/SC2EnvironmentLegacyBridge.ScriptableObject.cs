using System;

namespace SC2.MapEditor.Document
{
    /// <summary>
    /// The SC2TerrainData-accepting half of SC2EnvironmentLegacyBridge — kept
    /// in its own file so it can be left OUT of the standalone
    /// characterization test project's compile list, matching
    /// SC2TerrainLegacyBridge.ScriptableObject.cs's reasoning exactly
    /// (SC2TerrainData is a ScriptableObject, can't be constructed outside a
    /// live Unity process).
    /// </summary>
    public static partial class SC2EnvironmentLegacyBridge
    {
        public static SC2EnvironmentDocument BuildFromLegacy(SC2TerrainData data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            data.EnsureEnvironmentHeader();
            return BuildFromLegacy(
                data.EnvironmentHeader,
                data.EnvironmentMap,
                data.SkyMap,
                data.TexturePath3,
                data.ColorCorrectionCube,
                data.FogOfWarCorrectionCube,
                data.WaterPlaneSize,
                data.WaterUvScale,
                data.WaterTexturePaths,
                data.WaterDepthAutoGenerate,
                data.WaterDepthResolution,
                data.WaterDepthMargin,
                data.WaterDepthFalloffPower,
                data.WaterDepthMinAlpha,
                data.WaterDepthPaintBorder);
        }
    }
}
