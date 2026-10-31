# `decals.bdf` and the decal atlases

Code: `Assets/SC2/Runtime/BdfIO/Formats/Decals/DecalsBdf.cs`, `DecalsSpatialBuilder.cs`; geometry in `MapDocument/Domain/Decals/SC2DecalGeometryBuilder.cs`; export in `SC2DecalsExportService`. Container: [Container](Container.md), padding `Align32`, **2 files** (the container's number-of-files field is 2). The editor side is in [Decals](../Systems/Decals.md).

The file is **one baked, already-projected, already atlas-packed triangle mesh covering every decal**, not a serialization of the individually authored decals (those, with position, orientation, scale and material name, are the game's editor-side `.decals.tdf` and `.decalmats.tdf` sources; the editor keeps its own authored placements in the document). The layout was inferred from real vanilla files and
cross-checked against the atlas rectangles of `decalmats.tdf`, not taken from a specification, so field names are the best current understanding.

## Structure, in order

| Section | Size | Notes |
|---|---|---|
| header | 16 | four int32 |
| diffuse atlas path | strlen + 1 | NUL-terminated, at offset 16 |
| normal atlas path | strlen + 1 | starts on the next 4-byte boundary |
| EDH + EDB spatial block | variable | starts on the next 4-byte boundary; absent when the map has no decals |
| zero padding | | to a 32-byte boundary |
| geometry sub-file | 20-byte header + vertices + indices | starts on a 32-byte boundary |

## Header (16 bytes, int32)

| Offset | Field | Notes |
|---:|---|---|
| 0 | `Version` | 3 |
| 4 | diffuse atlas path offset | 16 |
| 8 | normal atlas path offset | after the diffuse path, rounded up to a multiple of 4 |
| 12 | extra data offset | after the normal path, rounded up to a multiple of 4; **-1 when the map has no decals** |

All three offset fields are offset-table entries.

## Spatial block

The same 108-byte [EDH](SpatialTree.md) and 56-byte EDB nodes as terrain and collision2; only the vertex layout of the geometry differs. It is kept as raw bytes for a byte-identical passthrough. After an edit of the geometry, `DecalsSpatialBuilder.Rebuild` regenerates the tree and the geometry permutation it describes. The tree contains absolute pointers, so serialization **rebases** them by the change in start offset when the lengths of the atlas
paths move the block (`ExtraDataOffsetTable` lists the pointer fields, relative to the block).

## Geometry sub-file

Located by the **second uint32 of the container's trailing data** (the trailing data is 12 bytes written as `0`, geometry start, `0`); it is not found through the offset table, which marks pointer fields for relocation.

Header (20 bytes, five int32, offsets relative to the sub-file's start):

| Offset | Field | Notes |
|---:|---|---|
| 0 | version | 1 |
| 4 | vertex count | |
| 8 | vertex offset | 20 (right after this header); -1 with count 0 on an empty map |
| 12 | index count | three per triangle |
| 16 | index offset | `20 + vertexCount * 76`; -1 when empty |

The vertex and index offset fields are offset-table entries.

## Vertex (76 bytes)

Verified against the game shader's `decal.hlsl` input for static decals and against baked `ScaleOffset` values that match the atlas rectangles exactly.

| Offset | Size | Field | Meaning |
|---:|---:|---|---|
| 0 | 12 | `Position` | float3 |
| 12 | 12 | `TexCoord` | float3, projected clip-space decal-box coordinates |
| 24 | 16 | `ScaleOffset` | float4 = (rectW / atlasW, rectH / atlasH, rectX / atlasW, rectY / atlasH), the decal's rectangle in the atlas |
| 40 | 12 | `TangentXform0` | float3, tangent-space basis column (tangent) |
| 52 | 12 | `TangentXform1` | float3, basis column (binormal) |
| 64 | 12 | `TangentXform2` | float3, basis column (normal) |

## Indices

`int32[IndexCount]`, plain 32-bit indices, three per triangle, already triangulated.

## Empty maps

A map with no decals and no atlas path is vanilla's **64-byte stub**: both path offsets point at the single terminator at offset 16 and the extra data offset is -1. The editor keeps writing this stub when the original had one ([Decals](../Systems/Decals.md)).

## Atlases

The two atlas paths in the header are game paths. A vanilla atlas is referenced by its game path; a custom or generated atlas is copied to `maps/<Map>/decals/decals_*.dds` and referenced there, see [Export pipeline](../Systems/ExportPipeline.md).

## Rules

- The parser needs at least 8 bytes of trailing data to find the geometry; with fewer it returns a model with only the paths.
- The editor's serializer writes PC byte order only.
- Re-serializing an unmodified file is byte-identical as long as neither atlas path's length nor the geometry changed.
