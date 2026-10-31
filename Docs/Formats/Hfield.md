# `hfield.bdf` (height field and terrain types)

Code: `Assets/SC2/Runtime/BdfIO/Formats/Hfield/HfieldBdf.cs`. Container: [Container](Container.md), padding `MinPad32`, 1 file, no string pool, 2 offset-table entries. Produced by `SC2HfieldProjectionService` from the **final exported terrain model**; read by [import](../Systems/ImportPipeline.md). The editor's side is in [export pipeline](../Systems/ExportPipeline.md) and [costs](../Systems/Costs.md) (the water mask of five-block costs is taken from this file).

## HFieldHeader (28 bytes)

| Offset | Size | Field | Notes |
|---:|---:|---|---|
| 0 | 4 | `Version` | 2 on all 56 vanilla maps |
| 4 | 4 | `MapX` | number of height samples along X |
| 8 | 4 | `MapY` | number of height samples along Y |
| 12 | 4 | height sample count | must equal `MapX * MapY` |
| 16 | 4 | heightmap offset | pointer, offset-table entry; 28 when written by the editor |
| 20 | 4 | terrain type count | must equal `(MapX - 1) * (MapY - 1)` |
| 24 | 4 | terrain types offset | pointer, offset-table entry |

## Heightmap

`uint16[MapX * MapY]`, byte order per platform, immediately after the header. **A raw height unit is 1/128 of a world unit.** The editor projects the grid from the final exported terrain model, after simplification and merging.

## Terrain types

One surface type id per **cell** (`(MapX - 1) * (MapY - 1)` values). The element size in the file is detected on read (**uint8 in all 56 vanilla files**; the editor's projection sets 1 and so writes uint8): if the bytes left after the offset equal the count, it is **uint8** (a raw image); otherwise **uint16**. `TerrainTypesElementSize` remembers which; serialization writes uint16 unless the model asks for 1 and every value fits in a byte.
What a type id means (land, water, and the game's other surface types) is decided by the [costs](../Systems/Costs.md) terrain-types table, not by this file.

## Offset table

Two entries, in this order: the heightmap offset field (position 16) and the terrain types offset field (position 24).

## Rules

- A writer rejects a heightmap or terrain-type array whose length does not match the sizes; the sizes are the contract.
- Anything derived from heights (costs, collision2) must be computed from the same exported model, or the files disagree.
