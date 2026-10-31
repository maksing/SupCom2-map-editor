# `collision2.bdf` (collision mesh)

Code: `Assets/SC2/Runtime/BdfIO/Formats/Collision2/Collision2Bdf.cs`. Container: [Container](Container.md), padding `Align32`, 1 file, no string pool. The editor's generation, preview and custom mesh path are in the [Collision2 system document](../Systems/Collision2.md).

## Structure, in order

| Section | Size | Notes |
|---|---|---|
| CollisionHeader | 24 | |
| CollisionVertex[] | 12 each | position only: three floats; no UV or normal |
| Triangle[] | 12 each | three uint32 vertex indices |
| ExtraDataHeader | 108 | one for the whole map; see [Spatial tree](SpatialTree.md) |
| ExtraDataBlock[] | 56 each | the tree, same node layout as terrain, root at the header's root pointer |
| tail | 0 to 28 on vanilla | zero fill after the last node; see below |

## CollisionHeader (24 bytes)

| Offset | Size | Field | Notes |
|---:|---:|---|---|
| 0 | 4 | `Version` | 1 |
| 4 | 4 | vertex count | |
| 8 | 4 | vertex offset | pointer (24) |
| 12 | 4 | index count | triangle count times 3 |
| 16 | 4 | index offset | pointer |
| 20 | 4 | extra data offset | pointer to the EDH |

## Offset table

For a tree of N nodes, `2N + 13` entries (2103 on the vanilla map examined), in this order:

1. the vertex offset field (position 8) and the index offset field (position 16);
2. for each node k in order, its `Child1` (+48) and `Child2` (+52) fields;
3. the EDH's root pointer (+32 of the EDH), then the three filler pointers of each of the three filler triples (positions +44, +48, +52, +64, +68, +72, +84, +88, +92 of the EDH), ten entries in all;
4. the header's extra data offset field (position 20).

The node count is recovered as `(offsetTable.Length - 13) / 2`; counting from the data length drops the last node on 51 of 56 vanilla files.

## The tail

The model and the writer treat the end of the content as a fixed 28-byte `Tail`. **On the vanilla maps it is not fixed**: measured on all 56 files, what follows the last node is 0, 4, 8, 12, 16, 20, 24 or 28 bytes (8 bytes in 21 files, 24 in 13, 28 in only 5), always all zeros, and the content length is always a multiple of 32. So it is zero fill that brings the content to a 32-byte boundary, not a 28-byte structure.
The parser takes the last 28 bytes of the content as `Tail`; on a file whose real tail is shorter, those bytes start inside the last node. The editor generates collision2 from scratch with an all-zero tail (and writes an imported mesh's original bytes verbatim), so the exported files carry 28 zero bytes, which the format has no rule against; the writer does not reproduce the vanilla boundary alignment of the content length.

## Rules

- `EndVertex` and `StartPoly` of each node are stored encoded (`EndVertex - 1`, `StartPoly * 3`), see [Spatial tree](SpatialTree.md).
- The mesh covers the whole map; the vanilla convention is at most 2500 triangles, but the format has no such limit (counts are plain uint32), and the editor lets the budget go to 10 000.
- The cumulative vertex and index fields of the EDH are 0 here; collision2 has one tree, not one per layer.
