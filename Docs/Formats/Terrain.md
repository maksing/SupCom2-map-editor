# `terrain.bdf`

Code: `Assets/SC2/Runtime/BdfIO/Formats/Terrain/` (`TerrainBdf.cs` parse and serialize, `TerrainModel.cs`, `TerrainLayout.cs` sizes and TBN packing, `TerrainBspBuilder.cs`). Written by the [export pipeline](../Systems/ExportPipeline.md) from `SC2TerrainLayerBuilder`; read by the [import pipeline](../Systems/ImportPipeline.md). Container: [Container](Container.md), padding `Align32Min8`, 2 sub-files.

## File structure, in order

| Section | Size | Where |
|---|---|---|
| FileHeader | 20 | offset 0 |
| ExtraDataHeader per layer | 108 each | offset 20 (`EdhOffset`, always 20) |
| EDB forest per layer | 56 per node | one tree per layer, trees stored one after another |
| MaterialData | 84 each | `MaterialOffset` |
| ShaderSetupItem | 8 each | after the materials, per material (none if the material has 0) |
| string pool | variable | after the shader setup items |
| GeometryHeader | 20 | `GeometryHeaderStart` (also in the container's trailing data) |
| vertices | 32 each | `GeometryHeaderStart + 20` |
| triangles | 12 each | `GeometryHeaderStart + indexOffset` (stored explicitly in the geometry header) |

Everything up to the string pool has positions computable from counts alone (`TerrainLayout.Compute`), so the offset table and the data length do not depend on the string order.

## FileHeader (20 bytes)

| Offset | Field |
|---:|---|
| 0 | `Version` (int, usually 6) |
| 4 | layer count |
| 8 | EDH offset (20) |
| 12 | material count |
| 16 | material offset |

## Layer (EDH + tree)

One layer per pair that carries geometry. The 108-byte EDH and the 56-byte tree nodes are described in [Spatial tree](SpatialTree.md). `CumulativeVertices` and `CumulativeIndices` of a layer are the sums over the previous layers: all layers share one vertex list and one triangle list, and a layer's triangles index into that shared list.
The root of layer i's tree is at EDH offset 32 of layer i; the number of nodes of a tree is found from the distance to the next tree (or to the material section).

## MaterialData (84 bytes)

| Offset | Size | Field |
|---:|---:|---|
| 0 | 4 | name (pointer) |
| 4 | 4 | shader file (pointer, optional) |
| 8 | 4 | shader prefix (pointer, optional) |
| 12 | 4 | shader setup item count |
| 16 | 4 | shader setup items (offset) |
| 20 | 4 | state (pointer, optional) |
| 24 | 8 | texture scroll (two floats) |
| 32 | 52 | 13 texture path pointers, each optional |

An optional pointer is `0xFFFFFFFF` when the field is absent. **The 13 texture slots**, in order: Diffuse1, Diffuse2, Normal1, Normal2, Normal3, Normal4, Mask1, Mask2, Ramp, Texture6, RNM Volume, Environment, Texture8 (the model's list; the editor's slot names and what it writes into each are in [materials and pairs](../Systems/MaterialsAndPairs.md)).

## ShaderSetupItem (8 bytes)

Two pointers: a name and a value (strings in the pool). The pair is read by the game's shader setup; what each name means is not documented in the code beyond being copied through. Count and position come from the material.

## GeometryHeader (20 bytes, five uint32)

| Offset | Field | Notes |
|---:|---|---|
| 0 | unknown | 3 on every file examined (a version?) |
| 4 | vertex count | |
| 8 | vertex data offset | 20, relative to the header |
| 12 | index count | three per triangle |
| 16 | index data offset | relative to the header, a large value |

The header's two pointer fields (+8 and +16) are the last two offset-table entries, which is how the parser finds it: `GeometryHeaderStart = OffsetTable[last] - 16`.

## Vertex (32 bytes)

| Offset | Size | Field | Encoding |
|---:|---:|---|---|
| 0 | 12 | position | three floats, world 0 to 1024 |
| 12 | 4 | UV1 (U, V) | two half-floats; large values, tiling |
| 16 | 4 | UV2 (U, V) | two int16, value / 32767 gives 0 to 1; the lookup into the blend and lighting masks |
| 20 | 4 | normal | packed vector |
| 24 | 4 | tangent | packed vector |
| 28 | 4 | binormal | packed vector |

**Packed vector**: three signed 10-bit fields in bits 0 to 9, 10 to 19 and 20 to 29 (bits 30 and 31 unused), each divided by 51.1. Measured on all 56 vanilla maps (about 9.07 million vertices): decoded this way the vectors are unit length (1.0014, 1.0013, 1.0013) and mutually orthogonal, the normal matches a winding-based normal to a median 0.51 degrees, the tangent matches a UV1-derived tangent to
0.43 degrees; read as float32 the same words would have no negative values over 27 million components, which is impossible for real normals. The game's `terrain.hlsl` declares exactly these three attributes. **Writing plain float32 here gives a garbage tangent basis in game**: diffuse is unaffected (it samples the normal map), specular and environment reflections get random highlights.
Packing clamps each component to +-511 so an overflow cannot wrap to the opposite sign. A vertex parsed from a file keeps its three raw words so that re-serializing is byte-identical; a built vertex is packed at serialize time.

## Triangle (12 bytes)

Three uint32 vertex indices into the shared vertex list.

## Round trip guarantees (`TerrainBdf.Serialize`)

- The offset table equals the original positionally (length and every entry).
- The data length equals the original (same set of strings, no deduplication).
- Bytes before the material section and from the geometry header to the end equal the original.
- Bytes inside the material section may differ (string order gives different pointer values) but a re-parse is semantically identical.

## What the editor writes

Layers are built per pair by `SC2TerrainLayerBuilder` (simplified by the pair's ratio), UV1 by `SC2TerrainUv1Unwrap`, UV2 for the mask lookup; the background adds its own layers; imported terrain objects are merged; then `RebuildAllBsp` and `Serialize`. The details and the path rules are in the [export pipeline](../Systems/ExportPipeline.md) and [simplification](../Systems/Simplification.md).
