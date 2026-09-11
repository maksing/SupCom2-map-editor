#nullable disable
using System;

namespace SC2.MapEditor.SmartExport
{
    [Flags]
    public enum SC2DirtyFlags
    {
        None = 0,
        TerrainGeometry = 1 << 0,
        TerrainMaterials = 1 << 1,
        HfieldHeightmap = 1 << 2,
        HfieldTerrainTypes = 1 << 3,
        Costs = 1 << 4,
        Collision2 = 1 << 5,
        Objects = 1 << 6,
        SaveLua = 1 << 7,
        ScenarioLua = 1 << 8,
        ScriptLua = 1 << 9,
        Info = 1 << 10,
        Decals = 1 << 11
    }
}

