# Spatial tree: ExtraDataHeader (EDH) and ExtraDataBlock (EDB)

Code: `TerrainLayout` (sizes), `TerrainBdf.Parse`/`Serialize`, `TerrainBspBuilder.cs` (`EdbTreeBuilder`), `Collision2Bdf.cs`, `DecalsSpatialBuilder.cs`. Model: `TerrainLayer`, `EdbNode`. The same two records appear in [terrain](Terrain.md), [collision2](Collision2.md) and [decals](Decals.md).

## ExtraDataHeader (EDH), 108 bytes

One per layer in terrain, one per file in collision2 and decals.

| Offset | Size | Field | Notes |
|---:|---:|---|---|
| 0 | 4 | `BspVersion1` | 200 on every file examined |
| 4 | 4 | `BspVersion2` | 30 |
| 8 | 4 | `Hc` (float) | 200.0 |
| 12 | 4 | cumulative vertices | sum of the vertex counts of the previous layers; 0 in collision2 and not used there |
| 16 | 4 | cumulative indices | the same for indices |
| 20 | 12 | reserved | zeros |
| 32 | 4 | `rootBlockOffset` | pointer to the first EDB of this tree |
| 36 | 8 | reserved | zeros |
| 44 | 12 | filler 1 | three pointers, all `0xFFFFFFFF` |
| 56 | 8 | reserved | zeros |
| 64 | 12 | filler 2 | three pointers, all `0xFFFFFFFF` |
| 76 | 8 | reserved | zeros |
| 84 | 12 | filler 3 | three pointers, all `0xFFFFFFFF` |
| 96 | 12 | `MapCenter` | three floats |

Offsets 20 to 95 are written exactly as the collision2 serializer writes them (reserved zeros and the three filler triples). The 10 pointer fields of an EDH (the root and the nine filler pointers) are offset-table entries; the meaning of the filler pointers is unknown.

## ExtraDataBlock (EDB), 56 bytes

A node of a BSP or bounding-volume tree. Nodes of one tree are stored contiguously, root first; children are pointers to other nodes of the same tree.

| Offset | Size | Field | Notes |
|---:|---:|---|---|
| 0 | 12 | `BboxMin` | three floats |
| 12 | 12 | `BboxMax` | three floats |
| 24 | 4 | `IsLeaf` | 0 internal, 1 leaf |
| 28 | 4 | `IsLeaf` copy | duplicates the previous field |
| 32 | 4 | `StartVertex` | first vertex index of the leaf, local to its layer |
| 36 | 4 | `EndVertex - 1` | the **last valid** vertex index; the model stores `EndVertex = value + 1` |
| 40 | 4 | `StartPoly * 3` | first triangle index times 3; the model stores `StartPoly = value / 3` |
| 44 | 4 | `PolyCount` | triangles in the node |
| 48 | 4 | `Child1` | pointer, `0xFFFFFFFF` = none |
| 52 | 4 | `Child2` | pointer, `0xFFFFFFFF` = none |

The two child fields are the only pointer-valued fields of the record, so a tree of N nodes contributes exactly `2N` offset-table entries. Collision2 uses that to find N: `(offsetTable.Length - 13) / 2`. Counting from the data length instead drops the last node on 51 of 56 vanilla files.

## Building a tree

`EdbTreeBuilder` follows the reference exporter: build the BSP tree over the input triangles first (a triangle crossing the split plane is **duplicated** into both branches), then walk it post-order and repack each leaf's geometry into new local vertex and triangle lists, deduplicating vertices by a rounded key (position, UV1, UV2, normal, seven significant digits). Defaults: at least 200 polygons per leaf, depth at most 30.
It was verified byte-for-byte against a real vanilla terrain file. Consequence: a layer's vertex and triangle counts can change after a rebuild, and every later layer's cumulative counts and triangle indices shift.

## Rules

- `EndVertex` and `StartPoly` are stored encoded; the parser and writer decode and encode them, nothing else must.
- A tree must be rebuilt after any change to the geometry of a layer (`RebuildLayerBsp`, `RebuildAllBsp`), before serializing.
- Decals keep the block raw for byte-identical passthrough; a changed geometry goes through `DecalsSpatialBuilder`, whose pointers are absolute offsets and so depend on the exact place the block ends up (the length of the two atlas paths).
