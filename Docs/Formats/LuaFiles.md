# The three Lua files: `_scenario.lua`, `_save.lua`, `_script.lua`

Code: `Assets/SC2/Scripts/Core/SC2ObjectsExport.cs` (`RegenerateScenarioLua`, `RegenerateSaveLua`, `RegenerateScriptLua`), `SC2LuaBlocks.cs` (find and replace a block by brace matching), `SC2LobbySpawnMath.cs`, readers `SC2SaveLuaParser.cs`, `SC2SaveLuaBytecodeReader.cs`, `SC2LuaBytecodeValue.cs`, `SC2ObjectsImport.cs`, and `Runtime/KopiLua` (a vendored Lua 5.1 for reading bytecode). Systems: [Scenario and mission](../Systems/ScenarioAndMission.md), [Objects](../Systems/Objects.md), [Export pipeline](../Systems/ExportPipeline.md).
The files are written as text with `\r\n` line ends, under `maps/<Map>/` and again under `uncompiled/maps/<Map>/`.

**Two older writers exist and are not the live path**: `SC2SaveLuaWriter` and `SC2ScenarioLuaWriter` (in `Scripts`) rewrite one block from scene `SC2Marker` components. Their only caller is the editor-only `Editor/ExportWindow.cs`. The export the UI runs is `SC2ObjectsExport` above.

## How they are produced: patch or skeleton

An imported map keeps its original text (stored in the document as a passthrough blob) and only the **regenerated blocks** are replaced (`SC2LuaBlocks.FindBlock` locates a key such as `Markers = ` and its matching brace; `ReplaceBlockContent` swaps the content). A new map starts from a fixed skeleton. A real vanilla `_save.lua` is compiled Lua 5.1 bytecode on 55 of 56 maps and `_script.lua` is bytecode too; the editor reads
bytecode (KopiLua) but never writes it, and a `uncompiled/maps/` mirror carries readable source.

## `_scenario.lua`

A top-level `version = 3` followed by one `ScenarioInfo = { ... }` table (not wrapped in `Scenario = { }`).

Skeleton fields written for a new skirmish map: `devname`, `name` (the display name), `description`, `type = 'skirmish'`, `starts = true`, `preview`, `reverbPreset`, `size = {x, y}`, `map`, `save`, `script` (paths under `/maps/<sanitized name>/`), `norushradius`, `StartPositions`, and `Configurations.standard` with one team `FFA` whose `armies` list the army names, and `customprops['ExtraArmies'] = STRING( 'ARMY_EXTRA' )`.

What the editor regenerates on every export:

- the `name` field from the document's display name, also for an imported map;
- `StartPositions`, one `['ARMY_n'] = { x, y, z }` entry per army, from the **lobby start positions** (the minimap marker the user dragged) and falling back to the real marker position for an army with none; this block never changes `_save.lua`;
- `armies`, the list of army names in the order of the `ARMY_*` markers.

Facts measured on the vanilla files (skirmish versus campaign): skirmish-only `devname`, `x360_name`, `x360_maptype`, `StartPositions`; campaign-only `campaignID` and `UIDeactivate = true`; `norushradius` is a real positive value on skirmish maps and 0 on campaign maps; every army is listed in one FFA team, even on 4v4 and 8v8 maps. The editor deliberately does not generate
`x360_name`, `x360_maptype` and `mappack` (they would be invented content); the author fills them in. Campaign maps are always imported, never generated.

## `_save.lua`

A top-level `Scenario = { ... }` table with the keys, in this order: `Props`, `Areas`, `MasterChain`, `Chains`, `Armies` (no real file omits `Props`). Skeleton:

```
Scenario = {
    Props = { },
    Areas = { },
    MasterChain = { ['_MASTERCHAIN_'] = { Markers = { } } },
    Chains = { },
    Armies = { },
}
```

Regenerated blocks (the rest of an imported file is kept):

| Block | Content written |
|---|---|
| `Markers` of `_MASTERCHAIN_` | one `['<name>'] = { }` entry per marker: `type = STRING( '<resolved type>' )`, `prop`, `color` if set, `orientation` (`VECTOR3( x, y, z )` or a quaternion form), `position = VECTOR3( x, y, z )`, then the marker's preserved extra fields. **`ARMY_*` markers are here** (start positions) and are the ones kept out of `mapobjs.bdf`. No `adjacentTo`: real path graph connectivity lives only in `mapobjs.bdf` |
| `Areas` | `['<name>'] = { ['rectangle'] = RECTANGLE( x1, z1, x2, z2 ) }` per zone |
| `Chains` | `['<name>'] = { Markers = { "<marker name>", ... } }` per chain |
| `Armies` | one entry per `ARMY_*` marker, **plus one extra `ARMY_EXTRA`**, which `customprops.ExtraArmies` in the scenario file refers to |

An army entry: bare keys `personality`, `plans`, `faction`, `Economy = { mass, energy }`, `Alliances = { ['<other>'] = '<stance>' }`; bracketed keys `['color'] = FLOAT( n )` and `['Units'] = GROUP { Units = { ... } }`. This mixed style is what the game's own serializer produced; the importer accepts both styles per key. An army with no metadata gets the vanilla default (personality and plans blank, color 1, faction 0, zero economy, no alliances, no units)
so the Armies table, the start markers and the scenario file always agree. A unit entry is `['<name>'] = { type = '<blueprint>', ['Position'] = VECTOR3(...), ['Orientation'] = VECTOR3(...) }`; a group is `['<name>'] = GROUP { wreckage = true (if set), Units = { ... } }`, and groups nest through the same `Units` key at every depth.

## `_script.lua`

Almost entirely fixed engine boilerplate (confirmed against a real vanilla file): `OnPopulate` (initialize armies, camera defaults `MinSpinPitch`, `MaxZoomMult`, `FarZoom`, and the playable-area call), `OnStart` (the multiplayer music cue), and the five debug hotkey handlers `OnShiftF3`, `OnCtrlF3`, `OnShiftF4`, `OnCtrlF4`, `OnShiftF5`. The **only per-map line** is
`import('/lua/sim/ScenarioFramework.lua').SetPlayableArea('<area>')`, written only when an area is chosen (the Scripts tab, else the first area with role Playable); with none the line is omitted rather than called with an empty string. It is always regenerated from this skeleton, never patched from an original (an original is bytecode),
unless the mission editor is in use, in which case the Lua compiled from the mission graph replaces it ([Scenario and mission](../Systems/ScenarioAndMission.md)).

## Rules

- The order of army names in `StartPositions` and `armies` is the order of the `ARMY_*` markers; renaming an army renames its lobby position.
- `StartPositions`, `armies`, the `Armies` table and the `ARMY_*` markers must list exactly the same set of armies (checked against the vanilla corpus).
- Lua generated here must parse; the mission compiler's output is syntax-checked before it replaces the plain script.
- Blocks the editor does not regenerate must be copied through byte for byte.
