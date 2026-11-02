# Game file formats

Layouts of the files the editor reads and writes, collected from the code and its comments, which record what was measured on the 56 vanilla maps. Offsets are in bytes inside the **decompressed** content unless a page says otherwise. All numbers are little-endian on PC (the only verified platform).
"Unknown" means the meaning was never confirmed: the editor preserves such fields and does not interpret them. The code is the source of truth; these pages say where to look and what is known.

Read [BdfIO](../Systems/BdfIO.md) first for the container, padding and offset-table rules that every `.bdf` shares.

| File | Page | Parser and writer |
|---|---|---|
| Container of every `.bdf` | [Container](Container.md) | `Runtime/BdfIO/Core/BdfContainer.cs` |
| Strings and pointers inside a `.bdf` | [String pool](StringPool.md) | `StringPoolWriter` in `BdfWriter.cs` |
| The spatial tree (EDH and EDB) of terrain, collision2 and decals | [Spatial tree](SpatialTree.md) | `TerrainLayout.cs`, `TerrainBspBuilder.cs` |
| `<Map>.terrain.win.bdf` | [Terrain](Terrain.md) | `Formats/Terrain/TerrainBdf.cs` |
| `<Map>.hfield.win.bdf` | [Hfield](Hfield.md) | `Formats/Hfield/HfieldBdf.cs` |
| `<Map>.costs.win.bdf` | [Costs](Costs.md) | `Formats/Costs/CostsBdf.cs` |
| `<Map>.collision2.win.bdf` | [Collision2](Collision2.md) | `Formats/Collision2/Collision2Bdf.cs` |
| `<Map>.info.win.bdf` | [Info](Info.md) | `Formats/Info/InfoBdf.cs` |
| `<Map>.mapobjs.win.bdf` | [MapObjs](MapObjs.md) | `Formats/MapObjs/MapObjsBdf.cs` |
| `decals.win.bdf` and its atlases | [Decals](Decals.md) | `Formats/Decals/DecalsBdf.cs`, `DecalsSpatialBuilder.cs` |
| Lighting mask textures, point clouds, minimap, map shot, water depth, material textures | [Texture files](TexturesAndMasks.md) | `SC2RnmMaskBaker`, `SC2PointCloudBaker`, `SC2DdsEncoder` |
| `_scenario.lua`, `_save.lua`, `_script.lua` | [Lua files](LuaFiles.md) | `SC2ObjectsExport` (the live path; the older `SC2ScenarioLuaWriter` and `SC2SaveLuaWriter` serve only the editor-only export window) |
| What the game's Lua does with a mission `_script.lua` (campaign setup, armies, AI, cinematics, menus) | [CampaignFramework](CampaignFramework.md) | the mission compiler (`SC2MissionCompiler`), described in [MissionEditor](../Systems/MissionEditor.md) |

The order in which the export writes them, and the rules about paths, is in the [export pipeline](../Systems/ExportPipeline.md).

## Shared vocabulary

- **EDH / EDB**: the 108-byte "extra data header" and the 56-byte "extra data block" nodes of a spatial tree (a BSP or bounding-volume tree). Terrain, collision2 and decals all use them; see [Spatial tree](SpatialTree.md).
- **Offset table**: positions inside the content that hold pointers, written by `BdfWriter.Mark()` in layout order.
- **Pointer**: a uint32 offset inside the content; `0xFFFFFFFF` means "none".
- **Raw height unit**: heights are stored as `uint16` in units of 1/128 of a world unit.
