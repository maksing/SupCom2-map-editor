#nullable disable
using System.Collections.Generic;

namespace SC2.MapEditor.SmartExport
{
    public static class SC2ExportDependencyGraph
    {
        public static SC2DirtyFlags ExpandStaleFlags(SC2DirtyFlags dirtyFlags)
        {
            var result = dirtyFlags;

            if ((dirtyFlags & SC2DirtyFlags.TerrainGeometry) != 0)
            {
                result |= SC2DirtyFlags.HfieldHeightmap;
                result |= SC2DirtyFlags.Costs;
                result |= SC2DirtyFlags.Collision2;
            }

            if ((dirtyFlags & SC2DirtyFlags.HfieldHeightmap) != 0)
            {
                result |= SC2DirtyFlags.Costs;
                result |= SC2DirtyFlags.Collision2;
            }

            if ((dirtyFlags & SC2DirtyFlags.Objects) != 0)
            {
                result |= SC2DirtyFlags.SaveLua;
                result |= SC2DirtyFlags.ScenarioLua;
            }

            return result;
        }

        public static bool IsDirtyOrStale(SC2DirtyFlags expandedDirtyFlags, SC2DirtyFlags fileMask)
        {
            return (expandedDirtyFlags & fileMask) != 0;
        }

        public static string GetStaleReason(SC2DirtyFlags originalDirtyFlags, SC2DirtyFlags fileMask)
        {
            if ((originalDirtyFlags & fileMask) != 0)
                return null;

            if ((fileMask & SC2DirtyFlags.HfieldHeightmap) != 0 &&
                (originalDirtyFlags & SC2DirtyFlags.TerrainGeometry) != 0)
                return "Stale because terrain geometry changed.";

            if ((fileMask & SC2DirtyFlags.Costs) != 0 &&
                ((originalDirtyFlags & SC2DirtyFlags.TerrainGeometry) != 0 ||
                 (originalDirtyFlags & SC2DirtyFlags.HfieldHeightmap) != 0))
                return "Stale because costs depend on hfield.";

            if ((fileMask & SC2DirtyFlags.Collision2) != 0 &&
                ((originalDirtyFlags & SC2DirtyFlags.TerrainGeometry) != 0 ||
                 (originalDirtyFlags & SC2DirtyFlags.HfieldHeightmap) != 0))
                return "Stale because collision2 depends on hfield.";

            if ((fileMask & SC2DirtyFlags.SaveLua) != 0 &&
                (originalDirtyFlags & SC2DirtyFlags.Objects) != 0)
                return "Stale because save.lua marker block depends on objects.";

            if ((fileMask & SC2DirtyFlags.ScenarioLua) != 0 &&
                (originalDirtyFlags & SC2DirtyFlags.Objects) != 0)
                return "Stale because scenario start positions depend on objects.";

            return null;
        }

        public static Dictionary<SC2ExportFileKind, SC2DirtyFlags> CreateDefaultFileMasks()
        {
            return new Dictionary<SC2ExportFileKind, SC2DirtyFlags>
            {
                { SC2ExportFileKind.TerrainBdf, SC2DirtyFlags.TerrainGeometry | SC2DirtyFlags.TerrainMaterials },
                { SC2ExportFileKind.HfieldBdf, SC2DirtyFlags.HfieldHeightmap | SC2DirtyFlags.HfieldTerrainTypes },
                { SC2ExportFileKind.CostsBdf, SC2DirtyFlags.Costs },
                { SC2ExportFileKind.Collision2Bdf, SC2DirtyFlags.Collision2 },
                { SC2ExportFileKind.MapObjsBdf, SC2DirtyFlags.Objects },
                { SC2ExportFileKind.InfoBdf, SC2DirtyFlags.Info },
                { SC2ExportFileKind.DecalsBdf, SC2DirtyFlags.Decals },
                { SC2ExportFileKind.DecalDiffuseAtlas, SC2DirtyFlags.Decals },
                { SC2ExportFileKind.DecalNormalAtlas, SC2DirtyFlags.Decals },
                { SC2ExportFileKind.SaveLua, SC2DirtyFlags.SaveLua },
                { SC2ExportFileKind.ScenarioLua, SC2DirtyFlags.ScenarioLua },
                { SC2ExportFileKind.ScriptLua, SC2DirtyFlags.ScriptLua }
            };
        }
    }
}

